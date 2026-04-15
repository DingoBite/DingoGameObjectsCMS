using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;

namespace DingoGameObjectsCMS.Stores
{
    public static class RuntimeStoresExtensions
    {
        public static RuntimeStore ResolveStore(this FixedString32Bytes storeId, StoreRealm realm)
        {
            return RuntimeStores.GetRuntimeStore(storeId, realm);
        }

        public static RuntimeStore ResolveStore(this RuntimeInstance value, StoreRealm realm)
        {
            if (value.TryResolveActiveStore(realm, out var store))
                return store;
            return null;
        }

        public static bool TryResolveActiveStore(this RuntimeInstance value, StoreRealm realm, out RuntimeStore store)
        {
            if (!RuntimeStores.TryGetRuntimeStore(value.StoreId, realm, out store) || store == null)
                return false;

            if (!store.IsRuntimeInstanceActive(value))
            {
                store = null;
                return false;
            }

            return true;
        }

        public static RuntimeStore GetStore(this GameRuntimeObject value)
        {
            if (RuntimeStores.TryGetRuntimeStore(value.StoreId, value.Realm, out var store))
                return store;
            return null;
        }

        public static IReadOnlyList<long> GetChildren(this GameRuntimeObject value)
        {
            var store = value.GetStore();
            if (store == null || !store.TryTakeChildren(value.InstanceId, out var children))
                return null;
            return children;
        }

        public static IEnumerable<GameRuntimeObject> GetChildrenObjectsRO(this GameRuntimeObject value)
        {
            var store = value.GetStore();
            if (store == null || !store.TryTakeChildren(value.InstanceId, out var children))
            {
                yield break;
            }

            foreach (var child in children)
            {
                if (!store.TryTakeRO(child, out var childObj))
                    continue;
                yield return childObj;
            }
        }
    }
}
