using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
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

        [SerializeReference, JsonProperty("Components", ItemTypeNameHandling = TypeNameHandling.Auto)] private List<GameRuntimeComponent> _components = new();
        [NonSerialized, JsonIgnore] private Dictionary<Type, GameRuntimeComponent> _componentsByType = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, GameRuntimeComponent> _componentsById = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, ComponentDirty> _componentsChanges = new();
        [NonSerialized, JsonIgnore] private Dictionary<uint, ComponentStructDirty> _structureChanges = new();

        [JsonIgnore] public IReadOnlyDictionary<uint, ComponentDirty> ComponentsChanges => _componentsChanges;
        [JsonIgnore] public IReadOnlyDictionary<uint, ComponentStructDirty> StructureChanges => _structureChanges;
        [JsonIgnore] public IReadOnlyList<GameRuntimeComponent> Components => _components;
        [JsonIgnore] public RuntimeInstance RuntimeInstance => new() { Id = InstanceId, StoreId = StoreId };
        
        public GameRuntimeComponent GetById(uint typeId)
        {
            EnsureCache();
            return _componentsById.GetValueOrDefault(typeId);
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
        
        public Entity CreateEntity(RuntimeStore runtimeStore, EntityCommandBuffer ecb)
        {
            var entity = ecb.CreateEntity();

            ecb.AddComponent(entity, new AssetLink { AssetGUID = AssetGUID });
            var source = SourceAssetGUID.isValid ? SourceAssetGUID : GUID;
            ecb.AddComponent(entity, new SourceAssetLink { AssetGUID = source });
            if (SourceAssetGUID.isValid)
                ecb.AddComponent(entity, new AssetPresentationTag());
            ecb.AddComponent(entity, new RuntimeRealm { Realm = Realm });
            ecb.AddComponent(entity, RuntimeInstance);

            if (Components == null) return entity;
            foreach (var c in Components)
            {
                c?.SetupForEntity(runtimeStore, ecb, this, entity);
            }

            return entity;
        }

        public void AddOrReplaceById(uint typeId, GameRuntimeComponent component)
        {
            var keyType = typeId.GetRegisteredType();
            EnsureCache();
            if (_componentsByType.TryGetValue(keyType, out var existing) && existing != null)
            {
                var idx = _components.FindIndex(c => ReferenceEquals(c, existing));
                if (idx >= 0)
                {
                    _components[idx] = component;
                    MarkComponentDirty(typeId);
                }
                else
                {
                    _components.Add(component);
                    MarkComponentStructDirty(typeId, keyType, CompStructOpKind.Add);
                    MarkComponentDirty(typeId);
                }
            }
            else
            {
                _components.Add(component);
                MarkComponentStructDirty(typeId, keyType, CompStructOpKind.Add);
                MarkComponentDirty(typeId);
            }

            _componentsByType[keyType] = component;
            _componentsById[typeId] = component;
        }
        
        public void AddOrReplace<T>(T component) where T : GameRuntimeComponent
        {
            if (typeof(T) == typeof(GameRuntimeComponent))
                return;
            if (component == null)
                return;

            var keyType = typeof(T);
            var typeId = keyType.GetId();
            AddOrReplaceById(typeId, component);
        }

        public bool RemoveByTypeId(uint typeId)
        {
            EnsureCache();

            if (!_componentsById.TryGetValue(typeId, out var c) || c == null)
                return false;

            var keyType = c.GetType();
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
            if (_componentsByType.Remove(typeof(T), out var c))
            {
                _components.Remove(c);
                if (c is IDisposable disposable)
                    disposable.Dispose();
            }

            var typeId = typeof(T).GetId();
            _componentsById.Remove(typeId);
            
            MarkComponentStructDirty<T>(CompStructOpKind.Remove);
        }

        public void Destroy()
        {
            foreach (var component in Components)
            {
                if (component is IDisposable disposable)
                    disposable.Dispose();
            }
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
            if (_componentsById == null)
                RebuildCache();
        }

        private void RebuildCache()
        {
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
        }
        
        [OnDeserialized]
        private void OnDeserialized(StreamingContext _) => RebuildCache();
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() => RebuildCache();
    }
}
