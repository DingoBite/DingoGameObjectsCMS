using System;
using System.Collections.Generic;
using System.Linq;
using Bind;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DingoGameObjectsCMS.Stores
{
    public enum StoreNetDir : byte
    {
        None = 0,
        S2C = 1,
        C2S = 2,
    }

    public enum RuntimeExecutionRole : byte
    {
        OfflineAuthoritative = 0,
        ServerAuthoritative = 1,
        HostAuthoritative = 2,
        ClientReplica = 3,
    }

    public static class RuntimeStores
    {
        private static readonly Bind<RuntimeExecutionRole> _role = new();

        private static readonly Dictionary<FixedString32Bytes, RuntimeStore> _serverStoresById = new();
        private static readonly Dictionary<FixedString32Bytes, RuntimeStore> _clientStoresById = new();
        private static readonly Dictionary<FixedString32Bytes, uint> _serverStoreEpochById = new();
        private static readonly Dictionary<FixedString32Bytes, uint> _clientStoreEpochById = new();

        private static readonly BindDict<FixedString32Bytes, RuntimeStore> _serverStores = new();
        private static readonly BindDict<FixedString32Bytes, RuntimeStore> _clientStores = new();

        private static readonly Dictionary<FixedString32Bytes, StoreNetDir> _netDirById = new();
        private static World _world;

        public static IReadonlyBind<RuntimeExecutionRole> Role => _role;
        public static RuntimeExecutionRole Current => _role.V;
        public static bool IsAuthoritative => _role.V != RuntimeExecutionRole.ClientReplica;
        public static bool IsRemoteReplica => _role.V == RuntimeExecutionRole.ClientReplica;

        public static IReadonlyBind<IReadOnlyDictionary<FixedString32Bytes, RuntimeStore>> ServerStores => _serverStores;
        public static IReadonlyBind<IReadOnlyDictionary<FixedString32Bytes, RuntimeStore>> ClientStores => _clientStores;

        static RuntimeStores()
        {
            ResetState();
        }

        public static void SetupWorld(World world)
        {
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException("RuntimeStores requires a valid ECS World.");

            _world = world;

            foreach (var store in _serverStoresById.Values)
            {
                store.LinkWorld(world);
            }

            foreach (var store in _clientStoresById.Values)
            {
                store.LinkWorld(world);
            }
        }

        private static World RequireWorld()
        {
            if (_world == null || !_world.IsCreated)
                throw new InvalidOperationException("RuntimeStores requires SetupWorld(...) before store creation.");

            return _world;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnSubsystemRegistration()
        {
            ResetState();
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void InstallPlayModeReset()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                ResetState();
        }
#endif

        public static void ResetState()
        {
            foreach (var store in _serverStoresById.Values)
            {
                store.Retire();
            }

            foreach (var store in _clientStoresById.Values)
            {
                store.Retire();
            }

            _serverStoresById.Clear();
            _clientStoresById.Clear();
            _serverStoreEpochById.Clear();
            _clientStoreEpochById.Clear();
            _netDirById.Clear();

            _serverStores.V.Clear();
            _clientStores.V.Clear();
            _serverStores.V = _serverStores.V;
            _clientStores.V = _clientStores.V;

            _world = null;
            _role.V = RuntimeExecutionRole.OfflineAuthoritative;
        }

        public static void SetRole(RuntimeExecutionRole role)
        {
            _role.V = role;
        }

        private static Dictionary<FixedString32Bytes, RuntimeStore> GetDict(StoreRealm realm) => realm == StoreRealm.Server ? _serverStoresById : _clientStoresById;
        private static Dictionary<FixedString32Bytes, uint> GetEpochDict(StoreRealm realm) => realm == StoreRealm.Server ? _serverStoreEpochById : _clientStoreEpochById;
        private static BindDict<FixedString32Bytes, RuntimeStore> GetBind(StoreRealm realm) => realm == StoreRealm.Server ? _serverStores : _clientStores;

        private static uint NextEpoch(FixedString32Bytes id, StoreRealm realm)
        {
            var dict = GetEpochDict(realm);
            var next = dict.TryGetValue(id, out var current) ? current + 1u : 1u;
            dict[id] = next;
            return next;
        }

        public static bool TryGetRuntimeStore(FixedString32Bytes id, StoreRealm realm, out RuntimeStore runtimeStore) => GetDict(realm).TryGetValue(id, out runtimeStore);
        public static RuntimeStore GetRuntimeStore(FixedString32Bytes id, StoreRealm realm) => GetDict(realm).GetValueOrDefault(id);
        public static IEnumerable<RuntimeStore> EnumerateStores(StoreRealm realm) => GetDict(realm).Values;

        public static bool TryGetNetDir(FixedString32Bytes storeId, out StoreNetDir dir) => _netDirById.TryGetValue(storeId, out dir);
        public static StoreNetDir GetNetDir(FixedString32Bytes storeId) => _netDirById.GetValueOrDefault(storeId, StoreNetDir.None);

        public static IEnumerable<FixedString32Bytes> GetStores(StoreNetDir dir) => _netDirById.Where(p => p.Value == dir).Select(p => p.Key);

        public static IEnumerable<FixedString32Bytes> GetS2CStoresForSnapshot(StoreRealm realm = StoreRealm.Server)
        {
            var explicitStores = GetStores(StoreNetDir.S2C).ToArray();
            if (explicitStores.Length > 0)
                return explicitStores;

            return GetDict(realm).Keys;
        }

        public static IEnumerable<FixedString32Bytes> GetC2SStores(StoreRealm realm = StoreRealm.Server)
        {
            var explicitStores = GetStores(StoreNetDir.C2S).ToArray();
            if (explicitStores.Length > 0)
                return explicitStores;

            return GetDict(realm).Keys;
        }
        
        public static RuntimeStore SetRuntimeStore(RuntimeStore store, StoreNetDir dir = StoreNetDir.None, bool ensurePair = false)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));

            var key = store.Id;
            var realm = store.Realm;
            
            store.LinkWorld(RequireWorld());

            var dict = GetDict(realm);
            var bind = GetBind(realm);
            var epoch = NextEpoch(key, realm);

            if (dict.TryGetValue(key, out var previous) && !ReferenceEquals(previous, store))
                previous.Retire();

            store.AdoptSlotEpoch(epoch);
            dict[key] = store;
            bind.V[key] = store;
            bind.V = bind.V;

            if (dir != StoreNetDir.None)
                _netDirById[key] = dir;

            if (ensurePair)
                EnsureOtherRealmExists(key);

            return store;
        }

        public static RuntimeStore GetOrAddRuntimeStore(FixedString32Bytes key, StoreNetDir dir = StoreNetDir.None, StoreRealm realm = StoreRealm.Server, bool ensurePair = false)
        {
            var dict = GetDict(realm);
            var bind = GetBind(realm);

            if (dict.TryGetValue(key, out var existing))
            {
                if (dir != StoreNetDir.None)
                    _netDirById.TryAdd(key, dir);

                if (bind.V.TryAdd(key, existing))
                    bind.V = bind.V;

                if (ensurePair)
                    EnsureOtherRealmExists(key);

                return existing;
            }

            var store = new RuntimeStore(key, realm, RequireWorld());
            store.AdoptSlotEpoch(NextEpoch(key, realm));
            dict[key] = store;
            bind.V[key] = store;
            bind.V = bind.V;

            if (dir != StoreNetDir.None)
                _netDirById[key] = dir;

            if (ensurePair)
                EnsureOtherRealmExists(key);
            return store;
        }

        private static void EnsureOtherRealmExists(FixedString32Bytes key)
        {
            if (!_serverStoresById.ContainsKey(key))
                GetOrAddRuntimeStore(key, StoreNetDir.None, StoreRealm.Server, ensurePair: false);

            if (!_clientStoresById.ContainsKey(key))
                GetOrAddRuntimeStore(key, StoreNetDir.None, StoreRealm.Client, ensurePair: false);
        }
    }
}
