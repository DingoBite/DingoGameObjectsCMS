#if MIRROR
using System;
using Mirror;
using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror
{
    [Serializable, Preserve]
    public struct RtSpawnMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public long Id;
        public long ParentId;
        public int InsertIndex;
        public byte[] Data;
        public uint ClientSeq;
    }

    [Serializable, Preserve]
    public struct RtAttachMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public long ParentId;
        public long ChildId;
        public int InsertIndex;
    }

    [Serializable, Preserve]
    public struct RtMoveMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public long ParentId;
        public long ChildId;
        public int NewIndex;
    }

    [Serializable, Preserve]
    public struct RtRemoveMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public long Id;
        public RemoveMode Mode;
    }

    [Serializable, Preserve]
    public struct RtMutateMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint Seq;
        public long TargetId;
        public uint CompTypeId;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtAppliedMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint Revision;
        public long TargetId;
        public uint CompTypeId;
        public byte[] Payload;
    }
}
#endif