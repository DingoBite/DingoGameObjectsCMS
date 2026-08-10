using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting;
using Hash128 = UnityEngine.Hash128;

namespace DingoGameObjectsCMS.RuntimeObjects.Objects
{
    [Serializable, Preserve]
    public class GameRuntimeObject : RuntimeGUIDObject, ISerializationCallbackReceiver
    {
        public GameAssetKey Key;
        public Hash128 AssetGUID;
        public Hash128 SourceAssetGUID;

        [SerializeField, JsonProperty("Origin")]
        private RuntimeObjectOrigin _origin;

        [SerializeReference, JsonProperty("Components", ItemConverterType = typeof(RuntimeComponentJsonConverter))]
        private List<GameRuntimeComponent> _components = new();

        [NonSerialized, JsonIgnore] private Dictionary<Type, GameRuntimeComponent> _componentsByType = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, GameRuntimeComponent> _componentsById = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, ComponentDirty> _componentsChanges = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, ComponentStructDirty> _structureChanges = new();
        [NonSerialized, JsonIgnore] private bool _isDestroyed;
        [NonSerialized, JsonIgnore] private bool _hasEntityProjection;
        [NonSerialized, JsonIgnore] private bool _isEntityFactoryProjection;
        [NonSerialized, JsonIgnore] private bool _cacheHasRuntimeIds;
        [NonSerialized, JsonIgnore] private ulong _entityFactoryMutationRevision;
        [NonSerialized, JsonIgnore] private Entity _entity;

        [NonSerialized, JsonIgnore] private RuntimeStore _runtimeStore;
        [NonSerialized, JsonIgnore] private World _world;

        [JsonIgnore] public IReadOnlyDictionary<uint, ComponentDirty> ComponentsChanges => _componentsChanges;
        [JsonIgnore] public IReadOnlyDictionary<uint, ComponentStructDirty> StructureChanges => _structureChanges;
        [JsonIgnore] public IReadOnlyList<GameRuntimeComponent> Components => _components;
        [JsonIgnore] public bool HasEntityProjection => _hasEntityProjection;
        [JsonIgnore] public bool IsEntityFactory => _isEntityFactoryProjection || HasEntityFactoryComponent();
        [JsonIgnore] public ulong EntityFactoryMutationRevision => _entityFactoryMutationRevision;
        [JsonIgnore] public RuntimeInstance RuntimeInstance => new() { Id = InstanceId, StoreId = StoreId, Epoch = _runtimeStore?.Epoch ?? 0u };
        [JsonIgnore] public RuntimeObjectOrigin Origin => _origin;

        public void SetOrigin(RuntimeObjectOrigin origin)
        {
            if (!origin.InstanceGuid.isValid)
                throw new ArgumentException("Runtime object origin requires a valid instance GUID.", nameof(origin));
            if (!origin.Asset.AssetGuid.isValid)
                throw new ArgumentException("Runtime object origin requires a valid asset GUID.", nameof(origin));

            _origin = origin;
        }

        public void LinkRuntimeContext(RuntimeStore runtimeStore, World world)
        {
            _runtimeStore = runtimeStore;
            _world = world;
        }

        public void LinkToEntity(Entity entity)
        {
            _entity = entity;
            _hasEntityProjection = true;
            _isEntityFactoryProjection |= HasEntityFactoryComponent();
        }

        public void ClearEntityLink()
        {
            _entity = Entity.Null;
        }

        public void ClearRuntimeContext()
        {
            _runtimeStore = null;
            _world = null;
            _hasEntityProjection = false;
            _isEntityFactoryProjection = false;
            _entityFactoryMutationRevision = 0;
            ClearEntityLink();
        }

        public GameRuntimeComponent GetById(uint typeId)
        {
            EnsureCache();
            return _componentsById.GetValueOrDefault(typeId);
        }

        public bool TryGetById(uint typeId, out GameRuntimeComponent component)
        {
            EnsureCache();
            return _componentsById.TryGetValue(typeId, out component);
        }

        public bool Has<T>() where T : GameRuntimeComponent
        {
            EnsureCache();
            return _componentsByType.ContainsKey(typeof(T));
        }

        public T TakeRO<T>() where T : GameRuntimeComponent
        {
            EnsureCache();
            return _componentsByType.TryGetValue(typeof(T), out var c) ? (T)c : null;
        }

        public T TakeRW<T>() where T : GameRuntimeComponent
        {
            EnsureCache();
            if (!_componentsByType.TryGetValue(typeof(T), out var c) || c == null)
                return null;

            MarkComponentDirty<T>();
            RegisterEntityFactoryWriteIntent();
            return (T)c;
        }

