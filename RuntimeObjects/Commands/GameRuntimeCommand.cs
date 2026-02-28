using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Commands
{
    [Serializable, Preserve]
    public class GameRuntimeCommand : GameGUIDObject, ISerializationCallbackReceiver
    {
        public GameAssetKey Key;
        public Hash128 AssetGUID;
        public FixedString32Bytes ApplyToStoreId;
        
        [SerializeReference, JsonProperty("Components", ItemTypeNameHandling = TypeNameHandling.Auto)] private List<GameRuntimeComponent> _components = new();
        [NonSerialized, JsonIgnore] private Dictionary<Type, GameRuntimeComponent> _componentsByType;
        [NonSerialized, JsonIgnore] private Dictionary<uint, GameRuntimeComponent> _componentsById;
        
        [JsonIgnore] public IReadOnlyList<GameRuntimeComponent> Components => _components;

        public GameRuntimeComponent GetById(uint typeId)
        {
            EnsureCache();
            return _componentsById.GetValueOrDefault(typeId);
        }

        public T Get<T>() where T : GameRuntimeComponent
        {
            EnsureCache();
            return _componentsByType.GetValueOrDefault(typeof(T)) as T;
        }

        public void AddOrReplace<T>(T component) where T : GameRuntimeComponent
        {
            EnsureCache();
            if (component == null)
                return;

            var keyType = typeof(T);
            var typeId = keyType.GetId();
            
            if (_componentsByType.TryGetValue(keyType, out var existing) && existing != null)
            {
                var idx = _components.FindIndex(c => ReferenceEquals(c, existing));
                if (idx >= 0)
                    _components[idx] = component;
                else
                    _components.Add(component);
            }
            else
            {
                _components.Add(component);
            }

            _componentsByType[keyType] = component;
            _componentsById[typeId] = component;
        }

        public void Remove<T>() where T : GameRuntimeComponent
        {
            if (_componentsByType.Remove(typeof(T), out var c))
                _components.Remove(c);

            var typeId = typeof(T).GetId();
            _componentsById.Remove(typeId);
        }
        
        private void EnsureCache()
        {
            if (_componentsById == null)
                RebuildCache();
        }

        private void RebuildCache()
        {
            _componentsByType = new Dictionary<Type, GameRuntimeComponent>(_components.Count);
            _componentsById = new Dictionary<uint, GameRuntimeComponent>(_components.Count);

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