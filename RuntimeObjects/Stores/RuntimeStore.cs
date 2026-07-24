using System;
using System.Collections.Generic;
using Bind;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Stores;
using DingoProjectAppStructure.Core.Model;
using DingoUnityExtensions;
using Unity.Collections;
using Unity.Entities;
using Unity.Profiling;
using UnityEngine;
using Hash128 = UnityEngine.Hash128;

namespace DingoGameObjectsCMS.RuntimeObjects.Stores
{
    public readonly struct RuntimeStoreEntries
    {
        private readonly Dictionary<long, GameRuntimeObject> _entries;

        internal RuntimeStoreEntries(Dictionary<long, GameRuntimeObject> entries)
        {
            _entries = entries;
        }

        public int Count => _entries?.Count ?? 0;

        public Dictionary<long, GameRuntimeObject>.Enumerator GetEnumerator()
        {
            return _entries != null
                ? _entries.GetEnumerator()
                : default;
        }
    }

    public class RuntimeStore : AppModelBase
    {
        public const int UPDATE_ORDER = 1_000_000;
        public const long STORE_ROOT_OBJECT_ID = 0;
        public const long FIRST_USER_OBJECT_ID = STORE_ROOT_OBJECT_ID + 1;

        private static readonly ProfilerMarker FLUSH_AND_COLLECTION_MARKER = new("SnakeAndMice.RuntimeStore.FlushAndCollection");
        private static readonly ProfilerMarker STRUCTURAL_BURST_MARKER = new("SnakeAndMice.RuntimeStore.StructuralBurst");
        
        private readonly SafeMulticast<NativeArray<RuntimeStructureDirty>> _structureChangesDispatcher = new();
        private readonly SafeMulticast<NativeArray<ObjectStructDirty>> _componentStructureChangesDispatcher = new();
        private readonly SafeMulticast<NativeArray<ObjectComponentDirty>> _componentChangesDispatcher = new();
        private readonly SafeMulticast<RuntimeStoreCommittedBatch> _committedBatchDispatcher = new();
        private readonly SafeMulticast<RuntimeStoreDirtyPublish> _dirtyPublishCompletedDispatcher = new();

        public event Action<NativeArray<RuntimeStructureDirty>> StructureChanges
        {
            add => _structureChangesDispatcher.Subscribe(value);
            remove => _structureChangesDispatcher.Unsubscribe(value);
        }

        public event Action<NativeArray<ObjectStructDirty>> ComponentStructureChanges
        {
            add => _componentStructureChangesDispatcher.Subscribe(value);
            remove => _componentStructureChangesDispatcher.Unsubscribe(value);
        }

        public event Action<NativeArray<ObjectComponentDirty>> ComponentChanges
        {
            add => _componentChangesDispatcher.Subscribe(value);
            remove => _componentChangesDispatcher.Unsubscribe(value);
        }

        public event Action<RuntimeStoreCommittedBatch> CommittedBatch
        {
            add => _committedBatchDispatcher.Subscribe(value);
            remove => _committedBatchDispatcher.Unsubscribe(value);
        }

        public event Action<RuntimeStoreDirtyPublish> DirtyPublishCompleted
        {
            add => _dirtyPublishCompletedDispatcher.Subscribe(value);
            remove => _dirtyPublishCompletedDispatcher.Unsubscribe(value);
        }

        
        public readonly FixedString32Bytes Id;
        public readonly StoreRealm Realm;

        private World _world;
        private long _lastId = FIRST_USER_OBJECT_ID;
        private uint _order;

        private readonly BindDict<long, GameRuntimeObject> _all = new();
        private readonly BindDict<long, GameRuntimeObject> _parents = new();

        private readonly Dictionary<long, Entity> _entityById = new();
        private readonly Dictionary<Entity, long> _idByEntity = new();
        private readonly HashSet<Entity> _entitiesPendingDestroy = new();
        private readonly List<Entity> _entitiesPendingDestroyWork = new();
        private readonly Dictionary<Hash128, long> _idByGuid = new();
        private readonly HashSet<long> _seenEntityLinks = new();

        private readonly Dictionary<long, long> _parentByChild = new();
        private readonly Dictionary<long, List<long>> _childrenByParent = new();
        private readonly HashSet<long> _hierarchyProjectionDirty = new();
        private readonly List<long> _hierarchyProjectionWork = new();

        private HashSet<long> _pendingTouched = new();
        private HashSet<long> _processingTouched = new();
        private readonly HashSet<PresentationComponentDirtyKey> _presentationComponentChanges = new();
        private bool _scheduled;
        private bool _flushInProgress;
        private bool _rescheduleRequested;
        private bool _suppressReplicationThisFlush;
        private ulong _netApplyTargetRevision;
        private bool _netApplyRevisionCommitted;
        private bool _netApplyScopeActive;
        private bool _entityLinkPassActive;

        private readonly HashSet<long> _cycleVisited = new();

        private readonly List<RuntimeStoreStructureChange> _structureChanges = new();
        private bool _structureChangesNeedSort;
        
        public IReadonlyBind<IReadOnlyDictionary<long, GameRuntimeObject>> All => _all;
        public RuntimeStoreEntries Entries => new(_all.V);
        public IReadonlyBind<IReadOnlyDictionary<long, GameRuntimeObject>> Parents => _parents;
        public bool ReplicationSuppressed => _suppressReplicationThisFlush;
        public World World => _world;
        public uint Epoch { get; private set; }
        public uint StoreGeneration { get; private set; }
        public ulong StoreRevision { get; private set; }
        public ulong DirtyPublishVersion { get; private set; }
        public ulong TotalTouchedSortCount => 0;
        public ulong TotalFullSortCount { get; private set; }
        public bool Retired { get; private set; }
        
        public RuntimeStore(FixedString32Bytes id, StoreRealm realm, World world)
        {
            Id = id;
            Realm = realm;
            LinkWorld(world);
        }

        internal void AdoptSlot(uint epoch, uint storeGeneration)
        {
            if (epoch == 0)
                throw new ArgumentOutOfRangeException(nameof(epoch));
            if (storeGeneration == 0)
                throw new ArgumentOutOfRangeException(nameof(storeGeneration));

            Epoch = epoch;
            StoreGeneration = storeGeneration;
            StoreRevision = 0;
            DirtyPublishVersion = 0;
            Retired = false;
        }

        internal void FinalizeReplicaBaseline(ulong storeRevision)
        {
            if (Realm != StoreRealm.Client)
                throw new InvalidOperationException($"RuntimeStore '{Id}' can finalize a replica baseline only in the client realm.");
            if (Retired || StoreGeneration == 0 || Epoch == 0)
                throw new InvalidOperationException($"RuntimeStore '{Id}' must adopt a live replica slot before baseline finalization.");
            if (_flushInProgress)
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot finalize a baseline while a flush is in progress.");

            // Baseline construction is staging work, not a local mutation
            // batch. The fully built store becomes observable only when the
            // registry swaps it in, so no dirty stream is emitted here.
            foreach (var runtimeObject in _all.V.Values)
                runtimeObject?.ClearDirty();
            _structureChanges.Clear();
            _structureChangesNeedSort = false;
            _pendingTouched.Clear();
            _processingTouched.Clear();
            _presentationComponentChanges.Clear();
            _order = 0;
            StoreRevision = storeRevision;
            DirtyPublishVersion = 0;
            _suppressReplicationThisFlush = false;
            _netApplyTargetRevision = 0;
            _netApplyRevisionCommitted = false;
            _netApplyScopeActive = false;
            _rescheduleRequested = false;
            _scheduled = false;
            CoroutineParent.RemoveLateUpdater(this);
            _all.V = _all.V;
            _parents.V = _parents.V;
        }

