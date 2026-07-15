using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeConnectionDeltaEnqueueResult : byte
    {
        Enqueued = 1,
        NeedsBaseline = 2,
        RevisionGap = 3,
        InvalidMembership = 4,
    }

    public enum RuntimeConnectionAckResult : byte
    {
        Accepted = 1,
        Duplicate = 2,
        Stale = 3,
        ForgedSequence = 4,
        StaleBaseline = 5,
        ForgedBaseline = 6,
    }

    public class RuntimeActiveBaselineTransfer
    {
        private readonly byte[] _payload;

        public readonly NetStoreRef Store;
        public readonly ulong BaselineId;
        public readonly ulong DeliverySequence;
        public readonly ulong StoreRevision;

        public int PayloadBytes => _payload.Length;

        public RuntimeActiveBaselineTransfer(NetStoreRef store, ulong baselineId, ulong deliverySequence, ulong storeRevision, byte[] payload)
        {
            Store = store;
            BaselineId = baselineId;
            DeliverySequence = deliverySequence;
            StoreRevision = storeRevision;
            _payload = payload == null || payload.Length == 0 ? Array.Empty<byte>() : (byte[])payload.Clone();
        }

        public byte[] CopyPayload() => _payload.Length == 0 ? Array.Empty<byte>() : (byte[])_payload.Clone();
    }

    public class RuntimeMembershipProjectionCommit
    {
        public readonly ulong DeliverySequence;
        public readonly ulong ToRevision;
        public readonly bool ReplacesMembership;
        public readonly IReadOnlyList<NetObjectRef> BaselineMembership;
        public readonly IReadOnlyList<NetObjectRef> Enters;
        public readonly IReadOnlyList<NetObjectRef> Leaves;

        private RuntimeMembershipProjectionCommit(ulong deliverySequence, ulong toRevision, bool replacesMembership, IReadOnlyList<NetObjectRef> baselineMembership, IReadOnlyList<NetObjectRef> enters, IReadOnlyList<NetObjectRef> leaves)
        {
            DeliverySequence = deliverySequence;
            ToRevision = toRevision;
            ReplacesMembership = replacesMembership;
            BaselineMembership = CopyImmutable(baselineMembership);
            Enters = CopyImmutable(enters);
            Leaves = CopyImmutable(leaves);
        }

        public static RuntimeMembershipProjectionCommit Baseline(ulong deliverySequence, ulong storeRevision, IReadOnlyList<NetObjectRef> membership)
        {
            return new RuntimeMembershipProjectionCommit(deliverySequence, storeRevision, true, membership, Array.Empty<NetObjectRef>(), Array.Empty<NetObjectRef>());
        }

        public static RuntimeMembershipProjectionCommit Delta(ulong deliverySequence, ulong toRevision, IReadOnlyList<NetObjectRef> enters, IReadOnlyList<NetObjectRef> leaves)
        {
            return new RuntimeMembershipProjectionCommit(deliverySequence, toRevision, false, Array.Empty<NetObjectRef>(), enters, leaves);
        }

        private static IReadOnlyList<NetObjectRef> CopyImmutable(IReadOnlyList<NetObjectRef> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<NetObjectRef>();

            var copy = new NetObjectRef[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return Array.AsReadOnly(copy);
        }
    }

    public class RuntimeConnectionStoreReplicationState
    {
        private readonly RuntimeReliableEnvelopeQueue _reliableQueue;
        private readonly SortedDictionary<ulong, RuntimeMembershipProjectionCommit> _pendingMembership = new();
        private HashSet<NetObjectRef> _projectedMembership = new();
        private HashSet<NetObjectRef> _acknowledgedMembership = new();
        private ulong _deliverySequence;
        private ulong _baselineId;

        public readonly NetStoreRef Store;

        public ulong StoreRevision { get; private set; }
        public ulong ProjectedRevision { get; private set; }
        public ulong AcknowledgedRevision { get; private set; }
        public ulong BaselineId => _baselineId;
        public ulong HighestDeliverySequence => _deliverySequence;
        public bool NeedsBaseline { get; private set; }
        public RuntimeActiveBaselineTransfer ActiveBaseline { get; private set; }
        public int PendingReliableCount => _reliableQueue.Count;
        public int PendingDeltaBytes => _reliableQueue.PendingBytes;
        public int ProjectedMembershipCount => _projectedMembership.Count;
        public int AcknowledgedMembershipCount => _acknowledgedMembership.Count;

        public RuntimeConnectionStoreReplicationState(NetStoreRef store, ulong initialStoreRevision, int maxPendingEnvelopeCount = RuntimeProtocolV2.MAX_PENDING_ENVELOPES, int maxPendingDeltaBytes = RuntimeProtocolV2.MAX_PENDING_ENVELOPE_BYTES)
        {
            if (!store.IsValid)
                throw new ArgumentException("Connection replication state requires a valid store reference.", nameof(store));

            Store = store;
            StoreRevision = initialStoreRevision;
            _reliableQueue = new RuntimeReliableEnvelopeQueue(maxPendingEnvelopeCount, maxPendingDeltaBytes);
        }

        public RuntimeActiveBaselineTransfer BeginBaseline(byte[] payload, IReadOnlyList<NetObjectRef> membership)
        {
            return BeginBaseline(StoreRevision, payload, membership);
        }

        public RuntimeActiveBaselineTransfer BeginBaseline(ulong baselineRevision, byte[] payload, IReadOnlyList<NetObjectRef> membership)
        {
            if (baselineRevision != StoreRevision)
                throw new InvalidOperationException($"Baseline revision {baselineRevision} does not match observed store revision {StoreRevision}.");
            if (payload != null && payload.Length > RuntimeProtocolV2.MAX_BASELINE_BYTES)
                throw new ArgumentOutOfRangeException(nameof(payload), $"Baseline exceeds {RuntimeProtocolV2.MAX_BASELINE_BYTES} bytes.");

            var nextBaselineId = TakeNext(_baselineId, nameof(BaselineId));
            var nextDeliverySequence = TakeNext(_deliverySequence, nameof(HighestDeliverySequence));
            var commit = RuntimeMembershipProjectionCommit.Baseline(nextDeliverySequence, baselineRevision, membership);
            ValidateBaselineMembership(commit.BaselineMembership);
            var transfer = new RuntimeActiveBaselineTransfer(Store, nextBaselineId, nextDeliverySequence, baselineRevision, payload);
            var marker = new RuntimeReliableEnvelope
            {
                Kind = RuntimeStoreDeltaKind.Mutation,
                DeliverySequence = nextDeliverySequence,
                BaselineId = nextBaselineId,
                FromRevision = baselineRevision,
                ToRevision = baselineRevision,
                Payload = Array.Empty<byte>(),
            };

            _reliableQueue.DiscardPendingForNewBaseline();
            _pendingMembership.Clear();
            if (!_reliableQueue.TryEnqueue(marker))
                throw new InvalidOperationException("Empty baseline sequencing marker did not fit an empty reliable queue.");

            _baselineId = nextBaselineId;
            _deliverySequence = nextDeliverySequence;
            ActiveBaseline = transfer;
            ProjectedRevision = baselineRevision;
            ReplaceProjectedMembership(commit.BaselineMembership);
            _pendingMembership.Add(nextDeliverySequence, commit);
            NeedsBaseline = false;
            return transfer;
        }

        public RuntimeConnectionDeltaEnqueueResult TryEnqueueDelta(ulong fromRevision, ulong toRevision, byte[] payload, IReadOnlyList<NetObjectRef> enters, IReadOnlyList<NetObjectRef> leaves, out RuntimeReliableEnvelope envelope)
        {
            envelope = null;
            if (toRevision <= fromRevision)
                throw new ArgumentOutOfRangeException(nameof(toRevision), "Delta must advance the store revision.");
            if (StoreRevision == ulong.MaxValue || toRevision != StoreRevision + 1)
            {
                NeedsBaseline = true;
                return RuntimeConnectionDeltaEnqueueResult.RevisionGap;
            }

            StoreRevision = toRevision;
            if (_baselineId == 0 || NeedsBaseline)
            {
                NeedsBaseline = true;
                return RuntimeConnectionDeltaEnqueueResult.NeedsBaseline;
            }

            if (fromRevision != ProjectedRevision || !TryBuildProjectedMembership(enters, leaves, out var nextMembership))
            {
                NeedsBaseline = true;
                return fromRevision != ProjectedRevision
                    ? RuntimeConnectionDeltaEnqueueResult.RevisionGap
                    : RuntimeConnectionDeltaEnqueueResult.InvalidMembership;
            }

            var nextDeliverySequence = TakeNext(_deliverySequence, nameof(HighestDeliverySequence));
            var payloadCopy = payload == null || payload.Length == 0 ? Array.Empty<byte>() : (byte[])payload.Clone();
            var pendingEnvelope = new RuntimeReliableEnvelope
            {
                Kind = RuntimeStoreDeltaKind.Mutation,
                DeliverySequence = nextDeliverySequence,
                BaselineId = _baselineId,
                FromRevision = fromRevision,
                ToRevision = toRevision,
                Payload = payloadCopy,
            };
            if (!_reliableQueue.TryEnqueue(pendingEnvelope))
            {
                NeedsBaseline = true;
                return RuntimeConnectionDeltaEnqueueResult.NeedsBaseline;
            }

            var commit = RuntimeMembershipProjectionCommit.Delta(nextDeliverySequence, toRevision, enters, leaves);
            _pendingMembership.Add(nextDeliverySequence, commit);
            _deliverySequence = nextDeliverySequence;
            ProjectedRevision = toRevision;
            _projectedMembership = nextMembership;
            envelope = pendingEnvelope;
            return RuntimeConnectionDeltaEnqueueResult.Enqueued;
        }

        /// <summary>
        /// Enqueues a reliable membership/topology projection without creating
        /// an authoritative store revision. The envelope may cover filtered
        /// revisions already observed by this connection, or use an equal
        /// from/to revision when only interest changed.
        /// </summary>
        public RuntimeConnectionDeltaEnqueueResult TryEnqueueInterestDelta(
            byte[] payload,
            IReadOnlyList<NetObjectRef> enters,
            IReadOnlyList<NetObjectRef> leaves,
            out RuntimeReliableEnvelope envelope)
        {
            envelope = null;
            if (_baselineId == 0 || NeedsBaseline)
                return RuntimeConnectionDeltaEnqueueResult.NeedsBaseline;
            if (ProjectedRevision > StoreRevision)
                throw new InvalidOperationException(
                    $"Projected revision {ProjectedRevision} is ahead of observed store revision {StoreRevision}.");
            if (!TryBuildProjectedMembership(enters, leaves, out var nextMembership))
                return RuntimeConnectionDeltaEnqueueResult.InvalidMembership;

            var nextDeliverySequence = TakeNext(_deliverySequence, nameof(HighestDeliverySequence));
            var payloadCopy = payload == null || payload.Length == 0 ? Array.Empty<byte>() : (byte[])payload.Clone();
            var pendingEnvelope = new RuntimeReliableEnvelope
            {
                Kind = RuntimeStoreDeltaKind.Interest,
                DeliverySequence = nextDeliverySequence,
                BaselineId = _baselineId,
                FromRevision = ProjectedRevision,
                ToRevision = StoreRevision,
                Payload = payloadCopy,
            };
            if (!_reliableQueue.TryEnqueue(pendingEnvelope))
            {
                NeedsBaseline = true;
                return RuntimeConnectionDeltaEnqueueResult.NeedsBaseline;
            }

            var commit = RuntimeMembershipProjectionCommit.Delta(
                nextDeliverySequence,
                StoreRevision,
                enters,
                leaves);
            _pendingMembership.Add(nextDeliverySequence, commit);
            _deliverySequence = nextDeliverySequence;
            ProjectedRevision = StoreRevision;
            _projectedMembership = nextMembership;
            envelope = pendingEnvelope;
            return RuntimeConnectionDeltaEnqueueResult.Enqueued;
        }

        /// <summary>
        /// Advances the observed authoritative revision when filtering produced
        /// no logical envelope. The next non-empty envelope starts at
        /// <see cref="ProjectedRevision"/> and therefore covers the complete
        /// skipped revision range without emitting empty network messages.
        /// </summary>
        public void ObserveFilteredRevision(ulong storeRevision)
        {
            if (StoreRevision == ulong.MaxValue || storeRevision != StoreRevision + 1)
            {
                NeedsBaseline = true;
                throw new InvalidOperationException(
                    $"Filtered store revision must be contiguous. Expected {StoreRevision + 1}, got {storeRevision}.");
            }

            StoreRevision = storeRevision;
        }

        public RuntimeConnectionAckResult Acknowledge(ulong baselineId, ulong deliverySequence)
        {
            if (baselineId == 0)
                return RuntimeConnectionAckResult.ForgedBaseline;
            if (baselineId < _baselineId)
                return RuntimeConnectionAckResult.StaleBaseline;
            if (baselineId > _baselineId)
                return RuntimeConnectionAckResult.ForgedBaseline;
            if (deliverySequence == 0)
                return RuntimeConnectionAckResult.ForgedSequence;

            var queueResult = _reliableQueue.Acknowledge(deliverySequence);
            switch (queueResult)
            {
                case RuntimeReliableAckResult.Duplicate:
                    return RuntimeConnectionAckResult.Duplicate;
                case RuntimeReliableAckResult.Stale:
                    return RuntimeConnectionAckResult.Stale;
                case RuntimeReliableAckResult.Future:
                    return RuntimeConnectionAckResult.ForgedSequence;
                case RuntimeReliableAckResult.Accepted:
                    ApplyAcknowledgedMembership(deliverySequence);
                    if (ActiveBaseline != null && ActiveBaseline.DeliverySequence <= deliverySequence)
                        ActiveBaseline = null;
                    return RuntimeConnectionAckResult.Accepted;
                default:
                    throw new ArgumentOutOfRangeException(nameof(queueResult), queueResult, null);
            }
        }

        public bool IsProjected(NetObjectRef value) => _projectedMembership.Contains(value);
        public bool IsAcknowledged(NetObjectRef value) => _acknowledgedMembership.Contains(value);
        public bool TryGetPendingReliableEnvelope(ulong deliverySequence, out RuntimeReliableEnvelope envelope) => _reliableQueue.TryGet(deliverySequence, out envelope);

        private void ValidateBaselineMembership(IReadOnlyList<NetObjectRef> membership)
        {
            var seen = new HashSet<NetObjectRef>();
            for (var i = 0; i < membership.Count; i++)
            {
                ValidateMembershipObject(membership[i]);
                if (!seen.Add(membership[i]))
                    throw new InvalidOperationException($"Baseline membership contains duplicate object '{membership[i]}'.");
            }
        }

        private bool TryBuildProjectedMembership(IReadOnlyList<NetObjectRef> enters, IReadOnlyList<NetObjectRef> leaves, out HashSet<NetObjectRef> nextMembership)
        {
            enters ??= Array.Empty<NetObjectRef>();
            leaves ??= Array.Empty<NetObjectRef>();
            nextMembership = new HashSet<NetObjectRef>(_projectedMembership);
            var transitions = new HashSet<NetObjectRef>();

            for (var i = 0; i < leaves.Count; i++)
            {
                if (!IsValidMembershipObject(leaves[i]) || !transitions.Add(leaves[i]) || !nextMembership.Remove(leaves[i]))
                    return false;
            }

            for (var i = 0; i < enters.Count; i++)
            {
                if (!IsValidMembershipObject(enters[i]) || !transitions.Add(enters[i]) || !nextMembership.Add(enters[i]))
                    return false;
            }

            return true;
        }

        private void ApplyAcknowledgedMembership(ulong deliverySequence)
        {
            var appliedSequences = new List<ulong>();
            foreach (var pair in _pendingMembership)
            {
                if (pair.Key > deliverySequence)
                    break;

                var commit = pair.Value;
                if (commit.ReplacesMembership)
                {
                    _acknowledgedMembership.Clear();
                    for (var i = 0; i < commit.BaselineMembership.Count; i++)
                    {
                        _acknowledgedMembership.Add(commit.BaselineMembership[i]);
                    }
                }
                else
                {
                    for (var i = 0; i < commit.Leaves.Count; i++)
                    {
                        _acknowledgedMembership.Remove(commit.Leaves[i]);
                    }

                    for (var i = 0; i < commit.Enters.Count; i++)
                    {
                        _acknowledgedMembership.Add(commit.Enters[i]);
                    }
                }

                AcknowledgedRevision = commit.ToRevision;
                appliedSequences.Add(pair.Key);
            }

            if (appliedSequences.Count == 0)
                throw new InvalidOperationException($"Accepted ACK {deliverySequence} has no pending membership projection.");

            for (var i = 0; i < appliedSequences.Count; i++)
            {
                _pendingMembership.Remove(appliedSequences[i]);
            }
        }

        private void ReplaceProjectedMembership(IReadOnlyList<NetObjectRef> membership)
        {
            _projectedMembership = new HashSet<NetObjectRef>();
            for (var i = 0; i < membership.Count; i++)
            {
                _projectedMembership.Add(membership[i]);
            }
        }

        private void ValidateMembershipObject(NetObjectRef value)
        {
            if (!IsValidMembershipObject(value))
                throw new InvalidOperationException($"Object '{value}' does not belong to connection store '{Store}'.");
        }

        private bool IsValidMembershipObject(NetObjectRef value) => value.IsValid && value.Store == Store;

        private static ulong TakeNext(ulong current, string name)
        {
            if (current == ulong.MaxValue)
                throw new InvalidOperationException($"{name} exhausted its range.");
            return current + 1;
        }
    }
}
