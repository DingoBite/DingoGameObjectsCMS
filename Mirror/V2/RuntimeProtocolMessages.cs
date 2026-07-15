#if MIRROR
using System;
using Mirror;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public readonly struct RuntimeReliableDeltaWireSize
    {
        public readonly int PackedBytes;
        public readonly int BatchedBytes;
        public readonly int MaxPackedBytes;

        public bool Fits => PackedBytes <= MaxPackedBytes;

        public RuntimeReliableDeltaWireSize(int packedBytes, int batchedBytes, int maxPackedBytes)
        {
            PackedBytes = packedBytes;
            BatchedBytes = batchedBytes;
            MaxPackedBytes = maxPackedBytes;
        }
    }

    /// <summary>
    /// Measures the exact generated Mirror representation of RtStoreDelta.
    /// The limit accounts for both Mirror's message id and its batch timestamp
    /// and VarUInt message-length header. Production also clamps the protocol
    /// cap to the active transport's reliable packet limit.
    /// </summary>
    public static class RuntimeReliableDeltaTransportBudget
    {
        public static bool Fits(in RuntimeReliableDeltaTransportEnvelope envelope)
        {
            return Measure(envelope, ResolveReliableTransportPacketBytes()).Fits;
        }

        public static RuntimeReliableDeltaWireSize Measure(
            in RuntimeReliableDeltaTransportEnvelope envelope,
            int reliableTransportPacketBytes)
        {
            if (reliableTransportPacketBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(reliableTransportPacketBytes));

            using var writer = NetworkWriterPool.Get();
            NetworkMessages.Pack(ToWire(envelope), writer);
            var packedBytes = writer.Position;
            var batchedBytes = checked(packedBytes + Batcher.MaxMessageOverhead(packedBytes));
            var protocolMaxPackedBytes = RuntimeProtocolV2.MAX_RELIABLE_DELTA_BATCH_BYTES
                                         - Batcher.MaxMessageOverhead(RuntimeProtocolV2.MAX_RELIABLE_DELTA_BATCH_BYTES);
            var transportMaxPackedBytes = reliableTransportPacketBytes
                                          - Batcher.MaxMessageOverhead(reliableTransportPacketBytes);
            var maxPackedBytes = Math.Min(protocolMaxPackedBytes, transportMaxPackedBytes);
            return new RuntimeReliableDeltaWireSize(packedBytes, batchedBytes, maxPackedBytes);
        }

        private static int ResolveReliableTransportPacketBytes()
        {
            var transport = Transport.active;
            return transport == null
                ? RuntimeProtocolV2.MAX_RELIABLE_DELTA_BATCH_BYTES
                : transport.GetMaxPacketSize(Channels.Reliable);
        }

        private static RtStoreDelta ToWire(in RuntimeReliableDeltaTransportEnvelope envelope)
        {
            return new RtStoreDelta
            {
                Kind = envelope.Kind,
                SessionId = envelope.SessionId,
                Store = envelope.Store,
                BaselineId = envelope.BaselineId,
                DeliverySequence = envelope.DeliverySequence,
                FromRevision = envelope.FromRevision,
                ToRevision = envelope.ToRevision,
                Payload = envelope.PayloadBuffer,
            };
        }
    }

    [Serializable, Preserve]
    public struct RtSessionHello : NetworkMessage
    {
        public RuntimeSessionDescriptor Descriptor;
        public ulong ClientNonce;
    }

    [Serializable, Preserve]
    public struct RtSessionManifest : NetworkMessage
    {
        public ulong SessionId;
        public RuntimeSessionDescriptor Descriptor;
        public RuntimeAssetCatalogEntry[] Assets;
        public RuntimeStoreCatalogEntry[] Stores;
    }

    [Serializable, Preserve]
    public struct RtSessionReady : NetworkMessage
    {
        public ulong SessionId;
    }

    [Serializable, Preserve]
    public struct RtProtocolReject : NetworkMessage
    {
        public RuntimeProtocolRejectCode Code;
        public string Detail;
    }

    [Serializable, Preserve]
    public struct RtBaselineChunk : NetworkMessage
    {
        public RuntimeBaselineChunk Value;
    }

    [Serializable, Preserve]
    public struct RtStoreDelta : NetworkMessage
    {
        public RuntimeStoreDeltaKind Kind;
        public ulong SessionId;
        public NetStoreRef Store;
        public ulong BaselineId;
        public ulong DeliverySequence;
        public ulong FromRevision;
        public ulong ToRevision;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtStoreAck : NetworkMessage
    {
        public ulong SessionId;
        public NetStoreRef Store;
        public ulong BaselineId;
        public ulong DeliverySequence;
    }

    [Serializable, Preserve]
    public struct RtStoreResyncRequest : NetworkMessage
    {
        public ulong SessionId;
        public NetStoreRef Store;
        public ulong BaselineId;
        public ulong ExpectedDeliverySequence;
    }

    [Serializable, Preserve]
    public struct RtStoreRemoved : NetworkMessage
    {
        public ulong SessionId;
        public NetStoreRef Store;
        public ulong DeliverySequence;
    }

    [Serializable, Preserve]
    public struct RtMotionState : NetworkMessage
    {
        public ulong SessionId;
        public NetStoreRef Store;
        public uint SimulationTick;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtCommandEnvelope : NetworkMessage
    {
        public RuntimeCommandEnvelope Value;
    }

    [Serializable, Preserve]
    public struct RtCommandResult : NetworkMessage
    {
        public ulong ClientSequence;
        public RuntimeCommandRejectCode RejectCode;
    }
}
#endif
