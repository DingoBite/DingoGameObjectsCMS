using System.Collections.Generic;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public static class DirtyTraits<T>
    {
        public static readonly bool NoStruct = typeof(IStoreStructDirtyIgnore).IsAssignableFrom(typeof(T));
        public static readonly bool Data = typeof(IStoreDataDirty).IsAssignableFrom(typeof(T));
    }

    public interface IStoreStructDirtyIgnore { }

    public interface IStoreDataDirty { }

    public enum RemoveMode : byte
    {
        None = 0,
        Subtree = 1,
        NodeOnly_DetachChildrenToRoot = 2,
        NodeOnly_ReparentChildrenToParent = 3,
    }

    public enum RuntimeStoreOpKind : byte
    {
        Spawn = 1,
        Reparent = 2,
        Move = 3,
        Remove = 4,
    }

    public readonly struct RuntimeStructureDirty
    {
        public readonly RuntimeStoreOpKind Kind;
        public readonly long Id;
        public readonly long ParentId;
        public readonly int Index;
        public readonly RemoveMode RemoveMode;
        public readonly uint Order;

        private RuntimeStructureDirty(RuntimeStoreOpKind kind, long id, long parentId, int index, RemoveMode removeMode, uint order)
        {
            Kind = kind;
            Id = id;
            ParentId = parentId;
            Index = index;
            RemoveMode = removeMode;
            Order = order;
        }

        public static RuntimeStructureDirty Spawn(long id, long parentId, int insertIndex, uint order) => new(RuntimeStoreOpKind.Spawn, id, parentId, insertIndex, default, order);
        public static RuntimeStructureDirty Reparent(long id, long parentId, int insertIndex, uint order) => new(RuntimeStoreOpKind.Reparent, id, parentId, insertIndex, default, order);
        public static RuntimeStructureDirty Move(long id, long parentId, int newIndex, uint order) => new(RuntimeStoreOpKind.Move, id, parentId, newIndex, default, order);
        public static RuntimeStructureDirty Remove(long id, RemoveMode mode, uint order) => new(RuntimeStoreOpKind.Remove, id, parentId: 0, index: -1, removeMode: mode, order: order);
    }

    public readonly struct RuntimeStructureDirtyComparer : IComparer<RuntimeStructureDirty>
    {
        public int Compare(RuntimeStructureDirty a, RuntimeStructureDirty b) => a.Order.CompareTo(b.Order);
    }

    public readonly struct ComponentDirty
    {
        public readonly uint CompTypeId;

        public ComponentDirty(uint compTypeId)
        {
            CompTypeId = compTypeId;
        }
    }

    public enum CompStructOpKind : byte
    {
        Add = 1,
        Remove = 2
    }

    public readonly struct ComponentStructDirty
    {
        public readonly uint CompTypeId;
        public readonly CompStructOpKind Kind;

        public ComponentStructDirty(uint compTypeId, CompStructOpKind kind)
        {
            CompTypeId = compTypeId;
            Kind = kind;
        }
    }

    public readonly struct ObjectComponentDirty
    {
        public readonly long Id;
        public readonly ComponentDirty Dirty;

        public ObjectComponentDirty(long id, ComponentDirty dirty)
        {
            Id = id;
            Dirty = dirty;
        }
    }

    public readonly struct ObjectStructDirty
    {
        public readonly long Id;
        public readonly ComponentStructDirty Dirty;

        public ObjectStructDirty(long id, ComponentStructDirty dirty)
        {
            Id = id;
            Dirty = dirty;
        }
    }

    public readonly struct LongComparer : IComparer<long>
    {
        public int Compare(long x, long y) => x.CompareTo(y);
    }

    public readonly struct ObjectStructDirtyComparer : IComparer<ObjectStructDirty>
    {
        public int Compare(ObjectStructDirty a, ObjectStructDirty b)
        {
            var c = a.Id.CompareTo(b.Id);
            return c != 0 ? c : a.Dirty.CompTypeId.CompareTo(b.Dirty.CompTypeId);
        }
    }

    public readonly struct ObjectComponentDirtyComparer : IComparer<ObjectComponentDirty>
    {
        public int Compare(ObjectComponentDirty a, ObjectComponentDirty b)
        {
            var c = a.Id.CompareTo(b.Id);
            return c != 0 ? c : a.Dirty.CompTypeId.CompareTo(b.Dirty.CompTypeId);
        }
    }
}