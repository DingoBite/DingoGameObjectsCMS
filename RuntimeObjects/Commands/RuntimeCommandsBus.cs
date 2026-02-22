using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Commands
{
    [Serializable, Preserve, HideInTypeMenu]
    public class GameRuntimeParameter
    {
        
    }

    [Serializable, Preserve]
    public class GameRuntimeCommand : RuntimeGUIDObject, ISerializationCallbackReceiver
    {
        public GameAssetKey Key;
        public Hash128 AssetGUID;
        
        [SerializeReference, JsonProperty("Parameters", ItemTypeNameHandling = TypeNameHandling.Auto)] private List<GameRuntimeParameter> _parameters = new();
        [NonSerialized, JsonIgnore] private Dictionary<Type, GameRuntimeParameter> _parametersByType;
        [NonSerialized, JsonIgnore] private Dictionary<uint, GameRuntimeParameter> _parametersById;
        
        public GameRuntimeParameter GetById(uint typeId)
        {
            EnsureCache();
            return _parametersById.GetValueOrDefault(typeId);
        }
        
        public T TakeOrForceCreate<T>(Func<T> factory) where T : GameRuntimeParameter
        {
            EnsureCache();
            if (!_parametersByType.TryGetValue(typeof(T), out var c) || c == null)
            {
                var value = factory();
                AddOrReplace(value);
                return value;
            }

            return (T) c;
        }
        
        public void AddOrReplace<T>(T component) where T : GameRuntimeParameter
        {
            EnsureCache();
            if (component == null)
                return;

            var keyType = typeof(T);
            var typeId = keyType.GetId();
            
            if (_parametersByType.TryGetValue(keyType, out var existing) && existing != null)
            {
                var idx = _parameters.FindIndex(c => ReferenceEquals(c, existing));
                if (idx >= 0)
                    _parameters[idx] = component;
                else
                    _parameters.Add(component);
            }
            else
            {
                _parameters.Add(component);
            }

            _parametersByType[keyType] = component;
            _parametersById[typeId] = component;
        }

        public void Remove<T>() where T : GameRuntimeParameter
        {
            if (_parametersByType.Remove(typeof(T), out var c))
                _parameters.Remove(c);

            var typeId = typeof(T).GetId();
            _parametersById.Remove(typeId);
        }
        
        private void EnsureCache()
        {
            if (_parametersById == null)
                RebuildCache();
        }

        private void RebuildCache()
        {
            _parametersByType = new Dictionary<Type, GameRuntimeParameter>(_parameters.Count);
            _parametersById = new Dictionary<uint, GameRuntimeParameter>(_parameters.Count);

            foreach (var c in _parameters)
            {
                if (c == null)
                    continue;

                var type = c.GetType();
                var id = type.GetId();
                _parametersByType[type] = c;
                _parametersById[id] = c;
            }
        }
        
        [OnDeserialized]
        private void OnDeserialized(StreamingContext _) => RebuildCache();
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() => RebuildCache();
    }

    public class RuntimeCommandsBus
    {
        
    }
}