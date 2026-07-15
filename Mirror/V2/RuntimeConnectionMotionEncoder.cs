using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Connection-local 20 Hz motion frame builder. The caller owns sampling,
    /// while this class owns coalescing, ACKed-membership filtering and binary
    /// encoding for one immutable store generation.
    /// </summary>
    public class RuntimeConnectionMotionEncoder
    {
        private readonly RuntimeConnectionStoreReplicationState _connectionState;
        private readonly RuntimeReplicationPolicyRegistry _policies;
        private readonly uint[] _motionComponentTypeIds;
        private readonly RuntimeConnectionMotionCoalescer _coalescer;
        private readonly RuntimeMotionFrameCodec _codec = new();

        public readonly NetStoreRef Store;

        public RuntimeConnectionMotionEncoder(
            RuntimeConnectionStoreReplicationState connectionState,
            RuntimeReplicationPolicyRegistry policies,
            IReadOnlyList<uint> motionComponentTypeIds,
            double startTimeSeconds = 0d)
        {
            _connectionState = connectionState ?? throw new ArgumentNullException(nameof(connectionState));
            _policies = policies ?? throw new ArgumentNullException(nameof(policies));
            if (motionComponentTypeIds == null || motionComponentTypeIds.Count == 0)
                throw new ArgumentException("At least one motion component type id is required.", nameof(motionComponentTypeIds));

            Store = connectionState.Store;
            _motionComponentTypeIds = CopyAndValidatePolicies(motionComponentTypeIds);
            _coalescer = new RuntimeConnectionMotionCoalescer(Store, startTimeSeconds);
        }

        public RuntimeMotionSubmitResult Submit(uint simulationTick, IReadOnlyList<RuntimeMotionSample> samples)
        {
            ValidateSamplePolicies(samples);
            return _coalescer.Submit(simulationTick, samples);
        }

        public RuntimeMotionFrameBuildResult TryEncode(
            double nowSeconds,
            uint simulationTick,
            out byte[] payload,
            out RuntimeMotionFrame frame)
        {
            payload = Array.Empty<byte>();
            var result = _coalescer.TryBuildFrame(nowSeconds, simulationTick, IsMotionEligible, out frame);
            if (result != RuntimeMotionFrameBuildResult.Built)
                return result;

            payload = _codec.Encode(frame);
            if (payload.Length > RuntimeMotionProtocol.MAX_UNRELIABLE_PAYLOAD_BYTES)
            {
                throw new InvalidOperationException(
                    $"Motion encoder produced {payload.Length} bytes, exceeding unreliable transport budget {RuntimeMotionProtocol.MAX_UNRELIABLE_PAYLOAD_BYTES}.");
            }
            return RuntimeMotionFrameBuildResult.Built;
        }

        public bool Forget(NetObjectRef value) => _coalescer.Forget(value);

        private bool IsMotionEligible(NetObjectRef value)
        {
            for (var i = 0; i < _motionComponentTypeIds.Length; i++)
            {
                if (RuntimeConnectionMotionEligibility.IsMotionEligible(
                        _connectionState,
                        value,
                        _motionComponentTypeIds[i],
                        _policies))
                {
                    return true;
                }
            }

            return false;
        }

        private uint[] CopyAndValidatePolicies(IReadOnlyList<uint> values)
        {
            var result = new uint[values.Count];
            var seen = new HashSet<uint>();
            for (var i = 0; i < values.Count; i++)
            {
                var componentTypeId = values[i];
                if (!seen.Add(componentTypeId))
                    throw new InvalidOperationException($"Motion component type id {componentTypeId} is duplicated.");
                if (_policies.GetRequired(componentTypeId) != RuntimeReplicationPolicy.UnreliableState)
                {
                    throw new InvalidOperationException(
                        $"Motion component type id {componentTypeId} must use {RuntimeReplicationPolicy.UnreliableState} policy.");
                }

                result[i] = componentTypeId;
            }

            return result;
        }

        private void ValidateSamplePolicies(IReadOnlyList<RuntimeMotionSample> samples)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));

            for (var sampleIndex = 0; sampleIndex < samples.Count; sampleIndex++)
            {
                var sample = samples[sampleIndex];
                if (sample.Components == null || sample.Components.Count == 0)
                {
                    throw new InvalidOperationException(
                        $"Motion sample '{sample.Object}' requires at least one component state.");
                }

                for (var componentIndex = 0; componentIndex < sample.Components.Count; componentIndex++)
                {
                    var componentTypeId = sample.Components[componentIndex].ComponentTypeId;
                    if (!IsConfiguredMotionComponent(componentTypeId))
                    {
                        throw new InvalidOperationException(
                            $"Motion component type id {componentTypeId} on object '{sample.Object}' is not registered "
                            + "in this encoder's motion component allowlist.");
                    }
                    if (_policies.GetRequired(componentTypeId) != RuntimeReplicationPolicy.UnreliableState)
                    {
                        throw new InvalidOperationException(
                            $"Motion component type id {componentTypeId} on object '{sample.Object}' must use "
                            + $"{RuntimeReplicationPolicy.UnreliableState} policy.");
                    }
                }
            }
        }

        private bool IsConfiguredMotionComponent(uint componentTypeId)
        {
            for (var i = 0; i < _motionComponentTypeIds.Length; i++)
            {
                if (_motionComponentTypeIds[i] == componentTypeId)
                    return true;
            }
            return false;
        }
    }
}
