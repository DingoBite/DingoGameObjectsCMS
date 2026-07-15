using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    [Serializable]
    public struct RuntimeCommandEnvelope
    {
        public uint CommandTypeId;
        public ulong ClientSequence;
        public uint ExpectedStoreGeneration;
        public byte[] Payload;
    }

    public readonly struct RuntimeCommandAuthority
    {
        public readonly int SenderConnectionId;
        public readonly bool SessionReady;
        public readonly uint ExpectedStoreGeneration;
        private readonly Func<uint, bool> _storeGenerationValidator;
        private readonly Func<NetObjectRef, bool> _objectMembershipValidator;

        public RuntimeCommandAuthority(
            int senderConnectionId,
            bool sessionReady,
            Func<uint, bool> storeGenerationValidator,
            uint expectedStoreGeneration = 0,
            Func<NetObjectRef, bool> objectMembershipValidator = null)
        {
            SenderConnectionId = senderConnectionId;
            SessionReady = sessionReady;
            ExpectedStoreGeneration = expectedStoreGeneration;
            _storeGenerationValidator = storeGenerationValidator;
            _objectMembershipValidator = objectMembershipValidator;
        }

        public RuntimeCommandAuthority WithExpectedStoreGeneration(uint generation)
        {
            return new RuntimeCommandAuthority(
                SenderConnectionId,
                SessionReady,
                _storeGenerationValidator,
                generation,
                _objectMembershipValidator);
        }

        public bool AllowsStoreGeneration(uint generation)
        {
            return generation != 0 && _storeGenerationValidator != null && _storeGenerationValidator(generation);
        }

        public bool CanAccessObject(in NetObjectRef target)
        {
            return target.IsValid
                   && target.Store.StoreGeneration == ExpectedStoreGeneration
                   && _objectMembershipValidator != null
                   && _objectMembershipValidator(target);
        }
    }

    public readonly struct RuntimeCommandRatePolicy
    {
        public readonly double MessagesPerSecond;
        public readonly int Burst;

        public RuntimeCommandRatePolicy(double messagesPerSecond, int burst)
        {
            if (messagesPerSecond <= 0)
                throw new ArgumentOutOfRangeException(nameof(messagesPerSecond));
            if (burst <= 0)
                throw new ArgumentOutOfRangeException(nameof(burst));
            MessagesPerSecond = messagesPerSecond;
            Burst = burst;
        }
    }

    public enum RuntimeCommandRejectCode : byte
    {
        None = 0,
        SessionNotReady = 1,
        InvalidSequence = 2,
        UnknownCommand = 3,
        InvalidStoreGeneration = 4,
        PayloadTooLarge = 5,
        RateLimited = 6,
        MalformedPayload = 7,
        ValidationFailed = 8,
        HandlerFailed = 9,
    }

    public readonly struct RuntimeCommandResult
    {
        public readonly ulong ClientSequence;
        public readonly RuntimeCommandRejectCode RejectCode;

        public RuntimeCommandResult(ulong clientSequence, RuntimeCommandRejectCode rejectCode)
        {
            ClientSequence = clientSequence;
            RejectCode = rejectCode;
        }

        public bool Accepted => RejectCode == RuntimeCommandRejectCode.None;
    }

    public interface IRuntimeCommandCodec<TCommand>
    {
        bool TryRead(byte[] payload, out TCommand command);
        byte[] Write(in TCommand command);
    }

    public delegate RuntimeCommandRejectCode RuntimeCommandValidator<TCommand>(in TCommand command, in RuntimeCommandAuthority authority);
    public delegate void RuntimeCommandHandler<TCommand>(in TCommand command, in RuntimeCommandAuthority authority);

    public sealed class RuntimeCommandRegistry
    {
        private interface IRegistration
        {
            int PayloadLimit { get; }
            RuntimeCommandRatePolicy RatePolicy { get; }
            RuntimeCommandRejectCode Dispatch(byte[] payload, in RuntimeCommandAuthority authority);
        }

        private sealed class Registration<TCommand> : IRegistration
        {
            private readonly IRuntimeCommandCodec<TCommand> _codec;
            private readonly RuntimeCommandValidator<TCommand> _validator;
            private readonly RuntimeCommandHandler<TCommand> _handler;

            public int PayloadLimit { get; }
            public RuntimeCommandRatePolicy RatePolicy { get; }

            public Registration(
                IRuntimeCommandCodec<TCommand> codec,
                RuntimeCommandValidator<TCommand> validator,
                RuntimeCommandHandler<TCommand> handler,
                int payloadLimit,
                RuntimeCommandRatePolicy ratePolicy)
            {
                _codec = codec ?? throw new ArgumentNullException(nameof(codec));
                _validator = validator ?? throw new ArgumentNullException(nameof(validator));
                _handler = handler ?? throw new ArgumentNullException(nameof(handler));
                PayloadLimit = payloadLimit;
                RatePolicy = ratePolicy;
            }

            public RuntimeCommandRejectCode Dispatch(byte[] payload, in RuntimeCommandAuthority authority)
            {
                if (!_codec.TryRead(payload, out var command))
                    return RuntimeCommandRejectCode.MalformedPayload;

                var validation = _validator(command, authority);
                if (validation != RuntimeCommandRejectCode.None)
                    return validation == RuntimeCommandRejectCode.ValidationFailed
                        ? validation
                        : RuntimeCommandRejectCode.ValidationFailed;

                try
                {
                    _handler(command, authority);
                    return RuntimeCommandRejectCode.None;
                }
                catch
                {
                    return RuntimeCommandRejectCode.HandlerFailed;
                }
            }
        }

        private struct RateState
        {
            public double Tokens;
            public double LastTimestamp;
            public bool Initialized;
        }

        private readonly Dictionary<uint, IRegistration> _registrations = new();
        private readonly Dictionary<int, ulong> _lastSequenceByConnection = new();
        private readonly Dictionary<(int ConnectionId, uint CommandTypeId), RateState> _rateByConnectionAndType = new();

        public void Register<TCommand>(
            uint commandTypeId,
            IRuntimeCommandCodec<TCommand> codec,
            RuntimeCommandValidator<TCommand> validator,
            RuntimeCommandHandler<TCommand> handler,
            int payloadLimit,
            RuntimeCommandRatePolicy ratePolicy)
        {
            if (commandTypeId == 0)
                throw new ArgumentOutOfRangeException(nameof(commandTypeId));
            if (payloadLimit <= 0)
                throw new ArgumentOutOfRangeException(nameof(payloadLimit));
            if (!_registrations.TryAdd(
                    commandTypeId,
                    new Registration<TCommand>(codec, validator, handler, payloadLimit, ratePolicy)))
                throw new InvalidOperationException($"Command type id '{commandTypeId}' is already registered.");
        }

        public RuntimeCommandResult Dispatch(in RuntimeCommandEnvelope envelope, in RuntimeCommandAuthority authority, double nowSeconds)
        {
            if (!authority.SessionReady)
                return Reject(envelope, RuntimeCommandRejectCode.SessionNotReady);

            var expectedSequence = _lastSequenceByConnection.TryGetValue(authority.SenderConnectionId, out var last)
                ? last + 1
                : 1UL;
            if (envelope.ClientSequence != expectedSequence)
                return Reject(envelope, RuntimeCommandRejectCode.InvalidSequence);

            // Once a sequence is syntactically accepted it is consumed even if validation fails.
            // This prevents replaying an expensive invalid command indefinitely.
            _lastSequenceByConnection[authority.SenderConnectionId] = envelope.ClientSequence;

            if (!_registrations.TryGetValue(envelope.CommandTypeId, out var registration))
                return Reject(envelope, RuntimeCommandRejectCode.UnknownCommand);
            var envelopeAuthority = authority.WithExpectedStoreGeneration(envelope.ExpectedStoreGeneration);
            if (!envelopeAuthority.AllowsStoreGeneration(envelope.ExpectedStoreGeneration))
                return Reject(envelope, RuntimeCommandRejectCode.InvalidStoreGeneration);

            var payload = envelope.Payload ?? Array.Empty<byte>();
            if (payload.Length > registration.PayloadLimit)
                return Reject(envelope, RuntimeCommandRejectCode.PayloadTooLarge);
            if (!TryConsumeRate(authority.SenderConnectionId, envelope.CommandTypeId, registration.RatePolicy, nowSeconds))
                return Reject(envelope, RuntimeCommandRejectCode.RateLimited);

            return new RuntimeCommandResult(envelope.ClientSequence, registration.Dispatch(payload, envelopeAuthority));
        }

        public void RemoveConnection(int connectionId)
        {
            _lastSequenceByConnection.Remove(connectionId);

            List<(int ConnectionId, uint CommandTypeId)> remove = null;
            foreach (var key in _rateByConnectionAndType.Keys)
            {
                if (key.ConnectionId != connectionId)
                    continue;
                remove ??= new List<(int ConnectionId, uint CommandTypeId)>();
                remove.Add(key);
            }

            if (remove == null)
                return;
            for (var i = 0; i < remove.Count; i++)
                _rateByConnectionAndType.Remove(remove[i]);
        }

        private bool TryConsumeRate(
            int connectionId,
            uint commandTypeId,
            RuntimeCommandRatePolicy policy,
            double nowSeconds)
        {
            var key = (connectionId, commandTypeId);
            _rateByConnectionAndType.TryGetValue(key, out var state);

            if (!state.Initialized)
            {
                state.Initialized = true;
                state.Tokens = policy.Burst;
                state.LastTimestamp = nowSeconds;
            }
            else
            {
                var elapsed = Math.Max(0d, nowSeconds - state.LastTimestamp);
                state.Tokens = Math.Min(policy.Burst, state.Tokens + elapsed * policy.MessagesPerSecond);
                state.LastTimestamp = nowSeconds;
            }

            if (state.Tokens < 1d)
            {
                _rateByConnectionAndType[key] = state;
                return false;
            }

            state.Tokens -= 1d;
            _rateByConnectionAndType[key] = state;
            return true;
        }

        private static RuntimeCommandResult Reject(in RuntimeCommandEnvelope envelope, RuntimeCommandRejectCode code)
        {
            return new RuntimeCommandResult(envelope.ClientSequence, code);
        }
    }
}
