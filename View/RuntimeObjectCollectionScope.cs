using System;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.View
{
    public enum RuntimeObjectCollectionScopeKind : byte
    {
        Parents = 0,
        DirectChildren = 1,
    }

    public readonly struct RuntimeObjectCollectionScope
    {
        public readonly RuntimeObjectCollectionScopeKind Kind;
        public readonly long ParentId;

        private RuntimeObjectCollectionScope(RuntimeObjectCollectionScopeKind kind, long parentId)
        {
            Kind = kind;
            ParentId = parentId;
        }

        public static RuntimeObjectCollectionScope Parents => default;

        public static RuntimeObjectCollectionScope DirectChildren(long parentId) =>
            new(RuntimeObjectCollectionScopeKind.DirectChildren, parentId);

        public bool SameAs(RuntimeObjectCollectionScope other) =>
            Kind == other.Kind && ParentId == other.ParentId;

        public void Validate()
        {
            switch (Kind)
            {
                case RuntimeObjectCollectionScopeKind.Parents:
                    return;
                case RuntimeObjectCollectionScopeKind.DirectChildren when ParentId > RuntimeStore.STORE_ROOT_OBJECT_ID:
                    return;
                case RuntimeObjectCollectionScopeKind.DirectChildren:
                    throw new ArgumentOutOfRangeException(nameof(ParentId), ParentId, "Direct children scope requires a user runtime object id.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null);
            }
        }
    }
}
