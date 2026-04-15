using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using DingoGameObjectsCMS;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Newtonsoft.Json;
using Unity.Collections;
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

        [SerializeReference, JsonProperty("Components", ItemTypeNameHandling = TypeNameHandling.Auto)]
        private List<GameRuntimeComponent> _components = new();

        [NonSerialized, JsonIgnore] private Dictionary<Type, GameRuntimeComponent> _componentsByType = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, GameRuntimeComponent> _componentsById = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, ComponentDirty> _componentsChanges = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, ComponentStructDirty> _structureChanges = new();
        [NonSerialized, JsonIgnore] private bool _isDestroyed;
        [NonSerialized, JsonIgnore] private bool _isEcbEntity;
        [NonSerialized, JsonIgnore] private int _grcEditingToken;
        [NonSerialized, JsonIgnore] private bool _hasEntityProjection;
        [NonSerialized, JsonIgnore] private Entity _entity;

        [NonSerialized, JsonIgnore] private RuntimeStore _runtimeStore;
        [NonSerialized, JsonIgnore] private World _world;

        [JsonIgnore] public IReadOnlyDictionary<uint, ComponentDirty> ComponentsChanges => _componentsChanges;
        [JsonIgnore] public IReadOnlyDictionary<uint, ComponentStructDirty> StructureChanges => _structureChanges;
        [JsonIgnore] public IReadOnlyList<GameRuntimeComponent> Components => _components;
        [JsonIgnore] public bool HasEntityProjection => _hasEntityProjection;
        [JsonIgnore] public RuntimeInstance RuntimeInstance => new() { Id = InstanceId, StoreId = StoreId };

        public void LinkRuntimeContext(RuntimeStore runtimeStore, World world)
        {
            _runtimeStore = runtimeStore;
            _world = world;
        }

        public void LinkToEntity(Entity entity)
        {
            _entity = entity;
            _isEcbEntity = false;
            _grcEditingToken = 0;
            _hasEntityProjection = true;
        }

        public void ClearEntityLink()
        {
            _entity = Entity.Null;
            _isEcbEntity = false;
            _grcEditingToken = 0;
        }


        public void ClearRuntimeContext()
        {
            _runtimeStore = null;
            _world = null;
            _hasEntityProjection = false;
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
            return (T)c;
        }

        public Entity CreateEntity()
        {
            var ecb = _world.TakeGRCEditingECB(out var ecbToken);

            _hasEntityProjection = true;
            _isEcbEntity = true;
            _grcEditingToken = ecbToken;
            _entity = ecb.CreateEntity();

            ecb.AddComponent(_entity, new AssetLink { AssetGUID = AssetGUID });
            var source = SourceAssetGUID.isValid ? SourceAssetGUID : GUID;
            ecb.AddComponent(_entity, new SourceAssetLink { AssetGUID = source });
            if (SourceAssetGUID.isValid)
                ecb.AddComponent(_entity, new AssetPresentationTag());
            ecb.AddComponent(_entity, new RuntimeRealm { Realm = Realm });
            ecb.AddComponent(_entity, RuntimeInstance);
            SetupEntityProjection(ecb);
            return _entity;
        }

        public void SetupEntityProjection(EntityCommandBuffer ecb)
        {
            if (Components == null)
                return;

            foreach (var c in Components)
            {
                c?.SetupForEntity(_runtimeStore, ecb, this, _entity);
            }
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

            ecb = _world.TakeGRCEditingECB(out var ecbToken);
            if (_isEcbEntity && ecbToken != _grcEditingToken)
                throw new InvalidOperationException($"GameRuntimeObject {InstanceId} in store '{StoreId}' is still bound to a deferred ECB entity from another editing scope.");

            return true;
        }


        public void AddOrReplaceById(uint typeId, GameRuntimeComponent component)
        {
            if (component == null)
                return;

            var keyType = component.GetType();
            EnsureCache();

            var shouldSyncEntity = TryTakeEditingEcb(out var ecb);
            var hadExisting = _componentsByType.TryGetValue(keyType, out var existing) && existing != null;
            var existingWasInList = false;

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
                    if (shouldSyncEntity)
                        existing.RemoveFromEntity(_runtimeStore, ecb, this, _entity);

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
            _isDestroyed = false;

            if (shouldSyncEntity)
                component.AddForEntity(_runtimeStore, ecb, this, _entity);
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
            var keyType = c.GetType();

            if (shouldSyncEntity)
                c.RemoveFromEntity(_runtimeStore, ecb, this, _entity);

            _componentsByType.Remove(keyType);
            _componentsById.Remove(typeId);
            _components.Remove(c);

            if (c is IDisposable disposable)
                disposable.Dispose();

            MarkComponentStructDirty(typeId, keyType, CompStructOpKind.Remove);
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

            _isDestroyed = true;

            if (_components == null)
                return;

            var shouldSyncEntity = TryTakeEditingEcb(out var ecb);
            if (shouldSyncEntity)
            {
                foreach (var component in _components)
                {
                    component?.RemoveFromEntity(_runtimeStore, ecb, this, _entity);
                }
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
            if (_componentsById == null || _componentsByType == null)
                RebuildCache();
        }

        private void RebuildCache()
        {
            _componentsByType ??= new Dictionary<Type, GameRuntimeComponent>();
            _componentsById ??= new Dictionary<uint, GameRuntimeComponent>();
            _componentsChanges ??= new Dictionary<uint, ComponentDirty>();
            _structureChanges ??= new Dictionary<uint, ComponentStructDirty>();

            _componentsByType.Clear();
            _componentsById.Clear();
            foreach (var c in _components)
            {
                if (c == null)
                    continue;

                var type = c.GetType();
                var id = type.GetId();
                _componentsByType[type] = c;
                _componentsById[id] = c;
            }

            _isDestroyed = false;
        }

        [OnDeserialized]
        private void OnDeserialized(StreamingContext _) => RebuildCache();

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() => RebuildCache();
    }
}
