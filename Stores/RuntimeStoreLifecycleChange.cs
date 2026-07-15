using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;

namespace DingoGameObjectsCMS.Stores
{
    public enum RuntimeStoreLifecycleKind : byte
    {
        Registered = 1,
        Replaced = 2,
        Removed = 3,
    }

    public readonly struct RuntimeStoreLifecycleChange
    {
        public readonly RuntimeStoreLifecycleKind Kind;
        public readonly FixedString32Bytes StoreId;
        public readonly StoreRealm Realm;
        public readonly uint StoreGeneration;
        public readonly RuntimeStore Store;
        public readonly RuntimeStore PreviousStore;

        private RuntimeStoreLifecycleChange(RuntimeStoreLifecycleKind kind, RuntimeStore store, RuntimeStore previousStore)
        {
            Kind = kind;
            StoreId = store.Id;
            Realm = store.Realm;
            StoreGeneration = store.StoreGeneration;
            Store = store;
            PreviousStore = previousStore;
        }

        public static RuntimeStoreLifecycleChange Registered(RuntimeStore store) => new(RuntimeStoreLifecycleKind.Registered, store, null);
        public static RuntimeStoreLifecycleChange Replaced(RuntimeStore store, RuntimeStore previousStore) => new(RuntimeStoreLifecycleKind.Replaced, store, previousStore);
        public static RuntimeStoreLifecycleChange Removed(RuntimeStore store) => new(RuntimeStoreLifecycleKind.Removed, store, null);
    }
}