        public Entity CreateEntity()
        {
            if (_hasEntityProjection)
            {
                if (_entity == Entity.Null)
                    throw new InvalidOperationException($"GameRuntimeObject {InstanceId} in store '{StoreId}' lost its entity link and cannot create a second projection.");

                return _entity;
            }

            if (_world == null || !_world.IsCreated)
                throw new InvalidOperationException($"GameRuntimeObject {InstanceId} in store '{StoreId}' requires a valid ECS World.");

            var entityManager = _world.EntityManager;
            var isEntityFactory = HasEntityFactoryComponent();
            _entity = entityManager.CreateEntity();
            entityManager.AddComponentData(_entity, new AssetLink { AssetGUID = AssetGUID });
            var source = SourceAssetGUID.isValid ? SourceAssetGUID : GUID;
            entityManager.AddComponentData(_entity, new SourceAssetLink { AssetGUID = source });
            if (SourceAssetGUID.isValid)
                entityManager.AddComponent<AssetPresentationTag>(_entity);
            entityManager.AddComponentData(_entity, new RuntimeRealm { Realm = Realm });
            entityManager.AddComponentData(_entity, RuntimeInstance);
            entityManager.AddComponentData(_entity, new RuntimeEntityDestroyState());
            entityManager.AddComponent<RuntimeProjectionPending>(_entity);
            entityManager.AddBuffer<RuntimeChildEntity>(_entity);
            if (isEntityFactory)
            {
                entityManager.AddComponent<RuntimeEntityFactoryTag>(_entity);
                entityManager.AddComponentData(
                    _entity,
                    new RuntimeEntityFactoryProductIdentity
                    {
                        ProductId = 0
                    });
                var linkedEntities = entityManager.AddBuffer<LinkedEntityGroup>(_entity);
                linkedEntities.Add(_entity);
            }

            _hasEntityProjection = true;
            _isEntityFactoryProjection = isEntityFactory;
            _entityFactoryMutationRevision = 0;
            _runtimeStore.LinkEntity(InstanceId, _entity);

            var ecb = _world.TakeGRCEditingECB();
            SetupEntityProjection(ecb);
            ecb.RemoveComponent<RuntimeProjectionPending>(_entity);
            return _entity;
        }

        public void SetupEntityProjection(EntityCommandBuffer ecb)
        {
            SetupEntityProjection(_runtimeStore, ecb, _entity);
        }

        /// <summary>
        /// Projects this GRO's complete component composition onto an ECS
        /// entity owned by another runtime object's topology or pool.
        /// The owning factory creates the entity and controls its lifetime;
        /// this GRO remains the sole authority for the product signature.
        /// </summary>
        public void SetupEntityProjection(
            RuntimeStore store,
            EntityCommandBuffer ecb,
            Entity entity)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (entity == Entity.Null)
                throw new ArgumentException(
                    "Entity projection requires a valid target entity.",
                    nameof(entity));
            if (Components == null)
                return;

            foreach (var component in Components)
            {
                component?.SetupForEntity(store, ecb, this, entity);
            }
        }

        public void SetupOwnedEntityProjection(
            RuntimeStore store,
            EntityCommandBuffer ecb,
            Entity entity)
        {
            if (HasEntityFactoryComponent())
            {
                throw new InvalidOperationException(
                    $"Product GA '{Key}' cannot contain a nested runtime "
                    + "Entity factory.");
            }

            SetupEntityProjection(store, ecb, entity);
        }

        private bool TryTakeEditingEcb(out EntityCommandBuffer ecb)
        {
            if (!_hasEntityProjection)
            {
                ecb = default;
                return false;
            }

            if (_entity == Entity.Null)
                throw new InvalidOperationException($"GameRuntimeObject {InstanceId} in store '{StoreId}' lost its entity link.");

            if (_world == null || !_world.IsCreated)
            {
                ecb = default;
                return false;
            }

            ecb = _world.TakeGRCEditingECB();
            return true;
        }


