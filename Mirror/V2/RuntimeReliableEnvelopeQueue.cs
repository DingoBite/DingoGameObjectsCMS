using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror.V2
{
    [Serializable, Preserve]
    public sealed class RuntimeReliableEnvelope
    {
        public RuntimeStoreDeltaKind Kind;
        public ulong DeliverySequence;
        public ulong BaselineId;
        public ulong FromRevision;
        public ulong ToRevision;
        public byte[] Payload;

        public int PayloadBytes => Payload?.Length ?? 0;
    }

    public enum RuntimeReliableAckResult : byte
    {
        Accepted = 0,
        Duplicate = 1,
        Stale = 2,
        Future = 3,
    }

    public sealed class RuntimeReliableEnvelopeQueue
    {
        private readonly LinkedList<RuntimeReliableEnvelope> _pending = new();
        private readonly int _maxCount;
        private readonly int _maxBytes;
        private int _pendingBytes;

        public int Count => _pending.Count;
        public int PendingBytes => _pendingBytes;
        public ulong LastAcknowledgedSequence { get; private set; }
        public ulong HighestEnqueuedSequence { get; private set; }

        public RuntimeReliableEnvelopeQueue(
            int maxCount = RuntimeProtocolV2.MAX_PENDING_ENVELOPES,
            int maxBytes = RuntimeProtocolV2.MAX_PENDING_ENVELOPE_BYTES)
        {
            if (maxCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxCount));
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            _maxCount = maxCount;
            _maxBytes = maxBytes;
        }

        public bool TryEnqueue(RuntimeReliableEnvelope envelope)
        {
            if (envelope == null)
                throw new ArgumentNullException(nameof(envelope));
            if (envelope.DeliverySequence == 0 || envelope.DeliverySequence != HighestEnqueuedSequence + 1)
                throw new InvalidOperationException($"Delivery sequence must be contiguous. Expected {HighestEnqueuedSequence + 1}, got {envelope.DeliverySequence}.");
            if (envelope.ToRevision < envelope.FromRevision)
                throw new InvalidOperationException("Envelope revision range is inverted.");

            var bytes = envelope.PayloadBytes;
            if (_pending.Count + 1 > _maxCount || _pendingBytes + bytes > _maxBytes)
                return false;

            _pending.AddLast(envelope);
            _pendingBytes += bytes;
            HighestEnqueuedSequence = envelope.DeliverySequence;
            return true;
        }

        public RuntimeReliableAckResult Acknowledge(ulong deliverySequence)
        {
            if (deliverySequence == LastAcknowledgedSequence)
                return RuntimeReliableAckResult.Duplicate;
            if (deliverySequence < LastAcknowledgedSequence)
                return RuntimeReliableAckResult.Stale;
            if (deliverySequence > HighestEnqueuedSequence)
                return RuntimeReliableAckResult.Future;

            var acknowledgedNode = _pending.First;
            while (acknowledgedNode != null && acknowledgedNode.Value.DeliverySequence != deliverySequence)
                acknowledgedNode = acknowledgedNode.Next;
            if (acknowledgedNode == null)
                return RuntimeReliableAckResult.Stale;

            while (_pending.First != null && _pending.First.Value.DeliverySequence <= deliverySequence)
            {
                _pendingBytes -= _pending.First.Value.PayloadBytes;
                _pending.RemoveFirst();
            }

            LastAcknowledgedSequence = deliverySequence;
            return RuntimeReliableAckResult.Accepted;
        }

        public bool TryGet(ulong deliverySequence, out RuntimeReliableEnvelope envelope)
        {
            for (var node = _pending.First; node != null; node = node.Next)
            {
                if (node.Value.DeliverySequence != deliverySequence)
                    continue;
                envelope = node.Value;
                return true;
            }

            envelope = null;
            return false;
        }

        public void DiscardPendingForNewBaseline()
        {
            _pending.Clear();
            _pendingBytes = 0;
        }
    }
}
