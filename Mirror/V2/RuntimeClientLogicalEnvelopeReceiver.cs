using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public delegate RuntimeSessionHandshakeResult RuntimeSessionStoreAuthorization(ulong sessionId, in NetStoreRef store);
    public delegate bool RuntimeClientBaselineApply(in RuntimeClientBaselineEnvelope baseline);
    public delegate bool RuntimeClientDeltaApply(in RuntimeClientDeltaEnvelope delta);

    public enum RuntimeClientReceiveResultKind : byte
    {
        Accepted = 1,
        Buffered = 2,
        BaselineApplied = 3,
        DeltaApplied = 4,
        Duplicate = 5,
        Stale = 6,
        Superseded = 7,
        Rejected = 8,
        ResyncRequested = 9,
        WaitingForBaseline = 10,
    }

    public enum RuntimeClientResyncReason : byte
    {
        BaselineInvalid = 1,
        BaselineTimedOut = 2,
        BaselineApplyFailed = 3,
        DeltaGapTimedOut = 4,
        DeltaBufferOverflow = 5,
        DeltaInvalid = 6,
        DeltaApplyFailed = 7,
    }

    public readonly struct RuntimeClientReceiveResult
    {
        public readonly RuntimeClientReceiveResultKind Kind;
        public readonly RuntimeProtocolRejectCode RejectCode;
        public readonly int AppliedDeltaCount;
        public readonly ulong LastAppliedSequence;

        public RuntimeClientReceiveResult(
            RuntimeClientReceiveResultKind kind,
            RuntimeProtocolRejectCode rejectCode,
            int appliedDeltaCount,
            ulong lastAppliedSequence)
        {
            Kind = kind;
            RejectCode = rejectCode;
            AppliedDeltaCount = appliedDeltaCount;
            LastAppliedSequence = lastAppliedSequence;
        }
    }

    public readonly struct RuntimeClientResyncRequest
    {
        public readonly ulong SessionId;
        public readonly NetStoreRef Store;
        public readonly ulong BaselineId;
        public readonly ulong ExpectedDeliverySequence;
        public readonly RuntimeClientResyncReason Reason;

        public RuntimeClientResyncRequest(
            ulong sessionId,
            NetStoreRef store,
            ulong baselineId,
            ulong expectedDeliverySequence,
            RuntimeClientResyncReason reason)
        {
            SessionId = sessionId;
            Store = store;
            BaselineId = baselineId;
            ExpectedDeliverySequence = expectedDeliverySequence;
            Reason = reason;
        }

#if MIRROR
        public RtStoreResyncRequest ToWireRequest()
        {
            return new RtStoreResyncRequest
            {
                SessionId = SessionId,
                Store = Store,
                BaselineId = BaselineId,
                ExpectedDeliverySequence = ExpectedDeliverySequence,
            };
        }
#endif
    }

    public readonly struct RuntimeClientBaselineEnvelope
    {
        public readonly ulong SessionId;
        public readonly NetStoreRef Store;
        public readonly ulong BaselineId;
        public readonly ulong DeliverySequence;
        public readonly ulong StoreRevision;
        public readonly byte[] Payload;

        public RuntimeClientBaselineEnvelope(
            ulong sessionId,
            NetStoreRef store,
            ulong baselineId,
            ulong deliverySequence,
            ulong storeRevision,
            byte[] payload)
        {
            SessionId = sessionId;
            Store = store;
            BaselineId = baselineId;
            DeliverySequence = deliverySequence;
            StoreRevision = storeRevision;
            Payload = payload ?? Array.Empty<byte>();
        }
    }

    public class RuntimeClientDeltaEnvelope
    {
        private readonly byte[] _payload;

        public readonly RuntimeStoreDeltaKind Kind;
        public readonly ulong SessionId;
        public readonly NetStoreRef Store;
        public readonly ulong BaselineId;
        public readonly ulong DeliverySequence;
        public readonly ulong FromRevision;
        public readonly ulong ToRevision;

        public byte[] Payload => _payload.Length == 0 ? Array.Empty<byte>() : (byte[])_payload.Clone();
        public int PayloadBytes => _payload.Length;

        public RuntimeClientDeltaEnvelope(
            ulong sessionId,
            NetStoreRef store,
            ulong baselineId,
            ulong deliverySequence,
            ulong fromRevision,
            ulong toRevision,
            byte[] payload,
            RuntimeStoreDeltaKind kind = RuntimeStoreDeltaKind.Mutation)
        {
            Kind = kind;
            SessionId = sessionId;
            Store = store;
            BaselineId = baselineId;
            DeliverySequence = deliverySequence;
            FromRevision = fromRevision;
            ToRevision = toRevision;
            _payload = payload == null || payload.Length == 0 ? Array.Empty<byte>() : (byte[])payload.Clone();
        }

        public bool HasSameLogicalContents(RuntimeClientDeltaEnvelope other)
        {
            if (other == null
                || SessionId != other.SessionId
                || Kind != other.Kind
                || Store != other.Store
                || BaselineId != other.BaselineId
                || DeliverySequence != other.DeliverySequence
                || FromRevision != other.FromRevision
                || ToRevision != other.ToRevision
                || _payload.Length != other._payload.Length)
            {
                return false;
            }

            var difference = 0;
            for (var i = 0; i < _payload.Length; i++)
            {
                difference |= _payload[i] ^ other._payload[i];
            }

            return difference == 0;
        }

#if MIRROR
        public static RuntimeClientDeltaEnvelope FromWire(in RtStoreDelta delta)
        {
            return new RuntimeClientDeltaEnvelope(
                delta.SessionId,
                delta.Store,
                delta.BaselineId,
                delta.DeliverySequence,
                delta.FromRevision,
                delta.ToRevision,
                delta.Payload,
                delta.Kind);
        }
#endif
    }

    /// <summary>
    /// Per-session, per-store reliable receive state. The baseline apply delegate
    /// and delta apply delegates must provide atomic commit semantics: false means
    /// that no replica state was published. Deltas are never invoked before a
    /// successful baseline apply.
    /// </summary>
    public class RuntimeClientLogicalEnvelopeReceiver
    {
        public const double DEFAULT_GAP_TIMEOUT_SECONDS = 2d;

        private readonly RuntimeSessionStoreAuthorization _authorize;
        private readonly RuntimeClientBaselineApply _applyBaseline;
        private readonly RuntimeClientDeltaApply _applyDelta;
        private readonly Action<RuntimeClientResyncRequest> _requestResync;
        private readonly RuntimeBaselineChunkAssembler _baselineAssembler = new();
        private readonly SortedDictionary<ulong, RuntimeClientDeltaEnvelope> _pendingDeltas = new();
        private readonly int _maxPendingEnvelopeCount;
        private readonly int _maxPendingBytes;
        private readonly double _gapTimeoutSeconds;

        private RuntimeBaselineChunk _activeBaselineHeader;
        private ulong _currentBaselineId;
        private ulong _appliedBaselineId;
        private int _pendingBytes;
        private double _baselineStartedAt;
        private double _gapStartedAt;
        private ulong _resyncExpectedSequence;
        private bool _hasGapTimer;
        private bool _resyncRequested;
        private bool _resyncRequiresNewBaseline;

        public readonly ulong SessionId;
        public readonly NetStoreRef Store;

        public ulong CurrentBaselineId => _currentBaselineId;
        public ulong AppliedBaselineId => _appliedBaselineId;
        public ulong LastAppliedSequence { get; private set; }
        public ulong LastAppliedRevision { get; private set; }
        public int PendingDeltaCount => _pendingDeltas.Count;
        public int PendingDeltaBytes => _pendingBytes;
        public bool HasActiveBaselineTransfer => _baselineAssembler.IsActive;
        public bool NeedsResync => _resyncRequested;

        public RuntimeClientLogicalEnvelopeReceiver(
            ulong sessionId,
            NetStoreRef store,
            RuntimeSessionStoreAuthorization authorize,
            RuntimeClientBaselineApply applyBaseline,
            RuntimeClientDeltaApply applyDelta,
            Action<RuntimeClientResyncRequest> requestResync,
            int maxPendingEnvelopeCount = RuntimeProtocolV2.MAX_PENDING_ENVELOPES,
            int maxPendingBytes = RuntimeProtocolV2.MAX_PENDING_ENVELOPE_BYTES,
            double gapTimeoutSeconds = DEFAULT_GAP_TIMEOUT_SECONDS)
        {
            if (sessionId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            }

            if (!store.IsValid)
            {
                throw new ArgumentException("Client receiver requires a valid store reference.", nameof(store));
            }

            if (maxPendingEnvelopeCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPendingEnvelopeCount));
            }

            if (maxPendingBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPendingBytes));
            }

            if (gapTimeoutSeconds <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(gapTimeoutSeconds));
            }

            SessionId = sessionId;
            Store = store;
            _authorize = authorize ?? throw new ArgumentNullException(nameof(authorize));
            _applyBaseline = applyBaseline ?? throw new ArgumentNullException(nameof(applyBaseline));
            _applyDelta = applyDelta ?? throw new ArgumentNullException(nameof(applyDelta));
            _requestResync = requestResync ?? throw new ArgumentNullException(nameof(requestResync));
            _maxPendingEnvelopeCount = maxPendingEnvelopeCount;
            _maxPendingBytes = maxPendingBytes;
            _gapTimeoutSeconds = gapTimeoutSeconds;
        }

        public RuntimeClientReceiveResult ReceiveBaselineChunk(in RuntimeBaselineChunk chunk, double nowSeconds)
        {
            var authorization = Authorize(chunk.SessionId, chunk.Store);
            if (!authorization.Accepted)
            {
                return Result(RuntimeClientReceiveResultKind.Rejected, authorization.RejectCode);
            }

            if (chunk.BaselineId < _currentBaselineId)
            {
                return Result(RuntimeClientReceiveResultKind.Stale);
            }

            if (_resyncRequested
                && !_resyncRequiresNewBaseline
                && chunk.BaselineId == _currentBaselineId
                && _appliedBaselineId != _currentBaselineId)
            {
                // A corrupt/timed-out transfer may be retransmitted with the
                // same logical baseline id. Resetting the assembler is safe:
                // no state from this baseline has been published yet.
                BeginSupersedingBaseline(chunk, nowSeconds);
            }

            if (chunk.BaselineId == _appliedBaselineId && _currentBaselineId == _appliedBaselineId)
            {
                return Result(RuntimeClientReceiveResultKind.Duplicate);
            }

            var hadBaseline = _currentBaselineId != 0;
            var beginsTransfer = chunk.BaselineId > _currentBaselineId;
            var superseded = hadBaseline && beginsTransfer;
            if (beginsTransfer)
            {
                BeginSupersedingBaseline(chunk, nowSeconds);
            }
            else if (_resyncRequested)
            {
                return Result(RuntimeClientReceiveResultKind.WaitingForBaseline);
            }
            else if (_currentBaselineId == 0)
            {
                BeginSupersedingBaseline(chunk, nowSeconds);
            }

            var assemblyResult = _baselineAssembler.Accept(chunk, nowSeconds, out var payload);
            switch (assemblyResult)
            {
                case RuntimeBaselineChunkResult.Accepted:
                    return Result(superseded ? RuntimeClientReceiveResultKind.Superseded : RuntimeClientReceiveResultKind.Accepted);
                case RuntimeBaselineChunkResult.Duplicate:
                case RuntimeBaselineChunkResult.DuplicateCompleted:
                    return Result(RuntimeClientReceiveResultKind.Duplicate);
                case RuntimeBaselineChunkResult.Completed:
                    return ApplyCompletedBaseline(payload, nowSeconds);
                case RuntimeBaselineChunkResult.TimedOut:
                    return RequestResync(RuntimeClientResyncReason.BaselineTimedOut);
                case RuntimeBaselineChunkResult.Invalid:
                case RuntimeBaselineChunkResult.ConflictingTransfer:
                case RuntimeBaselineChunkResult.Corrupt:
                    return RequestResync(RuntimeClientResyncReason.BaselineInvalid);
                default:
                    throw new ArgumentOutOfRangeException(nameof(assemblyResult), assemblyResult, null);
            }
        }

        public RuntimeClientReceiveResult ReceiveDelta(RuntimeClientDeltaEnvelope delta, double nowSeconds)
        {
            if (delta == null)
            {
                throw new ArgumentNullException(nameof(delta));
            }

            var authorization = Authorize(delta.SessionId, delta.Store);
            if (!authorization.Accepted)
            {
                return Result(RuntimeClientReceiveResultKind.Rejected, authorization.RejectCode);
            }

            if (!IsValidDelta(delta))
            {
                return RequestResync(RuntimeClientResyncReason.DeltaInvalid);
            }

            if (_resyncRequested)
            {
                var canRetryExpectedEnvelope = _appliedBaselineId == _currentBaselineId
                                               && !_resyncRequiresNewBaseline
                                               && delta.BaselineId == _currentBaselineId
                                               && delta.DeliverySequence == _resyncExpectedSequence;
                if (!canRetryExpectedEnvelope)
                    return Result(RuntimeClientReceiveResultKind.WaitingForBaseline);

                _resyncRequested = false;
                _resyncExpectedSequence = 0;
            }

            if (_currentBaselineId == 0)
            {
                return RequestResync(RuntimeClientResyncReason.DeltaInvalid);
            }

            if (delta.BaselineId < _currentBaselineId)
            {
                return Result(RuntimeClientReceiveResultKind.Stale);
            }

            if (delta.BaselineId > _currentBaselineId)
            {
                return RequestResync(RuntimeClientResyncReason.DeltaInvalid);
            }

            var baselineSequence = _activeBaselineHeader.DeliverySequence;
            if (_appliedBaselineId == _currentBaselineId)
            {
                baselineSequence = LastAppliedSequence;
            }

            if (delta.DeliverySequence < baselineSequence)
            {
                return Result(RuntimeClientReceiveResultKind.Stale);
            }

            if (delta.DeliverySequence == baselineSequence)
            {
                return Result(RuntimeClientReceiveResultKind.Duplicate);
            }

            if (_pendingDeltas.TryGetValue(delta.DeliverySequence, out var existing))
            {
                return existing.HasSameLogicalContents(delta)
                    ? Result(RuntimeClientReceiveResultKind.Duplicate)
                    : RequestResync(RuntimeClientResyncReason.DeltaInvalid);
            }

            if (_pendingDeltas.Count + 1 > _maxPendingEnvelopeCount
                || _pendingBytes + delta.PayloadBytes > _maxPendingBytes)
            {
                return RequestResync(RuntimeClientResyncReason.DeltaBufferOverflow);
            }

            _pendingDeltas.Add(delta.DeliverySequence, delta);
            _pendingBytes += delta.PayloadBytes;
            if (_appliedBaselineId != _currentBaselineId)
            {
                return Result(RuntimeClientReceiveResultKind.Buffered);
            }

            return DrainContiguous(nowSeconds, RuntimeClientReceiveResultKind.Buffered);
        }

        public RuntimeClientReceiveResult Tick(double nowSeconds)
        {
            if (_resyncRequested)
            {
                return Result(RuntimeClientReceiveResultKind.WaitingForBaseline);
            }

            if (_baselineAssembler.IsActive
                && nowSeconds - _baselineStartedAt > RuntimeProtocolV2.BASELINE_TIMEOUT_SECONDS)
            {
                _baselineAssembler.Reset();
                return RequestResync(RuntimeClientResyncReason.BaselineTimedOut);
            }

            if (_hasGapTimer && nowSeconds - _gapStartedAt > _gapTimeoutSeconds)
            {
                return RequestResync(RuntimeClientResyncReason.DeltaGapTimedOut);
            }

            return Result(RuntimeClientReceiveResultKind.Accepted);
        }

