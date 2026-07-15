using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using Unity.Entities;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public class RuntimeReplicaStagingRealms
    {
        private readonly Dictionary<NetStoreRef, PreparedRealm> _preparedByStore = new();

        public int PreparedStoreCount => _preparedByStore.Count;

        public void AddPreparedStore(RuntimeStore store, IReadOnlyList<long> plannedObjectIds)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (plannedObjectIds == null)
                throw new ArgumentNullException(nameof(plannedObjectIds));
            if (store.Realm != StoreRealm.Client || store.StoreGeneration == 0 || store.Epoch == 0 || store.Retired)
                throw new InvalidOperationException($"RuntimeStore '{store.Id}' is not a prepared client replica realm.");

            var reference = new NetStoreRef(store.Id, store.StoreGeneration);
            if (_preparedByStore.ContainsKey(reference))
                throw new InvalidOperationException($"A staging realm for store '{reference}' is already prepared.");

            var objectIds = new HashSet<long>();
            for (var i = 0; i < plannedObjectIds.Count; i++)
            {
                var objectId = plannedObjectIds[i];
                var value = new NetObjectRef(reference, objectId);
                if (!value.IsValid)
                    throw new InvalidOperationException($"Staging realm '{reference}' contains invalid planned object id {objectId}.");
                if (!objectIds.Add(objectId))
                    throw new InvalidOperationException($"Staging realm '{reference}' contains duplicate planned object id {objectId}.");
            }

            _preparedByStore.Add(reference, new PreparedRealm(store, objectIds));
        }

        public bool RemovePreparedStore(RuntimeStore store)
        {
            if (store == null || store.StoreGeneration == 0)
                return false;

            var reference = new NetStoreRef(store.Id, store.StoreGeneration);
            if (!_preparedByStore.TryGetValue(reference, out var prepared)
                || !ReferenceEquals(prepared.Store, store))
            {
                return false;
            }

            return _preparedByStore.Remove(reference);
        }

        public bool ContainsPreparedStore(RuntimeStore store)
        {
            if (store == null || store.StoreGeneration == 0)
                return false;

            var reference = new NetStoreRef(store.Id, store.StoreGeneration);
            return _preparedByStore.TryGetValue(reference, out var prepared)
                   && ReferenceEquals(prepared.Store, store);
        }

        public bool TryGetPreparedStore(in NetStoreRef reference, out RuntimeStore store)
        {
            if (_preparedByStore.TryGetValue(reference, out var prepared)
                && !prepared.Store.Retired)
            {
                store = prepared.Store;
                return true;
            }

            store = null;
            return false;
        }

        public bool TryGetPreparedStore(
            in RuntimeInstance instance,
            out NetStoreRef reference,
            out RuntimeStore store)
        {
            foreach (var pair in _preparedByStore)
            {
                var prepared = pair.Value;
                if (prepared.Store.Retired
                    || !instance.StoreId.Equals(prepared.Store.Id)
                    || instance.Epoch != prepared.Store.Epoch)
                {
                    continue;
                }

                reference = pair.Key;
                store = prepared.Store;
                return true;
            }

            reference = default;
            store = null;
            return false;
        }

        public RuntimeNetworkPatchCodecContext CreateNetworkPatchContext()
        {
            return new RuntimeNetworkPatchCodecContext(EncodeReference, DecodeReference);
        }

        private NetObjectRef EncodeReference(RuntimeInstance value)
        {
            foreach (var pair in _preparedByStore)
            {
                var prepared = pair.Value;
                if (!value.StoreId.Equals(prepared.Store.Id) || value.Epoch != prepared.Store.Epoch)
                    continue;
                if (!prepared.ObjectIds.Contains(value.Id))
                    throw new InvalidOperationException($"Runtime reference '{value.StoreId}/{value.Id}' is not planned in staging realm '{pair.Key}'.");
                return new NetObjectRef(pair.Key, value.Id);
            }

            if (!RuntimeStores.TryGetRuntimeStore(value.StoreId, StoreRealm.Client, out var active)
                || !active.IsRuntimeInstanceActive(value)
                || !active.TryTakeRO(value.Id, out _))
            {
                throw new InvalidOperationException(
                    $"Runtime reference '{value.StoreId}/{value.Id}' epoch {value.Epoch} is neither staged nor active in the client realm.");
            }

            return NetObjectRef.FromRuntimeInstance(value, active.StoreGeneration);
        }

        private RuntimeInstance DecodeReference(NetObjectRef value)
        {
            if (_preparedByStore.TryGetValue(value.Store, out var prepared))
            {
                if (prepared.Store.Retired)
                    throw new InvalidOperationException($"Staging realm for network reference '{value}' is retired.");
                if (!prepared.ObjectIds.Contains(value.ObjectId))
                    throw new InvalidOperationException($"Network reference '{value}' is not present in the prepared staging plan.");
                return value.ToRuntimeInstance(prepared.Store.Epoch);
            }

            if (!RuntimeStores.TryGetRuntimeStore(
                    value.Store.StoreId,
                    value.Store.StoreGeneration,
                    StoreRealm.Client,
                    out var active)
                || !active.TryTakeRO(value.ObjectId, out _))
            {
                throw new InvalidOperationException($"Network reference '{value}' is neither staged nor active in the client realm.");
            }

            return value.ToRuntimeInstance(active.Epoch);
        }

        private class PreparedRealm
        {
            public readonly RuntimeStore Store;
            public readonly HashSet<long> ObjectIds;

            public PreparedRealm(RuntimeStore store, HashSet<long> objectIds)
            {
                Store = store;
                ObjectIds = objectIds;
            }
        }
    }

    public class RuntimeReplicaBaselineSpawnFactory
    {
        private readonly RuntimeSessionAssetCatalog _assetCatalog;
        private readonly GameAssetLibraryLock _assetLock;
        private readonly GameAssetTemplateCache _templateCache;

        protected RuntimeReplicaBaselineSpawnFactory() { }

        public RuntimeReplicaBaselineSpawnFactory(
            RuntimeSessionAssetCatalog assetCatalog,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache)
        {
            _assetCatalog = assetCatalog ?? throw new ArgumentNullException(nameof(assetCatalog));
            _assetLock = assetLock ?? throw new ArgumentNullException(nameof(assetLock));
            _templateCache = templateCache ?? throw new ArgumentNullException(nameof(templateCache));
        }

        public virtual ResolvedGameAssetReference ResolveRequiredAsset(uint assetNetId)
        {
            if (_assetCatalog == null)
                throw new InvalidOperationException("Replica baseline spawn factory has no session asset catalog.");
            return _assetCatalog.GetRequired(assetNetId);
        }

        public virtual GameRuntimeObject Spawn(
            RuntimeStore store,
            RuntimeStoreBaselineSpawn spawn,
            in ResolvedGameAssetReference resolvedAsset,
            RuntimePatchCodecContext networkPatchContext)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (spawn == null)
                throw new ArgumentNullException(nameof(spawn));
            if (_assetLock == null || _templateCache == null)
                throw new InvalidOperationException("Replica baseline spawn factory has no GameAsset materialization dependencies.");

            var instance = new GameAssetInstance(
                spawn.InstanceGuid,
                new GameAssetReference(resolvedAsset.ExactKey),
                spawn.Overrides);
            var runtimeObject = store.Spawn(
                spawn.ObjectId,
                instance,
                _assetLock,
                _templateCache,
                networkPatchContext);
            ValidateMaterializedOrigin(runtimeObject, resolvedAsset);
            return runtimeObject;
        }

        private static void ValidateMaterializedOrigin(
            GameRuntimeObject runtimeObject,
            in ResolvedGameAssetReference expected)
        {
            if (runtimeObject == null)
                throw new InvalidOperationException("RuntimeStore.Spawn returned a null runtime object.");

            var actual = runtimeObject.Origin.Asset;
            if (!KeysEqual(actual.ExactKey, expected.ExactKey)
                || actual.AssetGuid != expected.AssetGuid
                || !string.Equals(actual.MaterializedContentHash, expected.MaterializedContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Materialized GameAsset origin for object {runtimeObject.InstanceId} does not match the session asset catalog.");
            }
        }

        private static bool KeysEqual(GameAssetKey left, GameAssetKey right)
        {
            return string.Equals(left.Mod, right.Mod, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
        }
    }

    public enum RuntimeReplicaBaselineStageState : byte
    {
        Prepared = 1,
        Built = 2,
        Published = 3,
        Failed = 4,
        Aborted = 5,
    }

    public class RuntimeReplicaBaselineStage
    {
        private readonly RuntimeStoreBaselinePayload _payload;
        private readonly ResolvedGameAssetReference[] _resolvedAssets;
        private readonly RuntimeReplicaBaselineSpawnFactory _spawnFactory;
        private readonly RuntimeReplicaStagingRealms _stagingRealms;

        public readonly RuntimeStore Store;

        public NetStoreRef StoreReference => _payload.Store;
        public ulong BaselineId => _payload.BaselineId;
        public ulong StoreRevision => _payload.StoreRevision;
        public int SpawnCount => _payload.Spawns.Count;
        public RuntimeReplicaBaselineStageState State { get; private set; }
        public bool HasPreparedRealm => _stagingRealms.ContainsPreparedStore(Store);

        public RuntimeReplicaBaselineStage(
            RuntimeStoreBaselinePayload payload,
            ResolvedGameAssetReference[] resolvedAssets,
            RuntimeStore store,
            RuntimeReplicaBaselineSpawnFactory spawnFactory,
            RuntimeReplicaStagingRealms stagingRealms)
        {
            _payload = payload ?? throw new ArgumentNullException(nameof(payload));
            _resolvedAssets = resolvedAssets ?? throw new ArgumentNullException(nameof(resolvedAssets));
            Store = store ?? throw new ArgumentNullException(nameof(store));
            _spawnFactory = spawnFactory ?? throw new ArgumentNullException(nameof(spawnFactory));
            _stagingRealms = stagingRealms ?? throw new ArgumentNullException(nameof(stagingRealms));
            if (_resolvedAssets.Length != _payload.Spawns.Count)
                throw new InvalidOperationException("Replica baseline asset plan does not match its spawn plan.");
            State = RuntimeReplicaBaselineStageState.Prepared;
        }

        public void Build()
        {
            if (State == RuntimeReplicaBaselineStageState.Built)
                return;
            if (State != RuntimeReplicaBaselineStageState.Prepared)
                throw new InvalidOperationException($"Replica baseline stage '{StoreReference}' cannot build from state {State}.");

            try
            {
                var patchContext = _stagingRealms.CreateNetworkPatchContext();
                for (var i = 0; i < _payload.Spawns.Count; i++)
                {
                    var spawn = _payload.Spawns[i];
                    var runtimeObject = _spawnFactory.Spawn(Store, spawn, _resolvedAssets[i], patchContext);
                    if (runtimeObject == null || runtimeObject.InstanceId != spawn.ObjectId)
                        throw new InvalidOperationException($"Replica baseline spawn factory returned the wrong object for id {spawn.ObjectId}.");
                    if (!Store.TryTakeRO(spawn.ObjectId, out var registered) || !ReferenceEquals(registered, runtimeObject))
                        throw new InvalidOperationException($"Replica baseline object {spawn.ObjectId} was not registered in staging store '{Store.Id}'.");
                }

                for (var i = 0; i < _payload.Spawns.Count; i++)
                {
                    var spawn = _payload.Spawns[i];
                    if (spawn.ParentObjectId == RuntimeStoreStructureChange.NO_PARENT_ID)
                        continue;
                    if (!Store.AttachChild(spawn.ParentObjectId, spawn.ObjectId, spawn.SiblingIndex))
                    {
                        throw new InvalidOperationException(
                            $"Replica baseline could not attach object {spawn.ObjectId} to parent {spawn.ParentObjectId} at index {spawn.SiblingIndex}.");
                    }
                }

                for (var i = 0; i < _payload.Spawns.Count; i++)
                {
                    var spawn = _payload.Spawns[i];
                    if (spawn.ParentObjectId == RuntimeStoreStructureChange.NO_PARENT_ID)
                        Store.CreateEntitySubtree(spawn.ObjectId);
                }

                RuntimeStores.FinalizeReplicaBaseline(Store, _payload.StoreRevision);
                State = RuntimeReplicaBaselineStageState.Built;
            }
            catch
            {
                Fail();
                throw;
            }
        }

        public RuntimeStore Publish(StoreNetDir direction = StoreNetDir.S2C)
        {
            if (State != RuntimeReplicaBaselineStageState.Built)
                throw new InvalidOperationException($"Replica baseline stage '{StoreReference}' cannot publish from state {State}.");

            try
            {
                var published = RuntimeStores.PublishPreparedReplicaStore(Store, direction);
                _stagingRealms.RemovePreparedStore(Store);
                State = RuntimeReplicaBaselineStageState.Published;
                return published;
            }
            catch
            {
                Fail();
                throw;
            }
        }

        public void CompleteGroupedPublish()
        {
            if (State != RuntimeReplicaBaselineStageState.Built)
                throw new InvalidOperationException($"Replica baseline stage '{StoreReference}' cannot complete grouped publication from state {State}.");
            _stagingRealms.RemovePreparedStore(Store);
            State = RuntimeReplicaBaselineStageState.Published;
        }

        public void Abort()
        {
            if (State == RuntimeReplicaBaselineStageState.Published)
                throw new InvalidOperationException($"Published replica baseline '{StoreReference}' cannot be aborted.");
            if (State == RuntimeReplicaBaselineStageState.Aborted || State == RuntimeReplicaBaselineStageState.Failed)
                return;

            _stagingRealms.RemovePreparedStore(Store);
            Store.Retire();
            State = RuntimeReplicaBaselineStageState.Aborted;
        }

        private void Fail()
        {
            _stagingRealms.RemovePreparedStore(Store);
            Store.Retire();
            State = RuntimeReplicaBaselineStageState.Failed;
        }
    }

    public class RuntimeReplicaBaselineStager
    {
        private readonly World _world;
        private readonly RuntimeStoreBaselineCodec _codec;
        private readonly RuntimeReplicaBaselineSpawnFactory _spawnFactory;
        private readonly RuntimeReplicaStagingRealms _stagingRealms;

        public RuntimeReplicaBaselineStager(
            World world,
            RuntimeStoreBaselineCodec codec,
            RuntimeReplicaBaselineSpawnFactory spawnFactory,
            RuntimeReplicaStagingRealms stagingRealms)
        {
            if (world == null || !world.IsCreated)
                throw new ArgumentException("Replica baseline stager requires a valid ECS World.", nameof(world));
            _world = world;
            _codec = codec ?? throw new ArgumentNullException(nameof(codec));
            _spawnFactory = spawnFactory ?? throw new ArgumentNullException(nameof(spawnFactory));
            _stagingRealms = stagingRealms ?? throw new ArgumentNullException(nameof(stagingRealms));
        }

        public RuntimeReplicaBaselineStage Prepare(byte[] payload)
        {
            var decoded = _codec.Decode(payload);
            var resolvedAssets = new ResolvedGameAssetReference[decoded.Spawns.Count];
            var objectIds = new long[decoded.Spawns.Count];
            for (var i = 0; i < decoded.Spawns.Count; i++)
            {
                resolvedAssets[i] = _spawnFactory.ResolveRequiredAsset(decoded.Spawns[i].AssetNetId);
                objectIds[i] = decoded.Spawns[i].ObjectId;
            }

            RuntimeStore stagingStore = null;
            try
            {
                stagingStore = new RuntimeStore(decoded.Store.StoreId, StoreRealm.Client, _world);
                RuntimeStores.PrepareReplicaStore(stagingStore, decoded.Store.StoreGeneration);
                _stagingRealms.AddPreparedStore(stagingStore, objectIds);
                return new RuntimeReplicaBaselineStage(
                    decoded,
                    resolvedAssets,
                    stagingStore,
                    _spawnFactory,
                    _stagingRealms);
            }
            catch
            {
                if (stagingStore != null)
                {
                    _stagingRealms.RemovePreparedStore(stagingStore);
                    stagingStore.Retire();
                }
                throw;
            }
        }
    }

    public class RuntimeReplicaBaselineApplier
    {
        public void Build(RuntimeReplicaBaselineStage stage)
        {
            if (stage == null)
                throw new ArgumentNullException(nameof(stage));
            stage.Build();
        }

        public RuntimeStore Publish(RuntimeReplicaBaselineStage stage, StoreNetDir direction = StoreNetDir.S2C)
        {
            if (stage == null)
                throw new ArgumentNullException(nameof(stage));
            return stage.Publish(direction);
        }

        public RuntimeStore Apply(RuntimeReplicaBaselineStage stage, StoreNetDir direction = StoreNetDir.S2C)
        {
            Build(stage);
            return Publish(stage, direction);
        }

        public void PublishGroup(
            IReadOnlyList<RuntimeReplicaBaselineStage> stages,
            StoreNetDir direction = StoreNetDir.S2C)
        {
            if (stages == null)
                throw new ArgumentNullException(nameof(stages));
            if (stages.Count == 0)
                throw new ArgumentException("Replica baseline publication group cannot be empty.", nameof(stages));

            var stores = new RuntimeStore[stages.Count];
            for (var i = 0; i < stages.Count; i++)
            {
                var stage = stages[i] ?? throw new InvalidOperationException($"Replica baseline publication group contains a null stage at index {i}.");
                if (stage.State != RuntimeReplicaBaselineStageState.Built)
                    throw new InvalidOperationException($"Replica baseline stage '{stage.StoreReference}' is not fully built.");
                if (!stage.HasPreparedRealm)
                    throw new InvalidOperationException($"Replica baseline stage '{stage.StoreReference}' lost its prepared staging realm before grouped publication.");
                stores[i] = stage.Store;
            }

            RuntimeStores.PublishPreparedReplicaStores(stores, direction);
            for (var i = 0; i < stages.Count; i++)
            {
                stages[i].CompleteGroupedPublish();
            }
        }
    }
}