        public void AddOrReplaceById(uint typeId, GameRuntimeComponent component)
        {
            if (component == null)
                return;

            var keyType = component.GetType();
            EnsureCache();
            if (_componentsById.TryGetValue(typeId, out var existingById)
                && existingById != null
                && existingById.GetType() != keyType)
            {
                throw new InvalidOperationException(
                    $"GameRuntimeObject {InstanceId} in store '{StoreId}' already uses runtime component type id {typeId} for '{existingById.GetType().FullName}'.");
            }
            foreach (var pair in _componentsById)
            {
                if (pair.Key != typeId
                    && pair.Value != null
                    && pair.Value.GetType() == keyType)
                {
                    throw new InvalidOperationException(
                        $"GameRuntimeObject {InstanceId} in store '{StoreId}' already uses runtime component type '{keyType.FullName}' as type id {pair.Key}.");
                }
            }

            var shouldSyncEntity = TryTakeEditingEcb(out var ecb);
            var projectedEntity = shouldSyncEntity ? _entity : Entity.Null;
            var hadExisting = _componentsByType.TryGetValue(keyType, out var existing) && existing != null;
            var existingWasInList = false;

            if (hadExisting && ReferenceEquals(existing, component))
            {
                MarkComponentDirty(typeId);
                RegisterProjectedEntityFactoryComponent(component);
                RegisterEntityFactoryWriteIntent();
                _isDestroyed = false;
                if (shouldSyncEntity)
                {
                    component.RemoveFromEntity(_runtimeStore, ecb, this, projectedEntity);
                    component.AddForEntity(_runtimeStore, ecb, this, projectedEntity);
                }

                return;
            }

            if (hadExisting)
            {
                var idx = _components.FindIndex(c => ReferenceEquals(c, existing));
                if (idx >= 0)
                {
                    _components[idx] = component;
                    existingWasInList = true;
                }
                else
                {
                    _components.Add(component);
                }

                if (!ReferenceEquals(existing, component))
                {
                    existing.DestroyForRuntime(_runtimeStore, shouldSyncEntity ? ecb : default, this, projectedEntity);

                    if (existing is IDisposable disposable)
                        disposable.Dispose();
                }

                if (!existingWasInList)
                    MarkComponentStructDirty(typeId, keyType, CompStructOpKind.Add);

                MarkComponentDirty(typeId);
            }
            else
            {
                _components.Add(component);
                MarkComponentStructDirty(typeId, keyType, CompStructOpKind.Add);
                MarkComponentDirty(typeId);
            }

            _componentsByType[keyType] = component;
            _componentsById[typeId] = component;
            RegisterProjectedEntityFactoryComponent(component);
            RegisterEntityFactoryWriteIntent();
            _isDestroyed = false;

            if (shouldSyncEntity)
                component.AddForEntity(_runtimeStore, ecb, this, projectedEntity);
        }

        public void AddOrReplace<T>(T component) where T : GameRuntimeComponent
        {
            if (typeof(T) == typeof(GameRuntimeComponent))
                return;
            if (component == null)
                return;

            var typeId = typeof(T).GetId();
            AddOrReplaceById(typeId, component);
        }

        public bool RemoveByTypeId(uint typeId)
        {
            EnsureCache();

            if (!_componentsById.TryGetValue(typeId, out var c) || c == null)
                return false;

            var shouldSyncEntity = TryTakeEditingEcb(out var ecb);
            var projectedEntity = shouldSyncEntity ? _entity : Entity.Null;
            var keyType = c.GetType();

            c.DestroyForRuntime(_runtimeStore, shouldSyncEntity ? ecb : default, this, projectedEntity);

            _componentsByType.Remove(keyType);
            _componentsById.Remove(typeId);
            _components.Remove(c);

            if (c is IDisposable disposable)
                disposable.Dispose();

            MarkComponentStructDirty(typeId, keyType, CompStructOpKind.Remove);
            RegisterEntityFactoryWriteIntent();
            return true;
        }

        public void Remove<T>() where T : GameRuntimeComponent
        {
            var typeId = typeof(T).GetId();
            RemoveByTypeId(typeId);
        }

        public void Destroy()
        {
            if (_isDestroyed)
                return;

            var shouldSyncEntity = TryTakeEditingEcb(out var ecb);
            Destroy(ecb, shouldSyncEntity);
        }

        private void Destroy(EntityCommandBuffer ecb, bool shouldSyncEntity)
        {
            _isDestroyed = true;

            if (_components == null)
                return;

            var projectedEntity = shouldSyncEntity ? _entity : Entity.Null;
            foreach (var component in _components)
            {
                component?.DestroyForRuntime(_runtimeStore, shouldSyncEntity ? ecb : default, this, projectedEntity);
            }

            foreach (var component in _components)
            {
                if (component is IDisposable disposable)
                    disposable.Dispose();
            }
        }

        public void SetDirty<T>() where T : GameRuntimeComponent
        {
            var typeId = typeof(T).GetId();
            SetDirtyById(typeId);
        }