        internal void Retire()
        {
            if (Retired)
                return;

            Retired = true;
            _structureChangesDispatcher.Clear();
            _componentStructureChangesDispatcher.Clear();
            _componentChangesDispatcher.Clear();
            _committedBatchDispatcher.Clear();
            _dirtyPublishCompletedDispatcher.Clear();

            try
            {
                var objects = new List<GameRuntimeObject>(_all.V.Values);
                foreach (var obj in objects)
                {
                    if (obj == null)
                        continue;

                    try
                    {
                        obj.Destroy();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                EntityCommandBuffer? destroyBuffer = null;
                if (_world != null && _world.IsCreated)
                {
                    try
                    {
                        // Component cleanup records its ECBs above. Creating the
                        // destroy buffer afterwards guarantees RemoveFromEntity
                        // commands play before the final entity destruction.
                        destroyBuffer = _world.TakeGRCEditingECB();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                foreach (var obj in objects)
                {
                    if (obj == null)
                        continue;

                    try
                    {
                        if (destroyBuffer.HasValue && _entityById.TryGetValue(obj.InstanceId, out var entity))
                        {
                            var buffer = destroyBuffer.Value;
                            ScheduleEntityDestroy(entity, buffer);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }

                foreach (var obj in objects)
                {
                    if (obj == null)
                        continue;

                    try
                    {
                        obj.ClearRuntimeContext();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
            finally
            {
                _entityLinkPassActive = false;
                _seenEntityLinks.Clear();
                _entityById.Clear();
                _idByEntity.Clear();
                _entitiesPendingDestroy.Clear();
                _entitiesPendingDestroyWork.Clear();
                _idByGuid.Clear();
                _parentByChild.Clear();
                _childrenByParent.Clear();
                _hierarchyProjectionDirty.Clear();
                _hierarchyProjectionWork.Clear();
                _pendingTouched.Clear();
                _processingTouched.Clear();
                _cycleVisited.Clear();
                _structureChanges.Clear();
                _structureChangesNeedSort = false;
                _presentationComponentChanges.Clear();
                CoroutineParent.RemoveLateUpdater(this);
                _scheduled = false;
                _flushInProgress = false;
                _rescheduleRequested = false;
                _suppressReplicationThisFlush = false;
                _netApplyTargetRevision = 0;
                _netApplyRevisionCommitted = false;
                _netApplyScopeActive = false;
                _order = 0;
                _lastId = FIRST_USER_OBJECT_ID;
                _parents.V.Clear();
                _all.V.Clear();
                _structureChangesDispatcher.Clear();
                _componentStructureChangesDispatcher.Clear();
                _componentChangesDispatcher.Clear();
                _committedBatchDispatcher.Clear();
                _dirtyPublishCompletedDispatcher.Clear();
            }
        }

        public bool IsRuntimeInstanceActive(RuntimeInstance runtimeInstance)
        {
            return !Retired && runtimeInstance.StoreId.Equals(Id) && runtimeInstance.Epoch == Epoch;
        }
        
        public void LinkWorld(World world)
        {
            if (world == null || !world.IsCreated)
                throw new InvalidOperationException($"RuntimeStore '{Id}' requires a valid ECS World.");

            if (_world != null && _world != world && _all.V.Count > 0)
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot change ECS World after runtime objects have been created.");

            _world = world;
        }

        private World RequireWorld()
        {
            if (_world == null || !_world.IsCreated)
                throw new InvalidOperationException($"RuntimeStore '{Id}' requires a valid ECS World.");

            return _world;
        }

        public IEnumerable<GameRuntimeObject> EnumerateGRONoRoot()
        {
            foreach (var pair in _parents.V)
            {
                if (pair.Key == STORE_ROOT_OBJECT_ID)
                    continue;

                yield return pair.Value;
            }
        }

        private void EnsureRootCreated()
        {
            if (_all.V.ContainsKey(STORE_ROOT_OBJECT_ID) || _entityById.ContainsKey(STORE_ROOT_OBJECT_ID))
                return;
            var storeRoot = CreateDetached(STORE_ROOT_OBJECT_ID);
            AddToRoot(storeRoot.InstanceId);
        }
        
        private uint NextOrder() => ++_order;

        private GameRuntimeObject CreateDetached() => CreateDetached(_lastId++);

        private GameRuntimeObject CreateDetached(long id)
        {
            if (id < 0)
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot create a runtime object with negative id {id}.");

            if (_all.V.ContainsKey(id) || _entityById.ContainsKey(id))
                throw new InvalidOperationException($"RuntimeStore '{Id}' already contains runtime object id {id}.");

            var world = RequireWorld();
            var obj = new GameRuntimeObject
            {
                InstanceId = id,
                StoreId = Id,
                Realm = Realm
            };

            obj.LinkRuntimeContext(this, world);
            RegisterGuid(id, obj);
            _all.V[id] = obj;

            if (_lastId <= id)
                _lastId = id + 1;

            MarkTouchedUpToRoot(id);
            MarkHierarchyProjectionDirty(id);
            return obj;
        }

        private bool RequiresExactGameAssetOrigin =>
            Realm == StoreRealm.Server
            && !Retired
            && RuntimeStores.GetNetDir(Id) == StoreNetDir.S2C
            && RuntimeStores.TryGetRuntimeStore(Id, StoreRealm.Server, out var active)
            && ReferenceEquals(active, this);

        private static bool HasExactGameAssetOrigin(GameRuntimeObject runtimeObject)
        {
            if (runtimeObject == null || !runtimeObject.GUID.isValid)
                return false;

            var origin = runtimeObject.Origin;
            return origin.InstanceGuid.isValid
                   && origin.InstanceGuid == runtimeObject.GUID
                   && origin.Asset.AssetGuid.isValid
                   && !string.IsNullOrWhiteSpace(origin.Asset.ExactKey.Version)
                   && !string.IsNullOrWhiteSpace(origin.Asset.MaterializedContentHash);
        }

        private static InvalidOperationException MissingGameAssetOrigin(
            FixedString32Bytes storeId,
            long objectId,
            string operation)
        {
            return new InvalidOperationException(
                $"RuntimeStore '{storeId}' cannot {operation} runtime object {objectId} in an authoritative S2C store without an exact GA origin. " +
                "Use RuntimeStore.Spawn(...) with a concrete GA, or use the explicit Empty GA for an intentionally empty object.");
        }

        private void EnsureExactGameAssetOrigin(GameRuntimeObject runtimeObject, string operation)
        {
            if (runtimeObject == null
                || runtimeObject.InstanceId == STORE_ROOT_OBJECT_ID
                || !RequiresExactGameAssetOrigin)
            {
                return;
            }

            if (!HasExactGameAssetOrigin(runtimeObject))
                throw MissingGameAssetOrigin(Id, runtimeObject.InstanceId, operation);
        }

        private void EnsureOriginlessCreationAllowed(string operation)
        {
            if (RequiresExactGameAssetOrigin)
                throw MissingGameAssetOrigin(Id, _lastId, operation);
        }

        internal void ValidateGameAssetOriginsForS2CRegistration()
        {
            if (Realm != StoreRealm.Server)
                return;

            foreach (var pair in _all.V)
            {
                if (pair.Key == STORE_ROOT_OBJECT_ID)
                    continue;
                if (!HasExactGameAssetOrigin(pair.Value))
                    throw MissingGameAssetOrigin(Id, pair.Key, "register");
            }
        }

        public GameRuntimeObject Create()
        {
            EnsureOriginlessCreationAllowed("create");
            var obj = CreateDetached();
            AddToRoot(obj.InstanceId);
            return obj;
        }

        public GameRuntimeObject CreateChild(long parentId, int insertIndex = -1)
        {
            EnsureOriginlessCreationAllowed("create child");
            var child = CreateDetached();
            AttachChild(parentId, child.InstanceId, insertIndex);
            return child;
        }

        public GameRuntimeObject Spawn(
            GameAssetInstance instance,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            long? parentId = null,
            int insertIndex = -1)
        {
            return Spawn(_lastId, instance, assetLock, templateCache, null, parentId, insertIndex);
        }

        public GameRuntimeObject Spawn(
            GameAssetInstance instance,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            RuntimePatchCodecContext patchContext,
            long? parentId = null,
            int insertIndex = -1)
        {
            return Spawn(_lastId, instance, assetLock, templateCache, patchContext, parentId, insertIndex);
        }

        public GameRuntimeObject Spawn(
            long objectId,
            GameAssetInstance instance,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            long? parentId = null,
            int insertIndex = -1)
        {
            return Spawn(objectId, instance, assetLock, templateCache, null, parentId, insertIndex);
        }

        public GameRuntimeObject Spawn(
            long objectId,
            GameAssetInstance instance,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            RuntimePatchCodecContext patchContext,
            long? parentId = null,
            int insertIndex = -1)
        {
            return SpawnMaterialized(
                objectId,
                templateCache,
                parentId,
                insertIndex,
                () => patchContext == null
                    ? templateCache.Materialize(instance, assetLock)
                    : templateCache.Materialize(instance, assetLock, patchContext));
        }

#if UNITY_EDITOR
        /// <summary>
        /// Publishes an already materialized exact-origin baseline. Kept
        /// editor-only so playground assets cannot bypass the immutable lock in
        /// a player or a network session.
        /// </summary>
        public GameRuntimeObject SpawnLocalAuthoringBaseline(
            GameRuntimeObject materialized,
            GameAssetTemplateCache templateCache,
            long? parentId = null,
            int insertIndex = -1)
        {
            if (materialized == null)
                throw new ArgumentNullException(nameof(materialized));
            return SpawnMaterialized(
                _lastId,
                templateCache,
                parentId,
                insertIndex,
                () => materialized);
        }
#endif

        /// <summary>
        /// Publishes a lane-projected replica spawn atomically. This is kept
        /// separate from authored Spawn so network code cannot accidentally
        /// route hot presence through normal semantic patch materialization.
        /// </summary>
        public GameRuntimeObject SpawnProjected(
            long objectId,
            GameAssetInstance instance,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            RuntimePatchCodecContext patchContext,
            Func<uint, RuntimeComponentPatchProjectionMode> selectMode,
            long? parentId = null,
            int insertIndex = -1)
        {
            return SpawnMaterialized(
                objectId,
                templateCache,
                parentId,
                insertIndex,
                () => templateCache.MaterializeProjected(
                    instance,
                    assetLock,
                    patchContext,
                    selectMode));
        }

        private GameRuntimeObject SpawnMaterialized(
            long objectId,
            GameAssetTemplateCache templateCache,
            long? parentId,
            int insertIndex,
            Func<GameRuntimeObject> materialize)
        {
            if (Retired)
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot spawn into a retired generation.");
            if (objectId < FIRST_USER_OBJECT_ID)
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot spawn reserved runtime object id {objectId}.");
            if (_all.V.ContainsKey(objectId) || _entityById.ContainsKey(objectId))
                throw new InvalidOperationException($"RuntimeStore '{Id}' already contains runtime object id {objectId}.");
            if (parentId.HasValue && !_all.V.ContainsKey(parentId.Value))
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot spawn runtime object {objectId} under missing parent {parentId.Value}.");
            if (templateCache == null)
                throw new ArgumentNullException(nameof(templateCache));
            if (materialize == null)
                throw new ArgumentNullException(nameof(materialize));

            if (parentId.HasValue)
                EnsureExactGameAssetOrigin(_all.V[parentId.Value], "attach child to");

            // Materialization and patch validation happen before the object is
            // observable through the store. A failed baseline or patch cannot
            // publish a partially initialized GRO.
            var runtimeObject = materialize();
            runtimeObject.InstanceId = objectId;
            EnsureExactGameAssetOrigin(runtimeObject, "spawn");

            if (!TryRegisterExternalObject(runtimeObject))
                throw new InvalidOperationException($"RuntimeStore '{Id}' could not register runtime object {objectId} with guid {runtimeObject.GUID}.");

            try
            {
                var published = parentId.HasValue
                    ? AttachChild(parentId.Value, objectId, insertIndex)
                    : PublishMaterializedRoot(objectId);
                if (!published)
                    throw new InvalidOperationException($"RuntimeStore '{Id}' could not publish materialized runtime object {objectId}.");
            }
            catch
            {
                RollbackUnpublishedRegistration(runtimeObject);
                throw;
            }

            return runtimeObject;
        }

        public Entity CreateEntitySubtree(long rootId)
        {
            if (!_all.V.ContainsKey(rootId))
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot project missing subtree root {rootId}.");

            var pending = new List<long> { rootId };
            var rootEntity = Entity.Null;

            while (pending.Count > 0)
            {
                var lastIndex = pending.Count - 1;
                var currentId = pending[lastIndex];
                pending.RemoveAt(lastIndex);

                if (!_all.V.TryGetValue(currentId, out var current))
                    throw new InvalidOperationException($"RuntimeStore '{Id}' hierarchy references missing runtime object {currentId}.");

                var entity = current.CreateEntity();
                MarkHierarchyProjectionDirty(currentId);
                if (currentId == rootId)
                    rootEntity = entity;

                if (!_childrenByParent.TryGetValue(currentId, out var children))
                    continue;

                for (var i = children.Count - 1; i >= 0; i--)
                {
                    pending.Add(children[i]);
                }
            }

            return rootEntity;
        }

        public void PublishRootExisting(long id)
        {
            AddToRoot(id);
        }

        public bool TryAddExternalObject(GameRuntimeObject value)
        {
            return TryRegisterExternalObject(value);
        }

        private bool TryRegisterExternalObject(GameRuntimeObject value)
        {
            if (value == null || value.InstanceId < 0)
                return false;

            if (value.InstanceId != STORE_ROOT_OBJECT_ID
                && RequiresExactGameAssetOrigin
                && !HasExactGameAssetOrigin(value))
            {
                return false;
            }

            var id = value.InstanceId;
            var world = RequireWorld();
            if (_all.V.ContainsKey(id) || _entityById.ContainsKey(id))
                return false;

            if (value.GUID.isValid && _idByGuid.TryGetValue(value.GUID, out var existingGuidId) && existingGuidId != id)
                return false;

            value.StoreId = Id;
            value.Realm = Realm;
            value.LinkRuntimeContext(this, world);
            RegisterGuid(id, value);
            _all.V[id] = value;

            if (_lastId <= id)
                _lastId = id + 1;

            MarkTouchedUpToRoot(id);
            MarkHierarchyProjectionDirty(id);
            ScheduleFlush();
            return true;
        }

        private void RollbackUnpublishedRegistration(GameRuntimeObject runtimeObject)
        {
            if (runtimeObject == null
                || IsPublished(runtimeObject.InstanceId)
                || !_all.V.TryGetValue(runtimeObject.InstanceId, out var registered)
                || !ReferenceEquals(registered, runtimeObject))
            {
                return;
            }

            _all.V.Remove(runtimeObject.InstanceId);
            UnregisterGuid(runtimeObject.GUID, runtimeObject.InstanceId);
            _pendingTouched.Remove(runtimeObject.InstanceId);
            _processingTouched.Remove(runtimeObject.InstanceId);
            _hierarchyProjectionDirty.Remove(runtimeObject.InstanceId);
            runtimeObject.ClearRuntimeContext();
        }

        private bool PublishMaterializedRoot(long id)
        {
            if (!_all.V.ContainsKey(id))
                return false;

            AddToRoot(id);
            return true;
        }

        public GameRuntimeObject TakeRootRW()
        {
            EnsureRootCreated();
            if (TryTakeRW(STORE_ROOT_OBJECT_ID, out var gameRuntimeObject))
                return gameRuntimeObject;
            return null;
        }

        public GameRuntimeObject TakeRootRO()
        {
            EnsureRootCreated();
            if (TryTakeRO(STORE_ROOT_OBJECT_ID, out var gameRuntimeObject))
                return gameRuntimeObject;
            return null;
        }

        public GameRuntimeObject TakeRW(long id)
        {
            if (TryTakeRW(id, out var gro))
                return gro;
            return null;
        }

        public GameRuntimeObject TakeRO(long id)
        {
            if (TryTakeRO(id, out var gro))
                return gro;
            return null;
        }

        public GameRuntimeObject TakeRW(Hash128 guid)
        {
            if (TryTakeRW(guid, out var gro))
                return gro;
            return null;
        }

        public GameRuntimeObject TakeRO(Hash128 guid)
        {
            if (TryTakeRO(guid, out var gro))
                return gro;
            return null;
        }

        public bool TryTakeRW(long id, out GameRuntimeObject gameRuntimeObject)
        {
            if (!_all.V.TryGetValue(id, out gameRuntimeObject))
                return false;

            MarkTouchedUpToRoot(id);
            return true;
        }

        public bool TryTakeRO(long id, out GameRuntimeObject gameRuntimeObject) => _all.V.TryGetValue(id, out gameRuntimeObject);

        public bool TryTakeRW(Hash128 guid, out GameRuntimeObject gameRuntimeObject)
        {
            if (!TryGetId(guid, out var id))
            {
                gameRuntimeObject = null;
                return false;
            }

            return TryTakeRW(id, out gameRuntimeObject);
        }

        public bool TryTakeRO(Hash128 guid, out GameRuntimeObject gameRuntimeObject)
        {
            if (!TryGetId(guid, out var id))
            {
                gameRuntimeObject = null;
                return false;
            }

            return TryTakeRO(id, out gameRuntimeObject);
        }

        public bool TryGetId(Hash128 guid, out long id)
        {
            id = -1;
            return guid.isValid && _idByGuid.TryGetValue(guid, out id);
        }

        public void SetDirty<T>(long id) where T : GameRuntimeComponent
        {
            SetDirty(id, typeof(T).GetId());
        }

        public void SetDirty<T>(RuntimeInstance runtimeInstance) where T : GameRuntimeComponent
        {
            SetDirty(runtimeInstance, typeof(T).GetId());
        }

        public bool TrySetDirty<T>(RuntimeInstance runtimeInstance) where T : GameRuntimeComponent
        {
            return TrySetDirty(runtimeInstance, typeof(T).GetId());
        }

        /// <summary>
        /// Publishes a component refresh for a client replica without recording an
        /// authoritative store mutation. This is reserved for presentation state
        /// projected from transient replica ECS data, such as interpolated motion.
        /// </summary>
        public bool TrySetReplicaPresentationDirty<T>(RuntimeInstance runtimeInstance)
            where T : GameRuntimeComponent
        {
            return TrySetReplicaPresentationDirty(runtimeInstance, typeof(T).GetId());
        }

        public bool TrySetReplicaPresentationDirty(RuntimeInstance runtimeInstance, uint compTypeId)
        {
            if (Realm != StoreRealm.Client || !IsRuntimeInstanceActive(runtimeInstance))
                return false;

            if (!_all.V.TryGetValue(runtimeInstance.Id, out var obj)
                || !obj.TryGetById(compTypeId, out _))
            {
                return false;
            }

            _presentationComponentChanges.Add(new PresentationComponentDirtyKey(runtimeInstance.Id, compTypeId));
            ScheduleFlush();
            return true;
        }

        public void SetDirty(RuntimeInstance runtimeInstance, uint compTypeId)
        {
            if (!runtimeInstance.StoreId.Equals(Id))
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot mark dirty a runtime instance from store '{runtimeInstance.StoreId}'.");

            if (Retired)
                throw new InvalidOperationException($"RuntimeStore '{Id}' epoch {Epoch} is retired.");

            if (runtimeInstance.Epoch != Epoch)
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot mark dirty runtime instance {runtimeInstance.Id} from epoch {runtimeInstance.Epoch} while active epoch is {Epoch}.");

            SetDirty(runtimeInstance.Id, compTypeId);
        }

        public bool TrySetDirty(RuntimeInstance runtimeInstance, uint compTypeId)
        {
            if (!IsRuntimeInstanceActive(runtimeInstance))
                return false;

            if (!_all.V.TryGetValue(runtimeInstance.Id, out var obj))
                return false;

            if (!obj.TryGetById(compTypeId, out _))
                return false;

            obj.SetDirtyById(compTypeId);
            SetObjectTouched(runtimeInstance.Id);
            return true;
        }

        public void SetDirty(long id, uint compTypeId)
        {
            if (Retired)
                throw new InvalidOperationException($"RuntimeStore '{Id}' epoch {Epoch} is retired.");

            if (!_all.V.TryGetValue(id, out var obj))
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot mark dirty component type id {compTypeId} for missing object {id}.");

            if (!obj.TryGetById(compTypeId, out _))
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot mark dirty component type id {compTypeId} for object {id}.");

            obj.SetDirtyById(compTypeId);
            SetObjectTouched(id);
        }

        public bool Remove(long id) => Remove(id, RemoveMode.Subtree, out _);
        public bool Remove(long id, out Entity entity) => Remove(id, RemoveMode.Subtree, out entity);
        public bool Remove(long id, RemoveMode mode, out Entity entity) => RemoveInternal(id, mode, out entity, true);

        public bool Remove(long id, EntityCommandBuffer unusedEcb) => Remove(id, RemoveMode.Subtree, out _);
        public bool Remove(long id, EntityCommandBuffer unusedEcb, out Entity entity) => Remove(id, RemoveMode.Subtree, out entity);
        public bool Remove(long id, RemoveMode mode, EntityCommandBuffer unusedEcb, out Entity entity) => RemoveInternal(id, mode, out entity, true);

        private bool RemoveInternal(long id, RemoveMode mode, out Entity entity, bool recordOp)
        {
            var wasPublished = IsPublished(id);
            var objectGuid = TakeObjectGuid(id);
            TakeStructurePosition(id, out var oldParentId, out var oldIndex);
            MarkHierarchyRelationsDirty(id);

            if (recordOp && wasPublished)
                RecordOp(RuntimeStoreStructureChange.Remove(id, objectGuid, oldParentId, oldIndex, mode, NextOrder()));

            _parents.V.Remove(id);
            entity = Entity.Null;

            MarkTouchedUpToRoot(id);

            if (!_all.V.TryGetValue(id, out var obj))
            {
                TryUnlinkEntity(id, out entity);
                DetachChildInternal(id);
                RemoveChildrenIndexOnly(id);
                return false;
            }

            TryGetEntity(id, out entity);

            var hadParent = oldParentId != RuntimeStoreStructureChange.NO_PARENT_ID;
            var parentId = oldParentId;
            DetachChildInternal(id);

            if (_childrenByParent.TryGetValue(id, out var children) && children.Count > 0)
            {
                var toProcess = new List<long>(children);

                switch (mode)
                {
                    case RemoveMode.Subtree:
                        foreach (var child in toProcess)
                        {
                            RemoveInternal(child, RemoveMode.Subtree, out _, recordOp: false);
                        }

                        break;

                    case RemoveMode.NodeOnly_DetachChildrenToRoot:
                        foreach (var child in toProcess)
                        {
                            DetachChild(child);
                        }

                        break;

                    case RemoveMode.NodeOnly_ReparentChildrenToParent when hadParent && _all.V.ContainsKey(parentId):
                        foreach (var child in toProcess)
                        {
                            AttachChild(parentId, child, insertIndex: -1);
                        }

                        break;

                    case RemoveMode.NodeOnly_ReparentChildrenToParent:
                        foreach (var child in toProcess)
                        {
                            DetachChild(child);
                        }

                        break;

                    default: throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
                }
            }

            try
            {
                obj.Destroy();
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            _all.V.Remove(id);
            UnregisterGuid(obj.GUID, id);

            if (entity != Entity.Null && _world != null && _world.IsCreated)
            {
                try
                {
                    var buffer = _world.TakeGRCEditingECB();
                    ScheduleEntityDestroy(entity, buffer);
                }
                catch (Exception e)
                {
                    ClearEntityPendingDestroy(entity);
                    Debug.LogException(e);
                }
            }

            UnlinkEntity(id);
            obj.ClearRuntimeContext();
            ScheduleFlush();
            return true;
        }


        public void BeginEntityLinkPass()
        {
            if (Retired)
                return;

            _entityLinkPassActive = true;
            _seenEntityLinks.Clear();
        }

        public void EndEntityLinkPass()
        {
            if (Retired)
                return;

            if (!_entityLinkPassActive)
                return;

            _entityLinkPassActive = false;

            if (_entityById.Count == 0)
            {
                _seenEntityLinks.Clear();
                return;
            }

            List<long> staleIds = null;
            foreach (var id in _entityById.Keys)
            {
                if (_seenEntityLinks.Contains(id))
                    continue;

                staleIds ??= new List<long>();
                staleIds.Add(id);
            }

            _seenEntityLinks.Clear();

            if (staleIds == null)
                return;

            foreach (var id in staleIds)
            {
                UnlinkEntity(id);
            }
        }

        public void LinkEntity(long id, Entity entity)
        {
            if (Retired)
                return;

            if (id < 0 || entity == Entity.Null)
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot link invalid runtime object id {id} or a null entity.");

            if (!_all.V.TryGetValue(id, out var runtimeObject))
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot link entity {entity} to missing runtime object {id}.");

            var linkChanged = !_entityById.TryGetValue(id, out var existingEntity) || existingEntity != entity;
            if (existingEntity != Entity.Null && existingEntity != entity)
                _idByEntity.Remove(existingEntity);

            if (_idByEntity.TryGetValue(entity, out var existingId) && existingId != id)
            {
                _entityById.Remove(existingId);
                if (_all.V.TryGetValue(existingId, out var previouslyLinked))
                    previouslyLinked.ClearEntityLink();
                MarkHierarchyRelationsDirty(existingId);
            }

            _entityById[id] = entity;
            _idByEntity[entity] = id;

            if (_entityLinkPassActive)
                _seenEntityLinks.Add(id);
            runtimeObject.LinkToEntity(entity);

            if (linkChanged)
                MarkHierarchyRelationsDirty(id);
        }

        public bool TryGetEntity(long id, out Entity e) => _entityById.TryGetValue(id, out e);

        public bool IsEntityPendingDestroy(Entity entity)
        {
            if (_entitiesPendingDestroy.Contains(entity))
                return true;
            if (_world == null || !_world.IsCreated)
                return false;

            var entityManager = _world.EntityManager;
            return entityManager.Exists(entity)
                   && entityManager.HasComponent<RuntimeEntityDestroyState>(entity)
                   && entityManager.GetComponentData<RuntimeEntityDestroyState>(entity).Pending != 0;
        }

        private void ScheduleEntityDestroy(Entity entity, EntityCommandBuffer buffer)
        {
            if (!TryMarkEntityPendingDestroy(entity))
                return;

            try
            {
                buffer.DestroyEntity(entity);
            }
            catch
            {
                ClearEntityPendingDestroy(entity);
                throw;
            }
        }

        private bool TryMarkEntityPendingDestroy(Entity entity)
        {
            if (entity == Entity.Null)
                return false;
            if (_world == null || !_world.IsCreated)
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot schedule Entity destruction without a valid ECS World.");

            var entityManager = _world.EntityManager;
            if (!entityManager.Exists(entity))
                return false;
            if (!entityManager.HasComponent<RuntimeEntityDestroyState>(entity))
                throw new InvalidOperationException($"RuntimeStore '{Id}' cannot destroy Entity {entity} without {nameof(RuntimeEntityDestroyState)}.");

            var state = entityManager.GetComponentData<RuntimeEntityDestroyState>(entity);
            if (state.Pending != 0)
            {
                _entitiesPendingDestroy.Add(entity);
                return false;
            }

            state.Pending = 1;
            entityManager.SetComponentData(entity, state);
            _entitiesPendingDestroy.Add(entity);
            return true;
        }

        private void ClearEntityPendingDestroy(Entity entity)
        {
            _entitiesPendingDestroy.Remove(entity);
            if (_world == null || !_world.IsCreated)
                return;

            var entityManager = _world.EntityManager;
            if (!entityManager.Exists(entity) || !entityManager.HasComponent<RuntimeEntityDestroyState>(entity))
                return;

            var state = entityManager.GetComponentData<RuntimeEntityDestroyState>(entity);
            state.Pending = 0;
            entityManager.SetComponentData(entity, state);
        }

        public void PruneDestroyedEntities(EntityManager entityManager)
        {
            if (_entitiesPendingDestroy.Count == 0)
                return;

            _entitiesPendingDestroyWork.Clear();
            foreach (var entity in _entitiesPendingDestroy)
            {
                if (!entityManager.Exists(entity))
                    _entitiesPendingDestroyWork.Add(entity);
            }

            foreach (var entity in _entitiesPendingDestroyWork)
            {
                _entitiesPendingDestroy.Remove(entity);
            }

            _entitiesPendingDestroyWork.Clear();
        }

        public bool TryTakeParentRW(long childId, out GameRuntimeObject parent)
        {
            if (!_parentByChild.TryGetValue(childId, out var parentId) || !_all.V.TryGetValue(parentId, out parent))
            {
                parent = null;
                return false;
            }

            MarkTouchedUpToRoot(parentId);
            return true;
        }

        public bool TryTakeParentRO(long childId, out GameRuntimeObject parent)
        {
            if (!_parentByChild.TryGetValue(childId, out var parentId) || !_all.V.TryGetValue(parentId, out parent))
            {
                parent = null;
                return false;
            }

            return true;
        }

        public bool TryTakeChildren(long parentId, out IReadOnlyList<long> children)
        {
            if (_childrenByParent.TryGetValue(parentId, out var list))
            {
                children = list;
                return true;
            }

            children = Array.Empty<long>();
            return false;
        }

        public bool AttachChild(long parentId, long childId, int insertIndex = -1)
        {
            if (parentId == childId)
                return false;

            if (!_all.V.TryGetValue(parentId, out var parent) || !_all.V.TryGetValue(childId, out var child))
                return false;

            EnsureExactGameAssetOrigin(parent, "attach child to");
            EnsureExactGameAssetOrigin(child, "attach");

            if (WouldCreateCycle(parentId, childId))
                return false;

            var wasPublished = IsPublished(childId);
            var objectGuid = TakeObjectGuid(childId);
            TakeStructurePosition(childId, out var oldParentId, out var oldIndex);

            _parents.V.Remove(childId);

            if (_parentByChild.ContainsKey(childId))
            {
                if (oldParentId == parentId)
                {
                    if (insertIndex >= 0)
                        return MoveChild(parentId, childId, insertIndex);

                    return true;
                }

                DetachChildInternal(childId);
            }

            _parentByChild[childId] = parentId;

            if (!_childrenByParent.TryGetValue(parentId, out var list))
            {
                list = new List<long>(capacity: 4);
                _childrenByParent[parentId] = list;
            }

            if (insertIndex < 0 || insertIndex > list.Count)
                insertIndex = list.Count;

            list.Insert(insertIndex, childId);

            MarkHierarchyProjectionDirty(parentId);
            MarkHierarchyProjectionDirty(childId);

            RecordOp(wasPublished
                ? RuntimeStoreStructureChange.Reparent(childId, objectGuid, oldParentId, oldIndex, parentId, insertIndex, NextOrder())
                : RuntimeStoreStructureChange.Spawn(childId, objectGuid, parentId, insertIndex, NextOrder()));

            MarkTouchedUpToRoot(parentId);
            ScheduleFlush();
            return true;
        }

        public bool DetachChild(long childId)
        {
            if (!_all.V.TryGetValue(childId, out var child))
                return false;

            EnsureExactGameAssetOrigin(child, "detach");

            var wasPublished = IsPublished(childId);
            TakeStructurePosition(childId, out var oldParentId, out var oldIndex);
            if (!DetachChildInternal(childId))
                return false;

            AddToRoot(childId, oldParentId, oldIndex, wasPublished);
            return true;
        }

        public bool MoveChild(long parentId, long childId, int newIndex)
        {
            if (!_childrenByParent.TryGetValue(parentId, out var list))
                return false;

            var oldIndex = list.IndexOf(childId);
            if (oldIndex < 0)
                return false;

            if (!_all.V.TryGetValue(parentId, out var parent) || !_all.V.TryGetValue(childId, out var child))
                return false;

            EnsureExactGameAssetOrigin(parent, "move child under");
            EnsureExactGameAssetOrigin(child, "move");

            if (newIndex < 0)
                newIndex = 0;
            if (newIndex >= list.Count)
                newIndex = list.Count - 1;

            if (oldIndex == newIndex)
                return true;

            list.RemoveAt(oldIndex);
            if (newIndex > list.Count)
                newIndex = list.Count;
            list.Insert(newIndex, childId);

            MarkHierarchyProjectionDirty(parentId);
            MarkHierarchyProjectionDirty(childId);

            RecordOp(RuntimeStoreStructureChange.Move(childId, TakeObjectGuid(childId), parentId, oldIndex, newIndex, NextOrder()));

            MarkTouchedUpToRoot(parentId);
            ScheduleFlush();
            return true;
        }

        public void BeginNetApply()
        {
            if (StoreRevision == ulong.MaxValue)
                throw new InvalidOperationException($"RuntimeStore '{Id}' exhausted its revision range.");
            BeginNetApply(StoreRevision + 1);
        }

        public void BeginNetApply(ulong authoritativeRevision)
        {
            if (_netApplyScopeActive)
                throw new InvalidOperationException($"RuntimeStore '{Id}' already has an active network apply scope.");
            if (authoritativeRevision <= StoreRevision)
                throw new ArgumentOutOfRangeException(nameof(authoritativeRevision), "Network apply revision must advance the active store revision.");

            _netApplyScopeActive = true;
            _netApplyTargetRevision = authoritativeRevision;
            _netApplyRevisionCommitted = false;
            _suppressReplicationThisFlush = true;
            ScheduleFlush();
        }

        public void BeginNetProjectionApply(ulong authoritativeRevision)
        {
            if (_netApplyScopeActive)
                throw new InvalidOperationException($"RuntimeStore '{Id}' already has an active network apply scope.");
            if (authoritativeRevision < StoreRevision)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(authoritativeRevision),
                    "Network projection apply cannot move the active store revision backwards.");
            }

            _netApplyScopeActive = true;
            _netApplyTargetRevision = authoritativeRevision;
            _netApplyRevisionCommitted = false;
            _suppressReplicationThisFlush = true;
            ScheduleFlush();
        }

        public void CommitNetApply()
        {
            if (!_netApplyScopeActive)
                throw new InvalidOperationException($"RuntimeStore '{Id}' has no active network apply scope.");
            StoreRevision = _netApplyTargetRevision;
            _netApplyRevisionCommitted = true;

            // Reliable envelopes can be delivered back-to-back in one Mirror
            // update (and initial-baseline deltas are drained synchronously).
            // Complete this transaction now so the next envelope observes a
            // closed scope and the committed revision. Flush still publishes
            // exactly one dirty/committed batch for this envelope and its
            // finally block owns resetting all net-apply state.
            Flush();
        }

        public void AbortNetApply()
        {
            _suppressReplicationThisFlush = false;
            _netApplyTargetRevision = 0;
            _netApplyRevisionCommitted = false;
            _netApplyScopeActive = false;
        }

        private bool IsPublished(long id) => _parents.V.ContainsKey(id) || _parentByChild.ContainsKey(id);

        private void RecordOp(in RuntimeStoreStructureChange op)
        {
            if (_structureChanges.Count > 0 && _structureChanges[_structureChanges.Count - 1].Order > op.Order)
                _structureChangesNeedSort = true;

            _structureChanges.Add(op);
            ScheduleFlush();
        }

        private Hash128 TakeObjectGuid(long id) => _all.V.TryGetValue(id, out var runtimeObject) ? runtimeObject.GUID : default;

        private void TakeStructurePosition(long id, out long parentId, out int index)
        {
            if (!_parentByChild.TryGetValue(id, out parentId))
            {
                parentId = RuntimeStoreStructureChange.NO_PARENT_ID;
                index = RuntimeStoreStructureChange.NO_INDEX;
                return;
            }

            index = _childrenByParent.TryGetValue(parentId, out var children) ? children.IndexOf(id) : RuntimeStoreStructureChange.NO_INDEX;
        }

        private bool WouldCreateCycle(long newParentId, long childId)
        {
            _cycleVisited.Clear();

            var cur = newParentId;
            while (true)
            {
                if (cur == childId)
                    return true;

                if (!_cycleVisited.Add(cur))
                    return true;

                if (!_parentByChild.TryGetValue(cur, out var p))
                    return false;

                if (p == cur)
                    return true;

                cur = p;
            }
        }

        private bool DetachChildInternal(long childId)
        {
            if (!_parentByChild.Remove(childId, out var parentId))
                return false;

            if (_childrenByParent.TryGetValue(parentId, out var list))
            {
                var idx = list.IndexOf(childId);
                if (idx >= 0)
                    list.RemoveAt(idx);

                if (list.Count == 0)
                    _childrenByParent.Remove(parentId);
            }

            MarkTouchedUpToRoot(parentId);
            MarkTouchedUpToRoot(childId);
            MarkHierarchyProjectionDirty(parentId);
            MarkHierarchyProjectionDirty(childId);
            return true;
        }

        private void RemoveChildrenIndexOnly(long parentId)
        {
            if (!_childrenByParent.Remove(parentId, out var list))
                return;

            foreach (var c in list)
            {
                _parentByChild.Remove(c);
                MarkHierarchyProjectionDirty(c);
            }

            MarkHierarchyProjectionDirty(parentId);
        }

        private void AddToRoot(long id, long oldParentId = RuntimeStoreStructureChange.NO_PARENT_ID, int oldIndex = RuntimeStoreStructureChange.NO_INDEX, bool wasPublishedBefore = false)
        {
            if (!_all.V.TryGetValue(id, out var obj))
                return;

            EnsureExactGameAssetOrigin(obj, "publish");

            if (_parents.V.ContainsKey(id))
            {
                _parents.V[id] = obj;
                return;
            }

            var wasPublished = wasPublishedBefore || IsPublished(id);

            _parents.V[id] = obj;

            MarkHierarchyProjectionDirty(id);

            RecordOp(wasPublished
                ? RuntimeStoreStructureChange.Reparent(id, obj.GUID, oldParentId, oldIndex, RuntimeStoreStructureChange.NO_PARENT_ID, RuntimeStoreStructureChange.NO_INDEX, NextOrder())
                : RuntimeStoreStructureChange.Spawn(id, obj.GUID, RuntimeStoreStructureChange.NO_PARENT_ID, RuntimeStoreStructureChange.NO_INDEX, NextOrder()));

            MarkTouchedUpToRoot(id);
            ScheduleFlush();
        }

        private long GetRootId(long id)
        {
            var cur = id;

            for (var i = 0; i < 1024; i++)
            {
                if (!_parentByChild.TryGetValue(cur, out var p))
                    return cur;

                if (p == cur)
                    return cur;

                cur = p;
            }

            return cur;
        }

        public void SetObjectTouched(long id) => MarkTouchedUpToRoot(id);

        public void FlushEntityHierarchy(EntityManager entityManager)
        {
            if (Retired || _hierarchyProjectionDirty.Count == 0)
                return;

            _hierarchyProjectionWork.Clear();
            foreach (var id in _hierarchyProjectionDirty)
            {
                _hierarchyProjectionWork.Add(id);
            }

            foreach (var id in _hierarchyProjectionWork)
            {
                if (!_all.V.TryGetValue(id, out var runtimeObject))
                {
                    _hierarchyProjectionDirty.Remove(id);
                    continue;
                }

                if (!runtimeObject.HasEntityProjection)
                {
                    _hierarchyProjectionDirty.Remove(id);
                    continue;
                }

                if (!_entityById.TryGetValue(id, out var entity) || !entityManager.Exists(entity))
                    continue;

                if (!entityManager.HasBuffer<RuntimeChildEntity>(entity))
                    throw new InvalidOperationException($"Runtime entity for object {id} in store '{Id}' has no RuntimeChildEntity buffer.");

                var childBuffer = entityManager.GetBuffer<RuntimeChildEntity>(entity);
                childBuffer.Clear();

                if (_childrenByParent.TryGetValue(id, out var children))
                {
                    foreach (var childId in children)
                    {
                        if (!_all.V.TryGetValue(childId, out var child))
                            throw new InvalidOperationException($"RuntimeStore '{Id}' hierarchy references missing child {childId} from parent {id}.");

                        if (!_entityById.TryGetValue(childId, out var childEntity) || !entityManager.Exists(childEntity))
                            continue;

                        childBuffer.Add(new RuntimeChildEntity
                        {
                            Instance = child.RuntimeInstance,
                            Entity = childEntity
                        });
                    }
                }

                if (_parentByChild.TryGetValue(id, out var parentId)
                    && _all.V.TryGetValue(parentId, out var parent)
                    && _entityById.TryGetValue(parentId, out var parentEntity)
                    && entityManager.Exists(parentEntity))
                {
                    var parentData = new RuntimeParentEntity
                    {
                        Instance = parent.RuntimeInstance,
                        Entity = parentEntity
                    };

                    if (entityManager.HasComponent<RuntimeParentEntity>(entity))
                        entityManager.SetComponentData(entity, parentData);
                    else
                        entityManager.AddComponentData(entity, parentData);
                }
                else if (entityManager.HasComponent<RuntimeParentEntity>(entity))
                {
                    entityManager.RemoveComponent<RuntimeParentEntity>(entity);
                }

                _hierarchyProjectionDirty.Remove(id);
            }

            _hierarchyProjectionWork.Clear();
        }

        private void MarkHierarchyProjectionDirty(long id)
        {
            if (id >= 0)
                _hierarchyProjectionDirty.Add(id);
        }

        private void MarkHierarchyRelationsDirty(long id)
        {
            MarkHierarchyProjectionDirty(id);

            if (_parentByChild.TryGetValue(id, out var parentId))
                MarkHierarchyProjectionDirty(parentId);

            if (!_childrenByParent.TryGetValue(id, out var children))
                return;

            foreach (var childId in children)
            {
                MarkHierarchyProjectionDirty(childId);
            }
        }

        private void MarkTouchedUpToRoot(long id)
        {
            var rootId = GetRootId(id);
            if (rootId != id)
                _pendingTouched.Add(id);

            _pendingTouched.Add(rootId);
            ScheduleFlush();
        }

        private void ScheduleFlush()
        {
            if (_scheduled)
            {
                if (_flushInProgress)
                    _rescheduleRequested = true;
                return;
            }

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, Flush, UPDATE_ORDER);
        }

        private void RegisterGuid(long id, GameRuntimeObject obj)
        {
            if (obj == null || !obj.GUID.isValid)
                return;

            if (_idByGuid.TryGetValue(obj.GUID, out var existingId) && existingId != id)
                throw new InvalidOperationException($"RuntimeStore '{Id}' already contains runtime object guid {obj.GUID} on id {existingId}.");

            _idByGuid[obj.GUID] = id;
        }

        private void UnregisterGuid(Hash128 guid, long id)
        {
            if (!guid.isValid)
                return;

            if (_idByGuid.TryGetValue(guid, out var existingId) && existingId == id)
                _idByGuid.Remove(guid);
        }

        private bool UnlinkEntity(long id)
        {
            if (!TryUnlinkEntity(id, out _))
                return false;

            return true;
        }

        private bool TryUnlinkEntity(long id, out Entity entity)
        {
            if (!_entityById.Remove(id, out entity))
                return false;

            MarkHierarchyRelationsDirty(id);
            _idByEntity.Remove(entity);
            _seenEntityLinks.Remove(id);

            if (_all.V.TryGetValue(id, out var obj))
                obj.ClearEntityLink();

            return true;
        }

        private void Flush()
        {
            using var profilerScope = FLUSH_AND_COLLECTION_MARKER.Auto();
            var profileStructuralBurst = _structureChanges.Count > 0;
            if (profileStructuralBurst)
                STRUCTURAL_BURST_MARKER.Begin();

            _flushInProgress = true;
            try
            {
                var replicationSuppressed = _suppressReplicationThisFlush;
                SwapTouchedBuffers();

                NativeList<RuntimeStructureDirty> emitStructure = default;
                NativeList<RuntimeStoreStructureChange> emitCommittedStructure = default;
                if (_structureChanges.Count > 0)
                {
                    if (_structureChangesNeedSort)
                    {
                        TotalFullSortCount++;
                        _structureChanges.Sort(new RuntimeStoreStructureChangeComparer());
                    }

                    emitStructure = new NativeList<RuntimeStructureDirty>(_structureChanges.Count, Allocator.Temp);
                    emitCommittedStructure = new NativeList<RuntimeStoreStructureChange>(_structureChanges.Count, Allocator.Temp);
                    foreach (var c in _structureChanges)
                    {
                        emitStructure.Add(c.ToRuntimeStructureDirty());
                        emitCommittedStructure.Add(c);
                    }

                    _structureChanges.Clear();
                    _structureChangesNeedSort = false;
                }

                NativeList<ObjectStructDirty> emitCompStruct = default;
                NativeList<ObjectComponentDirty> emitAuthoritativeComp = default;

                if (_processingTouched.Count > 0)
                {
                    emitCompStruct = new NativeList<ObjectStructDirty>(_processingTouched.Count * 2, Allocator.Temp);
                    emitAuthoritativeComp = new NativeList<ObjectComponentDirty>(_processingTouched.Count * 2, Allocator.Temp);

                    foreach (var id in _processingTouched)
                    {
                        if (!_all.V.TryGetValue(id, out var obj))
                            continue;

                        var structChanges = obj.StructureChanges;
                        var compChanges = obj.ComponentsChanges;

                        var hadAny = false;

                        if (structChanges != null && structChanges.Count > 0)
                        {
                            foreach (var kv in structChanges)
                            {
                                emitCompStruct.Add(new ObjectStructDirty(id, kv.Value));
                            }

                            hadAny = true;
                        }

                        if (compChanges != null && compChanges.Count > 0)
                        {
                            foreach (var kv in compChanges)
                            {
                                var compTypeId = kv.Key;

                                if (structChanges != null && structChanges.TryGetValue(compTypeId, out var sd) && sd.Kind == CompStructOpKind.Remove)
                                    continue;

                                var dirty = new ObjectComponentDirty(id, kv.Value);
                                emitAuthoritativeComp.Add(dirty);
                                _presentationComponentChanges.Remove(new PresentationComponentDirtyKey(id, compTypeId));
                            }

                            hadAny = true;
                        }

                        if (hadAny)
                            obj.ClearDirty();
                    }

                    if (emitCompStruct.Length > 1)
                    {
                        TotalFullSortCount++;
                        emitCompStruct.AsArray().Sort(new ObjectStructDirtyComparer());
                    }
                    if (emitAuthoritativeComp.Length > 1)
                    {
                        TotalFullSortCount++;
                        emitAuthoritativeComp.AsArray().Sort(new ObjectComponentDirtyComparer());
                    }
                }

                _processingTouched.Clear();

                NativeList<ObjectComponentDirty> emitPresentationComp = default;
                if (_presentationComponentChanges.Count > 0)
                {
                    emitPresentationComp = new NativeList<ObjectComponentDirty>(_presentationComponentChanges.Count, Allocator.Temp);
                    foreach (var dirty in _presentationComponentChanges)
                    {
                        if (!_all.V.TryGetValue(dirty.ObjectId, out var obj)
                            || !obj.TryGetById(dirty.ComponentTypeId, out _))
                        {
                            continue;
                        }

                        emitPresentationComp.Add(new ObjectComponentDirty(
                            dirty.ObjectId,
                            new ComponentDirty(dirty.ComponentTypeId)));
                    }

                    _presentationComponentChanges.Clear();

                    if (emitPresentationComp.Length > 1)
                    {
                        TotalFullSortCount++;
                        emitPresentationComp.AsArray().Sort(new ObjectComponentDirtyComparer());
                    }
                }

                NativeList<ObjectComponentDirty> emitMergedComp = default;
                var publicComponentChanges = default(NativeArray<ObjectComponentDirty>);
                if (emitAuthoritativeComp.IsCreated && emitAuthoritativeComp.Length > 0
                    && emitPresentationComp.IsCreated && emitPresentationComp.Length > 0)
                {
                    emitMergedComp = new NativeList<ObjectComponentDirty>(emitAuthoritativeComp.Length + emitPresentationComp.Length, Allocator.Temp);
                    MergeSortedComponentChanges(emitAuthoritativeComp.AsArray(), emitPresentationComp.AsArray(), ref emitMergedComp);
                    publicComponentChanges = emitMergedComp.AsArray();
                }
                else if (emitAuthoritativeComp.IsCreated && emitAuthoritativeComp.Length > 0)
                {
                    publicComponentChanges = emitAuthoritativeComp.AsArray();
                }
                else if (emitPresentationComp.IsCreated && emitPresentationComp.Length > 0)
                {
                    publicComponentChanges = emitPresentationComp.AsArray();
                }

                // BindDict listeners are legacy structure observers. Notify them
                // only for the captured structure publish; callback mutations are
                // recorded into the next publish buffers.
                if (emitStructure.IsCreated && emitStructure.Length > 0)
                {
                    _all.V = _all.V;
                    _parents.V = _parents.V;
                }

                var hasMutationBatch = (emitCommittedStructure.IsCreated && emitCommittedStructure.Length > 0)
                    || (emitCompStruct.IsCreated && emitCompStruct.Length > 0)
                    || (emitAuthoritativeComp.IsCreated && emitAuthoritativeComp.Length > 0);
                if (hasMutationBatch)
                {
                    if (_netApplyScopeActive)
                    {
                        if (!_netApplyRevisionCommitted)
                            StoreRevision = _netApplyTargetRevision;
                    }
                    else
                    {
                        if (StoreRevision == ulong.MaxValue)
                            throw new InvalidOperationException($"RuntimeStore '{Id}' generation {StoreGeneration} exhausted its revision range.");

                        StoreRevision++;
                    }
                }

                if (emitStructure.IsCreated && emitStructure.Length > 0)
                    _structureChangesDispatcher.Invoke(emitStructure.AsArray());

                if (emitCompStruct.IsCreated && emitCompStruct.Length > 0)
                    _componentStructureChangesDispatcher.Invoke(emitCompStruct.AsArray());

                if (publicComponentChanges.IsCreated && publicComponentChanges.Length > 0)
                    _componentChangesDispatcher.Invoke(publicComponentChanges);

                if (hasMutationBatch)
                {
                    _committedBatchDispatcher.Invoke(new RuntimeStoreCommittedBatch(
                        Id,
                        Realm,
                        StoreGeneration,
                        StoreRevision,
                        replicationSuppressed,
                        emitCommittedStructure.IsCreated ? emitCommittedStructure.AsArray() : default,
                        emitCompStruct.IsCreated ? emitCompStruct.AsArray() : default,
                        emitAuthoritativeComp.IsCreated ? emitAuthoritativeComp.AsArray() : default));
                }

                var dirtyKinds = RuntimeStoreDirtyKinds.None;
                if (emitStructure.IsCreated && emitStructure.Length > 0)
                    dirtyKinds |= RuntimeStoreDirtyKinds.Structure;
                if (emitCompStruct.IsCreated && emitCompStruct.Length > 0)
                    dirtyKinds |= RuntimeStoreDirtyKinds.ComponentStructure;
                if (publicComponentChanges.IsCreated && publicComponentChanges.Length > 0)
                    dirtyKinds |= RuntimeStoreDirtyKinds.ComponentData;
                if (emitPresentationComp.IsCreated && emitPresentationComp.Length > 0)
                    dirtyKinds |= RuntimeStoreDirtyKinds.Presentation;

                if (dirtyKinds != RuntimeStoreDirtyKinds.None)
                {
                    if (DirtyPublishVersion == ulong.MaxValue)
                        throw new InvalidOperationException($"RuntimeStore '{Id}' generation {StoreGeneration} exhausted its dirty publish version range.");

                    DirtyPublishVersion++;
                    _dirtyPublishCompletedDispatcher.Invoke(new RuntimeStoreDirtyPublish(StoreGeneration, DirtyPublishVersion, dirtyKinds));
                }

                if (emitStructure.IsCreated)
                    emitStructure.Dispose();
                if (emitCommittedStructure.IsCreated)
                    emitCommittedStructure.Dispose();
                if (emitCompStruct.IsCreated)
                    emitCompStruct.Dispose();
                if (emitAuthoritativeComp.IsCreated)
                    emitAuthoritativeComp.Dispose();
                if (emitPresentationComp.IsCreated)
                    emitPresentationComp.Dispose();
                if (emitMergedComp.IsCreated)
                    emitMergedComp.Dispose();
            }
            finally
            {
                try
                {
                    _flushInProgress = false;

                    _suppressReplicationThisFlush = false;
                    _netApplyTargetRevision = 0;
                    _netApplyRevisionCommitted = false;
                    _netApplyScopeActive = false;

                    var needReschedule = _rescheduleRequested;
                    _rescheduleRequested = false;

                    _scheduled = false;
                    CoroutineParent.RemoveLateUpdater(this);

                    if (needReschedule)
                        ScheduleFlush();
                }
                finally
                {
                    if (profileStructuralBurst)
                        STRUCTURAL_BURST_MARKER.End();
                }
            }
        }

        private void SwapTouchedBuffers()
        {
            var previousProcessing = _processingTouched;
            _processingTouched = _pendingTouched;
            _pendingTouched = previousProcessing;
            _pendingTouched.Clear();
        }

        private static void MergeSortedComponentChanges(
            NativeArray<ObjectComponentDirty> authoritative,
            NativeArray<ObjectComponentDirty> presentation,
            ref NativeList<ObjectComponentDirty> target)
        {
            var comparer = new ObjectComponentDirtyComparer();
            var authoritativeIndex = 0;
            var presentationIndex = 0;
            while (authoritativeIndex < authoritative.Length && presentationIndex < presentation.Length)
            {
                if (comparer.Compare(authoritative[authoritativeIndex], presentation[presentationIndex]) <= 0)
                {
                    target.Add(authoritative[authoritativeIndex]);
                    authoritativeIndex++;
                }
                else
                {
                    target.Add(presentation[presentationIndex]);
                    presentationIndex++;
                }
            }

            while (authoritativeIndex < authoritative.Length)
            {
                target.Add(authoritative[authoritativeIndex]);
                authoritativeIndex++;
            }

            while (presentationIndex < presentation.Length)
            {
                target.Add(presentation[presentationIndex]);
                presentationIndex++;
            }
        }

        private readonly struct PresentationComponentDirtyKey : IEquatable<PresentationComponentDirtyKey>
        {
            public readonly long ObjectId;
            public readonly uint ComponentTypeId;

            public PresentationComponentDirtyKey(long objectId, uint componentTypeId)
            {
                ObjectId = objectId;
                ComponentTypeId = componentTypeId;
            }

            public bool Equals(PresentationComponentDirtyKey other)
            {
                return ObjectId == other.ObjectId && ComponentTypeId == other.ComponentTypeId;
            }

            public override bool Equals(object obj)
            {
                return obj is PresentationComponentDirtyKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return (ObjectId.GetHashCode() * 397) ^ (int)ComponentTypeId;
                }
            }
        }
    }
}

