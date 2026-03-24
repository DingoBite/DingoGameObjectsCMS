using System;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;

namespace DingoGameObjectsCMS.Systems
{
    public static class RuntimeStoreResolver
    {
        private static Func<FixedString32Bytes, StoreRealm, RuntimeStore> _resolver;
        
        public static void SetupResolver(Func<FixedString32Bytes, StoreRealm, RuntimeStore> resolver) => _resolver = resolver;
        public static RuntimeStore ResolveStore(this FixedString32Bytes storeId, StoreRealm realm) => _resolver(storeId, realm);
    }
}