        public void SetDirty(GameRuntimeComponent component)
        {
            if (component == null)
                throw new InvalidOperationException($"GameRuntimeObject {InstanceId} in store '{StoreId}' cannot mark a null component dirty.");

            SetDirtyById(component.GetType().GetId());
        }

        public void SetDirtyById(uint compTypeId)
        {
            EnsureCache();
            if (!_componentsById.ContainsKey(compTypeId))
                throw new InvalidOperationException($"GameRuntimeObject {InstanceId} in store '{StoreId}' does not contain component type id {compTypeId}.");

            MarkComponentDirty(compTypeId);
            RegisterEntityFactoryWriteIntent();
        }

        public void RegisterEntityFactoryWriteIntent()
        {
            if (!_isEntityFactoryProjection || _entityFactoryMutationRevision == ulong.MaxValue)
                return;

            _entityFactoryMutationRevision++;
        }

        public void ClearDirty()
        {
            _componentsChanges.Clear();
            _structureChanges.Clear();
        }

        private void MarkComponentDirty<T>()
        {
            if (!DirtyTraits<T>.Data)
                return;
            var compTypeId = typeof(T).GetId();
            MarkComponentDirty(compTypeId);
        }

        private void MarkComponentDirty(uint compTypeId)
        {
            _componentsChanges[compTypeId] = new ComponentDirty(compTypeId);
        }

        private void MarkComponentStructDirty<T>(CompStructOpKind kind)
        {
            if (DirtyTraits<T>.NoStruct)
                return;

            var compTypeId = typeof(T).GetId();
            if (_structureChanges.TryGetValue(compTypeId, out var prev))
            {
                if (prev.Kind == CompStructOpKind.Add && kind == CompStructOpKind.Remove)
                {
                    _structureChanges.Remove(compTypeId);
                    _componentsChanges.Remove(compTypeId);
                    return;
                }
            }

            _structureChanges[compTypeId] = new ComponentStructDirty(compTypeId, kind);
        }

        private void MarkComponentStructDirty(uint compTypeId, Type compType, CompStructOpKind kind)
        {
            if (typeof(IStoreStructDirtyIgnore).IsAssignableFrom(compType))
                return;

            if (_structureChanges.TryGetValue(compTypeId, out var prev))
            {
                if (prev.Kind == CompStructOpKind.Add && kind == CompStructOpKind.Remove)
                {
                    _structureChanges.Remove(compTypeId);
                    _componentsChanges.Remove(compTypeId);
                    return;
                }
            }

            _structureChanges[compTypeId] = new ComponentStructDirty(compTypeId, kind);
        }

        private void EnsureCache()
        {
            if (_componentsById == null
                || _componentsByType == null
                || (RuntimeComponentTypeRegistry.IsInitialized && !_cacheHasRuntimeIds))
                RebuildCache();
        }

        private void RebuildCache()
        {
            _components ??= new List<GameRuntimeComponent>();
            _componentsByType ??= new Dictionary<Type, GameRuntimeComponent>();
            _componentsById ??= new Dictionary<uint, GameRuntimeComponent>();
            _componentsChanges ??= new Dictionary<uint, ComponentDirty>();
            _structureChanges ??= new Dictionary<uint, ComponentStructDirty>();

            _componentsByType.Clear();
            _componentsById.Clear();
            _cacheHasRuntimeIds = RuntimeComponentTypeRegistry.IsInitialized;
            RuntimeComponentCollectionValidator.Validate(
                _components,
                $"GameRuntimeObject {InstanceId} in store '{StoreId}'",
                requireRegisteredTypeIds: _cacheHasRuntimeIds);
            foreach (var c in _components)
            {
                var type = c.GetType();
                _componentsByType[type] = c;

                if (!_cacheHasRuntimeIds)
                    continue;

                var id = type.GetId();
                _componentsById[id] = c;
            }

            _isDestroyed = false;
        }

        private bool HasEntityFactoryComponent()
        {
            if (_components == null)
                return false;

            foreach (var component in _components)
            {
                if (component is GameRuntimeEntityFactoryComponent)
                    return true;
            }

            return false;
        }

        private void RegisterProjectedEntityFactoryComponent(GameRuntimeComponent component)
        {
            if (_hasEntityProjection
                && !_isEntityFactoryProjection
                && component is GameRuntimeEntityFactoryComponent)
            {
                _isEntityFactoryProjection = true;
            }
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext _) => RebuildCache();

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() => RebuildCache();
    }
}




