using System;
using System.Collections.Generic;
using System.Linq;
using Bind;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoUnityExtensions;
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
        private static readonly SafeMulticast<RuntimeStoreLifecycleChange> _storeLifecycleDispatcher = new();

        private static readonly Dictionary<FixedString32Bytes, RuntimeStore> _serverStoresById = new();
        private static readonly Dictionary<FixedString32Bytes, RuntimeStore> _clientStoresById = new();
        private static readonly Dictionary<FixedString32Bytes, uint> _serverStoreEpochById = new();
        private static readonly Dictionary<FixedString32Bytes, uint> _clientStoreEpochById = new();
        private static readonly Dictionary<FixedString32Bytes, uint> _serverStoreGenerationById = new();
        private static readonly Dictionary<FixedString32Bytes, uint> _clientStoreGenerationById = new();

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

        public static event Action<RuntimeStoreLifecycleChange> StoreLifecycleChanged
        {
            add => _storeLifecycleDispatcher.Subscribe(value);
            remove => _storeLifecycleDispatcher.Unsubscribe(value);
        }

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
            _serverStoreGenerationById.Clear();
            _clientStoreGenerationById.Clear();
            _netDirById.Clear();

            _serverStores.V.Clear();
            _clientStores.V.Clear();
            _serverStores.V = _serverStores.V;
            _clientStores.V = _clientStores.V;

            _world = null;
            _role.V = RuntimeExecutionRole.OfflineAuthoritative;
            _storeLifecycleDispatcher.Clear();
        }

        public static void SetRole(RuntimeExecutionRole role)
        {
            _role.V = role;
        }

        private static Dictionary<FixedString32Bytes, RuntimeStore> GetDict(StoreRealm realm) => realm == StoreRealm.Server ? _serverStoresById : _clientStoresById;
        private static Dictionary<FixedString32Bytes, uint> GetEpochDict(StoreRealm realm) => realm == StoreRealm.Server ? _serverStoreEpochById : _clientStoreEpochById;
        private static Dictionary<FixedString32Bytes, uint> GetGenerationDict(StoreRealm realm) => realm == StoreRealm.Server ? _serverStoreGenerationById : _clientStoreGenerationById;
        private static BindDict<FixedString32Bytes, RuntimeStore> GetBind(StoreRealm realm) => realm == StoreRealm.Server ? _serverStores : _clientStores;

        private static uint NextEpoch(FixedString32Bytes id, StoreRealm realm)
        {
            var dict = GetEpochDict(realm);
            var current = dict.GetValueOrDefault(id);
            if (current == uint.MaxValue)
                throw new InvalidOperationException($"RuntimeStore '{id}' in realm {realm} exhausted its epoch range.");

            var next = current + 1u;
            dict[id] = next;
            return next;
        }

        private static uint NextGeneration(FixedString32Bytes id, StoreRealm realm)
        {
            var dict = GetGenerationDict(realm);
            var current = dict.GetValueOrDefault(id);
            if (current == uint.MaxValue)
                throw new InvalidOperationException($"RuntimeStore '{id}' in realm {realm} exhausted its generation range.");

            var next = current + 1u;
            dict[id] = next;
            return next;
        }

        public static bool TryGetRuntimeStore(FixedString32Bytes id, StoreRealm realm, out RuntimeStore runtimeStore) => GetDict(realm).TryGetValue(id, out runtimeStore);
        public static bool TryGetRuntimeStore(FixedString32Bytes id, uint storeGeneration, StoreRealm realm, out RuntimeStore runtimeStore)
        {
            if (!GetDict(realm).TryGetValue(id, out runtimeStore) || runtimeStore.Retired || runtimeStore.StoreGeneration != storeGeneration)
            {
                runtimeStore = null;
                return false;
            }

            return true;
        }

        public static RuntimeStore GetRuntimeStore(FixedString32Bytes id, StoreRealm realm) => GetDict(realm).GetValueOrDefault(id);
        public static IEnumerable<RuntimeStore> EnumerateStores(StoreRealm realm) => GetDict(realm).Values;

        public static bool RemoveRuntimeStore(FixedString32Bytes id, StoreRealm realm)
        {
            var dict = GetDict(realm);
            if (!dict.Remove(id, out var store))
                return false;

            store.Retire();

            var bind = GetBind(realm);
            bind.V.Remove(id);
            bind.V = bind.V;

            if (!_serverStoresById.ContainsKey(id) && !_clientStoresById.ContainsKey(id))
                _netDirById.Remove(id);

            _storeLifecycleDispatcher.Invoke(RuntimeStoreLifecycleChange.Removed(store));

            return true;
        }

        public static bool RemoveRuntimeStore(FixedString32Bytes id, uint storeGeneration, StoreRealm realm)
        {
            if (!TryGetRuntimeStore(id, storeGeneration, realm, out _))
                return false;
            return RemoveRuntimeStore(id, realm);
        }

        public static bool TryGetNetDir(FixedString32Bytes storeId, out StoreNetDir dir) => _netDirById.TryGetValue(storeId, out dir);
        public static StoreNetDir GetNetDir(FixedString32Bytes storeId) => _netDirById.GetValueOrDefault(storeId, StoreNetDir.None);

        private static void ValidateAuthoritativeS2CStore(
            FixedString32Bytes storeId,
            StoreNetDir effectiveDirection,
            RuntimeStore candidate = null)
        {
            if (effectiveDirection != StoreNetDir.S2C)
                return;

            var authoritative = candidate != null && candidate.Realm == StoreRealm.Server
                ? candidate
                : _serverStoresById.GetValueOrDefault(storeId);
            authoritative?.ValidateGameAssetOriginsForS2CRegistration();
        }

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
            var effectiveDirection = dir != StoreNetDir.None ? dir : GetNetDir(key);

            ValidateAuthoritativeS2CStore(key, effectiveDirection, store);
            
            store.LinkWorld(RequireWorld());

            var dict = GetDict(realm);
            var bind = GetBind(realm);
            if (dict.TryGetValue(key, out var active) && ReferenceEquals(active, store))
            {
                if (dir != StoreNetDir.None)
                    _netDirById[key] = dir;

                if (bind.V.TryAdd(key, store))
                    bind.V = bind.V;

                if (ensurePair)
                    EnsureOtherRealmExists(key);

                return store;
            }

            var previous = active;
            var epoch = NextEpoch(key, realm);
            var generation = NextGeneration(key, realm);
            if (previous != null)
                previous.Retire();

            store.AdoptSlot(epoch, generation);
            dict[key] = store;
            bind.V[key] = store;
            bind.V = bind.V;

            if (dir != StoreNetDir.None)
                _netDirById[key] = dir;

            _storeLifecycleDispatcher.Invoke(previous == null ? RuntimeStoreLifecycleChange.Registered(store) : RuntimeStoreLifecycleChange.Replaced(store, previous));

            if (ensurePair)
                EnsureOtherRealmExists(key);

            return store;
        }

        /// <summary>
        /// Assigns the authoritative generation and its stable local epoch to
        /// a client staging store without exposing it through the registry.
        /// Rebaselining the same authoritative generation must preserve the
        /// epoch: RuntimeInstance values held by other active replica stores
        /// remain valid across the atomic store replacement. A new generation
        /// is the only operation that advances the local epoch.
        /// </summary>
        public static void PrepareReplicaStore(RuntimeStore store, uint authoritativeGeneration)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (store.Realm != StoreRealm.Client)
                throw new InvalidOperationException($"Replica staging requires realm {StoreRealm.Client}, received {store.Realm}.");
            if (authoritativeGeneration == 0)
                throw new ArgumentOutOfRangeException(nameof(authoritativeGeneration), "Authoritative store generation must be non-zero.");
            if (store.Retired)
                throw new InvalidOperationException($"Cannot prepare retired replica store '{store.Id}'.");
            if (store.StoreGeneration != 0 || store.Epoch != 0)
                throw new InvalidOperationException($"Replica staging store '{store.Id}' has already adopted a slot.");

            store.LinkWorld(RequireWorld());
            var generations = GetGenerationDict(StoreRealm.Client);
            var currentGeneration = generations.GetValueOrDefault(store.Id);
            if (authoritativeGeneration < currentGeneration)
            {
                throw new InvalidOperationException(
                    $"Replica store '{store.Id}' generation {authoritativeGeneration} is stale; local registry has seen generation {currentGeneration}.");
            }

            uint epoch;
            if (authoritativeGeneration > currentGeneration)
            {
                generations[store.Id] = authoritativeGeneration;
                epoch = NextEpoch(store.Id, StoreRealm.Client);
            }
            else
            {
                epoch = GetEpochDict(StoreRealm.Client).GetValueOrDefault(store.Id);
                if (epoch == 0)
                {
                    throw new InvalidOperationException(
                        $"Replica store '{store.Id}' generation {authoritativeGeneration} has no reserved local epoch.");
                }
            }

            store.AdoptSlot(epoch, authoritativeGeneration);
        }

        /// <summary>
        /// Atomically swaps a completely validated/prepared replica generation
        /// into the observable registry. Rebaseline of the same authoritative
        /// generation preserves its local epoch.
        /// </summary>
        public static RuntimeStore PublishPreparedReplicaStore(RuntimeStore store, StoreNetDir dir = StoreNetDir.S2C)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (store.Realm != StoreRealm.Client || store.StoreGeneration == 0 || store.Epoch == 0)
                throw new InvalidOperationException($"Replica store '{store.Id}' has not been prepared.");
            if (store.Retired)
                throw new InvalidOperationException($"Cannot publish retired replica store '{store.Id}'.");

            var generations = GetGenerationDict(StoreRealm.Client);
            var reservedGeneration = generations.GetValueOrDefault(store.Id);
            if (reservedGeneration != store.StoreGeneration)
            {
                throw new InvalidOperationException(
                    $"Replica store '{store.Id}' prepared generation {store.StoreGeneration}, but registry reservation is {reservedGeneration}.");
            }

            var dict = GetDict(StoreRealm.Client);
            var bind = GetBind(StoreRealm.Client);
            dict.TryGetValue(store.Id, out var previous);
            if (ReferenceEquals(previous, store))
                return store;
            if (previous != null && previous.StoreGeneration > store.StoreGeneration)
                throw new InvalidOperationException($"Cannot replace newer replica store '{store.Id}' generation {previous.StoreGeneration} with {store.StoreGeneration}.");

            var effectiveDirection = dir != StoreNetDir.None ? dir : GetNetDir(store.Id);
            ValidateAuthoritativeS2CStore(store.Id, effectiveDirection);

            dict[store.Id] = store;
            bind.V[store.Id] = store;
            bind.V = bind.V;
            if (dir != StoreNetDir.None)
                _netDirById[store.Id] = dir;

            previous?.Retire();
            _storeLifecycleDispatcher.Invoke(previous == null
                ? RuntimeStoreLifecycleChange.Registered(store)
                : RuntimeStoreLifecycleChange.Replaced(store, previous));
            return store;
        }

        /// <summary>
        /// Publishes a complete set of prepared replica stores as one registry
        /// swap. Every store is validated before any active dictionary or bind
        /// is changed; lifecycle notifications are emitted only after the full
        /// set is visible.
        /// </summary>
        public static void PublishPreparedReplicaStores(
            IReadOnlyList<RuntimeStore> stores,
            StoreNetDir dir = StoreNetDir.S2C)
        {
            if (stores == null)
                throw new ArgumentNullException(nameof(stores));
            if (stores.Count == 0)
                throw new ArgumentException("Replica publication group cannot be empty.", nameof(stores));

            var generations = GetGenerationDict(StoreRealm.Client);
            var dict = GetDict(StoreRealm.Client);
            var bind = GetBind(StoreRealm.Client);
            var seen = new HashSet<FixedString32Bytes>();
            var previous = new RuntimeStore[stores.Count];
            for (var i = 0; i < stores.Count; i++)
            {
                var store = stores[i] ?? throw new InvalidOperationException($"Replica publication group contains a null store at index {i}.");
                if (store.Realm != StoreRealm.Client || store.StoreGeneration == 0 || store.Epoch == 0)
                    throw new InvalidOperationException($"Replica store '{store.Id}' has not been prepared.");
                if (store.Retired)
                    throw new InvalidOperationException($"Cannot publish retired replica store '{store.Id}'.");
                if (!seen.Add(store.Id))
                    throw new InvalidOperationException($"Replica publication group contains duplicate store '{store.Id}'.");

                var reservedGeneration = generations.GetValueOrDefault(store.Id);
                if (reservedGeneration != store.StoreGeneration)
                {
                    throw new InvalidOperationException(
                        $"Replica store '{store.Id}' prepared generation {store.StoreGeneration}, but registry reservation is {reservedGeneration}.");
                }

                dict.TryGetValue(store.Id, out var previousStore);
                previous[i] = previousStore;
                if (previous[i] != null && previous[i].StoreGeneration > store.StoreGeneration)
                {
                    throw new InvalidOperationException(
                        $"Cannot replace newer replica store '{store.Id}' generation {previous[i].StoreGeneration} with {store.StoreGeneration}.");
                }

                var effectiveDirection = dir != StoreNetDir.None ? dir : GetNetDir(store.Id);
                ValidateAuthoritativeS2CStore(store.Id, effectiveDirection);
            }

            for (var i = 0; i < stores.Count; i++)
            {
                var store = stores[i];
                dict[store.Id] = store;
                bind.V[store.Id] = store;
                if (dir != StoreNetDir.None)
                    _netDirById[store.Id] = dir;
            }
            bind.V = bind.V;

            for (var i = 0; i < stores.Count; i++)
            {
                if (!ReferenceEquals(previous[i], stores[i]))
                    previous[i]?.Retire();
            }

            for (var i = 0; i < stores.Count; i++)
            {
                if (ReferenceEquals(previous[i], stores[i]))
                    continue;
                _storeLifecycleDispatcher.Invoke(previous[i] == null
                    ? RuntimeStoreLifecycleChange.Registered(stores[i])
                    : RuntimeStoreLifecycleChange.Replaced(stores[i], previous[i]));
            }
        }

        public static void FinalizeReplicaBaseline(RuntimeStore store, ulong storeRevision)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            store.FinalizeReplicaBaseline(storeRevision);
        }

        public static RuntimeStore GetOrAddRuntimeStore(FixedString32Bytes key, StoreNetDir dir = StoreNetDir.None, StoreRealm realm = StoreRealm.Server, bool ensurePair = false)
        {
            var dict = GetDict(realm);
            var bind = GetBind(realm);

            if (dict.TryGetValue(key, out var existing))
            {
                var effectiveDirection = _netDirById.TryGetValue(key, out var configuredDirection)
                    ? configuredDirection
                    : dir;
                ValidateAuthoritativeS2CStore(key, effectiveDirection, existing);

                if (dir != StoreNetDir.None)
                    _netDirById.TryAdd(key, dir);

                if (bind.V.TryAdd(key, existing))
                    bind.V = bind.V;

                if (ensurePair)
                    EnsureOtherRealmExists(key);

                return existing;
            }

            var store = new RuntimeStore(key, realm, RequireWorld());
            var effectiveNewDirection = dir != StoreNetDir.None ? dir : GetNetDir(key);
            ValidateAuthoritativeS2CStore(key, effectiveNewDirection, store);
            store.AdoptSlot(NextEpoch(key, realm), NextGeneration(key, realm));
            dict[key] = store;
            bind.V[key] = store;
            bind.V = bind.V;

            if (dir != StoreNetDir.None)
                _netDirById[key] = dir;

            _storeLifecycleDispatcher.Invoke(RuntimeStoreLifecycleChange.Registered(store));

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
