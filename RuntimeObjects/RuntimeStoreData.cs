using System;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public enum RemoveMode : byte
    {
        Subtree = 0,
        NodeOnly_DetachChildrenToRoot = 1,
        NodeOnly_ReparentChildrenToParent = 2,
    }

    public enum RuntimeStoreOpKind : byte
    {
        Spawn = 1,
        Reparent = 2,
        Move = 3,
        Remove = 4,
    }

    [Serializable, Preserve]
    public readonly struct RuntimeStoreOp
    {
        public readonly RuntimeStoreOpKind Kind;
        public readonly long Id;
        public readonly long ParentId;
        public readonly int Index;
        public readonly RemoveMode RemoveMode;

        private RuntimeStoreOp(RuntimeStoreOpKind kind, long id, long parentId, int index, RemoveMode removeMode)
        {
            Kind = kind;
            Id = id;
            ParentId = parentId;
            Index = index;
            RemoveMode = removeMode;
        }

        public static RuntimeStoreOp Spawn(long id, long parentId, int insertIndex) => new(RuntimeStoreOpKind.Spawn, id, parentId, insertIndex, default);
        public static RuntimeStoreOp Reparent(long id, long parentId, int insertIndex) => new(RuntimeStoreOpKind.Reparent, id, parentId, insertIndex, default);
        public static RuntimeStoreOp Move(long id, long parentId, int newIndex) => new(RuntimeStoreOpKind.Move, id, parentId, newIndex, default);
        public static RuntimeStoreOp Remove(long id, RemoveMode mode) => new(RuntimeStoreOpKind.Remove, id, parentId: 0, index: -1, removeMode: mode);
    }

}