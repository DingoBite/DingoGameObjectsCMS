#if MIRROR
using System;
using Mirror;
using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror
{
    [Serializable, Preserve]
    public struct RtSpawnMsg : NetworkMessage
    {
        public Hash128 StoreId;
        public long Id;
        public long ParentId;
        public int InsertIndex;
        public byte[] Data;
        public uint ClientSeq;
    }

    [Serializable, Preserve]
    public struct RtAttachMsg : NetworkMessage
    {
        public Hash128 StoreId;
        public long ParentId;
        public long ChildId;
        public int InsertIndex;
    }

    [Serializable, Preserve]
    public struct RtMoveMsg : NetworkMessage
    {
        public Hash128 StoreId;
        public long ParentId;
        public long ChildId;
        public int NewIndex;
    }

    [Serializable, Preserve]
    public struct RtRemoveMsg : NetworkMessage
    {
        public Hash128 StoreId;
        public long Id;
        public RemoveMode Mode;
    }

    [Serializable, Preserve]
    public struct RtMutateMsg : NetworkMessage
    {
        public Hash128 StoreId;
        public uint Seq;
        public long TargetId;
        public uint CompTypeId;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtAppliedMsg : NetworkMessage
    {
        public Hash128 StoreId;
        public uint Revision;
        public long TargetId;
        public uint CompTypeId;
        public byte[] Payload;
    }

    public interface IRuntimeObjectSerializer
    {
        byte[] Serialize(GameRuntimeObject obj);
        void DeserializeInto(GameRuntimeObject obj, byte[] data);
    }
}
#endif