#if MIRROR
        public RuntimeClientReceiveResult ReceiveDelta(in RtStoreDelta delta, double nowSeconds)
        {
            return ReceiveDelta(RuntimeClientDeltaEnvelope.FromWire(delta), nowSeconds);
        }
#endif

        private RuntimeClientReceiveResult ApplyCompletedBaseline(byte[] payload, double nowSeconds)
        {
            var baseline = new RuntimeClientBaselineEnvelope(
                SessionId,
                Store,
                _activeBaselineHeader.BaselineId,
                _activeBaselineHeader.DeliverySequence,
                _activeBaselineHeader.StoreRevision,
                payload);
            if (!_applyBaseline(baseline))
            {
                return RequestResync(RuntimeClientResyncReason.BaselineApplyFailed);
            }

            _appliedBaselineId = baseline.BaselineId;
            _currentBaselineId = baseline.BaselineId;
            LastAppliedSequence = baseline.DeliverySequence;
            LastAppliedRevision = baseline.StoreRevision;
            _activeBaselineHeader = default;
            _baselineStartedAt = 0;
            return DrainContiguous(nowSeconds, RuntimeClientReceiveResultKind.BaselineApplied);
        }

        private RuntimeClientReceiveResult DrainContiguous(double nowSeconds, RuntimeClientReceiveResultKind fallbackKind)
        {
            var applied = 0;
            while (LastAppliedSequence != ulong.MaxValue
                   && _pendingDeltas.TryGetValue(LastAppliedSequence + 1, out var delta))
            {
                if (delta.FromRevision != LastAppliedRevision || delta.ToRevision < delta.FromRevision)
                {
                    return RequestResync(RuntimeClientResyncReason.DeltaInvalid);
                }

                if (!_applyDelta(delta))
                {
                    return RequestResync(RuntimeClientResyncReason.DeltaApplyFailed);
                }

                _pendingDeltas.Remove(delta.DeliverySequence);
                _pendingBytes -= delta.PayloadBytes;
                LastAppliedSequence = delta.DeliverySequence;
                LastAppliedRevision = delta.ToRevision;
                applied++;
            }

            UpdateGapTimer(nowSeconds);
            return Result(applied > 0 ? RuntimeClientReceiveResultKind.DeltaApplied : fallbackKind, appliedDeltaCount: applied);
        }

        private void BeginSupersedingBaseline(in RuntimeBaselineChunk chunk, double nowSeconds)
        {
            _baselineAssembler.Reset();
            ClearPendingDeltas();
            _currentBaselineId = chunk.BaselineId;
            _activeBaselineHeader = chunk;
            _activeBaselineHeader.Payload = null;
            _activeBaselineHeader.PayloadHash = null;
            _baselineStartedAt = nowSeconds;
            _resyncRequested = false;
            _resyncRequiresNewBaseline = false;
            _resyncExpectedSequence = 0;
            _hasGapTimer = false;
            _gapStartedAt = 0;
        }

        private RuntimeSessionHandshakeResult Authorize(ulong sessionId, in NetStoreRef store)
        {
            var authorization = _authorize(sessionId, store);
            if (!authorization.Accepted)
            {
                return authorization;
            }

            if (sessionId != SessionId)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidEnvelope, "Envelope references a forged or stale session id.");
            }

            if (store != Store)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidStore, $"Envelope store '{store}' does not match receiver store '{Store}'.");
            }

            return RuntimeSessionHandshakeResult.Success();
        }

        private bool IsValidDelta(RuntimeClientDeltaEnvelope delta)
        {
            return delta.BaselineId != 0
                   && delta.DeliverySequence != 0
                   && ((delta.Kind == RuntimeStoreDeltaKind.Mutation && delta.ToRevision > delta.FromRevision)
                       || (delta.Kind == RuntimeStoreDeltaKind.Interest && delta.ToRevision >= delta.FromRevision));
        }

        private void UpdateGapTimer(double nowSeconds)
        {
            if (_pendingDeltas.Count == 0 || LastAppliedSequence == ulong.MaxValue)
            {
                _hasGapTimer = false;
                _gapStartedAt = 0;
                return;
            }

            using var enumerator = _pendingDeltas.GetEnumerator();
            enumerator.MoveNext();
            var hasGap = enumerator.Current.Key > LastAppliedSequence + 1;
            if (!hasGap)
            {
                _hasGapTimer = false;
                _gapStartedAt = 0;
                return;
            }

            if (!_hasGapTimer)
            {
                _hasGapTimer = true;
                _gapStartedAt = nowSeconds;
            }
        }

        private RuntimeClientReceiveResult RequestResync(RuntimeClientResyncReason reason)
        {
            if (_resyncRequested)
            {
                return Result(RuntimeClientReceiveResultKind.WaitingForBaseline);
            }

            _resyncRequested = true;
            _baselineAssembler.Reset();
            // A pure reliable-sequence gap can be repaired by retransmitting
            // the one missing envelope. Preserve already buffered later
            // envelopes so they drain immediately after that recovery.
            if (reason != RuntimeClientResyncReason.DeltaGapTimedOut)
                ClearPendingDeltas();
            ulong expectedSequence;
            if (_appliedBaselineId == _currentBaselineId && _currentBaselineId != 0)
            {
                expectedSequence = LastAppliedSequence == ulong.MaxValue ? ulong.MaxValue : LastAppliedSequence + 1;
            }
            else
            {
                expectedSequence = _activeBaselineHeader.DeliverySequence == 0 ? 1 : _activeBaselineHeader.DeliverySequence;
            }
            _resyncExpectedSequence = expectedSequence;
            var forceNewBaseline = reason == RuntimeClientResyncReason.BaselineApplyFailed
                                   || reason == RuntimeClientResyncReason.DeltaBufferOverflow
                                   || reason == RuntimeClientResyncReason.DeltaInvalid
                                   || reason == RuntimeClientResyncReason.DeltaApplyFailed;
            _resyncRequiresNewBaseline = forceNewBaseline;
            _requestResync(new RuntimeClientResyncRequest(
                SessionId,
                Store,
                forceNewBaseline ? 0 : _currentBaselineId,
                expectedSequence,
                reason));
            return Result(RuntimeClientReceiveResultKind.ResyncRequested);
        }

        private void ClearPendingDeltas()
        {
            _pendingDeltas.Clear();
            _pendingBytes = 0;
            _hasGapTimer = false;
            _gapStartedAt = 0;
        }

        private RuntimeClientReceiveResult Result(
            RuntimeClientReceiveResultKind kind,
            RuntimeProtocolRejectCode rejectCode = RuntimeProtocolRejectCode.None,
            int appliedDeltaCount = 0)
        {
            return new RuntimeClientReceiveResult(kind, rejectCode, appliedDeltaCount, LastAppliedSequence);
        }
    }
}
