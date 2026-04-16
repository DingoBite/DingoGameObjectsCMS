#if MIRROR
using System;
using Mirror;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Serialization;
using Unity.Collections;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror
{
    [Serializable, Preserve]
    public struct RtCommandMsg : NetworkMessage
    {
        public uint Tick;
        public uint Seq;
        public int Sender;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtStoreSyncMsg : NetworkMessage
    {
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtStoreAckMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint SnapshotId;
    }

    [Serializable, Preserve]
    public struct RtStoreResyncRequestMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint HaveSnapshotId;
    }

    public static partial class RuntimeReplicationFilter
    {
        public static bool ShouldReplicateObject(GameRuntimeObject obj, ReplicationMask mask, NetworkConnectionToClient connection = null, int replicationProfileId = 0)
        {
            return ShouldReplicateObject(obj, mask, connection != null ? connection.connectionId : -1, replicationProfileId);
        }

        public static bool ShouldReplicateComponent(GameRuntimeObject owner, GameRuntimeComponent component, ReplicationMask mask, NetworkConnectionToClient connection = null, int replicationProfileId = 0)
        {
            return ShouldReplicateComponent(owner, component, mask, connection != null ? connection.connectionId : -1, replicationProfileId);
        }
    }

    public static partial class RuntimeStoreSnapshotCodec
    {
        public static RuntimeStoreSnapshot BuildSnapshot(
            RuntimeStore store,
            NetworkConnectionToClient connection = null,
            int replicationProfileId = 0,
            IRuntimePayloadSerializer serializer = null)
        {
            return BuildSnapshot(store, connection != null ? connection.connectionId : -1, replicationProfileId, serializer);
        }
    }
}
#endif
