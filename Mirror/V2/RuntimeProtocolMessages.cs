#if MIRROR
using System;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using Mirror;
using Unity.Collections;
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
    public struct RtStateStreamFrame : NetworkMessage
    {
        public ulong SessionId;
        public NetStoreRef Store;
        public uint StreamTypeId;
        public uint Sequence;
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

    [Serializable, Preserve]
    public struct RuntimeCommandJournalWireEntry
    {
        public long ApplyBeforeTick;
        public ulong Sequence;
        public uint CommandTypeId;
        public ushort CommandCodecVersion;
        public byte[] Payload;
        public bool SessionWide;
        public FixedString32Bytes[] StoreIds;
    }

    [Serializable, Preserve]
    public struct RtCommandJournalBatch : NetworkMessage
    {
        public ulong SessionId;
        public string CheckpointGroupId;
        public string CheckpointHash;
        public ulong FromCursor;
        public ulong ScannedThroughCursor;
        public bool CompletesCatchup;
        public RuntimeCommandJournalWireEntry[] Entries;
    }

    [Serializable, Preserve]
    public struct RtCommandJournalResyncRequest : NetworkMessage
    {
        public ulong SessionId;
        public string CheckpointGroupId;
        public string CheckpointHash;
        public ulong ExpectedCursor;
        public bool ForceCheckpointBaseline;
    }

    public static class RuntimeCommandJournalWireCodec
    {
        public static RtCommandJournalBatch ToWire(RuntimeCommandJournalBatch batch)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }
            if (batch.Entries.Count > RuntimeProtocolV2.MAX_JOURNAL_BATCH_ENTRIES)
            {
                throw new InvalidOperationException(
                    $"Journal batch contains {batch.Entries.Count} entries; limit is {RuntimeProtocolV2.MAX_JOURNAL_BATCH_ENTRIES}.");
            }

            var entries = new RuntimeCommandJournalWireEntry[batch.Entries.Count];
            var payloadBytes = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = batch.Entries[i];
                payloadBytes = checked(payloadBytes + entry.EncodedCommand.PayloadBytes);
                entries[i] = new RuntimeCommandJournalWireEntry
                {
                    ApplyBeforeTick = entry.ApplyBeforeTick,
                    Sequence = entry.Sequence,
                    CommandTypeId = entry.EncodedCommand.TypeId,
                    CommandCodecVersion = entry.EncodedCommand.CodecVersion,
                    Payload = RuntimeEncodedCommand.CopyPayload(entry.EncodedCommand.Payload),
                    SessionWide = entry.Scope.IsSessionWide,
                    StoreIds = CopyStoreIds(entry.Scope),
                };
            }
            if (payloadBytes > RuntimeProtocolV2.MAX_JOURNAL_BATCH_BYTES)
            {
                throw new InvalidOperationException(
                    $"Journal batch payload is {payloadBytes} bytes; limit is {RuntimeProtocolV2.MAX_JOURNAL_BATCH_BYTES}.");
            }

            return new RtCommandJournalBatch
            {
                SessionId = batch.SessionId,
                CheckpointGroupId = batch.CheckpointGroupId,
                CheckpointHash = batch.CheckpointHash,
                FromCursor = batch.FromCursor,
                ScannedThroughCursor = batch.ScannedThroughCursor,
                CompletesCatchup = batch.CompletesCatchup,
                Entries = entries,
            };
        }

        public static RuntimeCommandJournalBatch FromWire(in RtCommandJournalBatch wire)
        {
            var wireEntries = wire.Entries ?? Array.Empty<RuntimeCommandJournalWireEntry>();
            if (wireEntries.Length > RuntimeProtocolV2.MAX_JOURNAL_BATCH_ENTRIES)
            {
                throw new FormatException(
                    $"Journal batch contains {wireEntries.Length} entries; limit is {RuntimeProtocolV2.MAX_JOURNAL_BATCH_ENTRIES}.");
            }

            var entries = new RuntimeCommandJournalEntry[wireEntries.Length];
            var payloadBytes = 0;
            for (var i = 0; i < entries.Length; i++)
            {
                var wireEntry = wireEntries[i];
                payloadBytes = checked(payloadBytes + (wireEntry.Payload?.Length ?? 0));
                var scope = wireEntry.SessionWide
                    ? RequireSessionScope(wireEntry.StoreIds)
                    : new RuntimeCommandJournalScope(
                        wireEntry.StoreIds
                        ?? throw new FormatException(
                            $"Journal entry {i} has no store-set scope."));
                entries[i] = new RuntimeCommandJournalEntry(
                    wireEntry.ApplyBeforeTick,
                    wireEntry.Sequence,
                    new RuntimeEncodedCommand(
                        wireEntry.CommandTypeId,
                        wireEntry.CommandCodecVersion,
                        wireEntry.Payload),
                    scope);
            }
            if (payloadBytes > RuntimeProtocolV2.MAX_JOURNAL_BATCH_BYTES)
            {
                throw new FormatException(
                    $"Journal batch payload is {payloadBytes} bytes; limit is {RuntimeProtocolV2.MAX_JOURNAL_BATCH_BYTES}.");
            }

            return new RuntimeCommandJournalBatch(
                wire.SessionId,
                wire.CheckpointGroupId,
                wire.CheckpointHash,
                wire.FromCursor,
                wire.ScannedThroughCursor,
                entries,
                wire.CompletesCatchup);
        }

        private static FixedString32Bytes[] CopyStoreIds(RuntimeCommandJournalScope scope)
        {
            if (scope.IsSessionWide)
            {
                return Array.Empty<FixedString32Bytes>();
            }

            var result = new FixedString32Bytes[scope.StoreIds.Count];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = scope.StoreIds[i];
            }

            return result;
        }

        private static RuntimeCommandJournalScope RequireSessionScope(
            FixedString32Bytes[] storeIds)
        {
            if (storeIds != null && storeIds.Length > 0)
            {
                throw new FormatException(
                    "A session-wide journal scope cannot also carry StoreIds.");
            }

            return RuntimeCommandJournalScope.Session;
        }
    }
}
#endif
