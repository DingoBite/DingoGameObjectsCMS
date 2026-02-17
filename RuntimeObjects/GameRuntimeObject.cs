using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting;
using Hash128 = UnityEngine.Hash128;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    [Serializable, Preserve]
    public struct RuntimeInstance : IComponentData
    {
        public long Id;
        public Hash128 StoreId;
    }

    [Serializable, Preserve]
    public struct GameAssetKey
    {
        public const string MODS = "mods";
        public const string UNDEFINED = "_undefined";
        public const string NONE = "_none";
        public const string ZERO_V = "0.0.0";

        public string Mod;
        public string Type;
        public string Key;
        public string Version;
        
        public GameAssetKey(string mod = null, string type = null, string key = null, string version = null)
        {
            Mod = string.IsNullOrWhiteSpace(mod) ? UNDEFINED : mod;
            Type = string.IsNullOrWhiteSpace(type) ? NONE : type;
            Key = string.IsNullOrWhiteSpace(key) ? NONE : key;
            Version = string.IsNullOrWhiteSpace(version) ? ZERO_V : version;
        }
    }

    [Serializable, Preserve]
    public class GameRuntimeObject : RuntimeGUIDObject, ISerializationCallbackReceiver
    {
        public GameAssetKey Key;
        public Hash128 AssetGUID;
        public Hash128 SourceAssetGUID;

        [SerializeReference, JsonProperty("Components", ItemTypeNameHandling = TypeNameHandling.Auto)] private List<GameRuntimeComponent> _components = new();
        [NonSerialized, JsonIgnore] private Dictionary<Type, GameRuntimeComponent> _componentsCache;

        [JsonIgnore] public IReadOnlyList<GameRuntimeComponent> Components => _components;
        [JsonIgnore] public RuntimeInstance RuntimeInstance => new() { Id = InstanceId, StoreId = StoreId };

        public Entity CreateEntity(RuntimeStore runtimeStore, EntityCommandBuffer ecb)
        {
            var entity = ecb.CreateEntity();

            ecb.AddComponent(entity, new AssetLink { AssetGUID = AssetGUID });
            var source = SourceAssetGUID.isValid ? SourceAssetGUID : GUID;
            ecb.AddComponent(entity, new SourceAssetLink { AssetGUID = source });
            if (SourceAssetGUID.isValid)
                ecb.AddComponent(entity, new AssetPresentationTag());
            
            if (Components == null)
                return entity;

            foreach (var c in Components)
            {
                c?.SetupForEntity(runtimeStore, ecb, this, entity);
            }

            return entity;
        }

        public void Destroy()
        {
            foreach (var component in Components)
            {
                if (component is IDisposable disposable)
                    disposable.Dispose();
            }
        }
        
        public void AddOrReplace<T>(T component) where T : GameRuntimeComponent
        {
            if (component == null)
                return;

            EnsureCache();

            var keyType = typeof(T);

            if (_componentsCache.TryGetValue(keyType, out var existing) && existing != null)
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

            _componentsCache[keyType] = component;
        }

        public T Get<T>() where T : GameRuntimeComponent
        {
            EnsureCache();
            return _componentsCache.TryGetValue(typeof(T), out var c) ? (T)c : null;
        }
        
        public T GetOrForceCreate<T>(T create) where T : GameRuntimeComponent
        {
            EnsureCache();
            if (_componentsCache.TryGetValue(typeof(T), out var c))
                return (T)c;
            AddOrReplace(create);
            return create;
        }

        public bool TryGet<T>(out T value) where T : GameRuntimeComponent
        {
            value = Get<T>();
            return value != null;
        }

        public bool Remove<T>(out T value) where T : GameRuntimeComponent
        {
            EnsureCache();

            var keyType = typeof(T);

            if (_componentsCache.TryGetValue(keyType, out var existing) && existing != null)
            {
                value = (T)existing;
                _componentsCache.Remove(keyType);

                var idx = _components.FindIndex(c => ReferenceEquals(c, existing));
                if (idx >= 0)
                {
                    _components.RemoveAt(idx);
                }
                else
                {
                    for (var i = 0; i < _components.Count; i++)
                    {
                        if (_components[i] != null && _components[i].GetType() == keyType)
                        {
                            _components.RemoveAt(i);
                            break;
                        }
                    }
                }

                return true;
            }

            value = null;
            return false;
        }

        public void ClearComponents()
        {
            _components.Clear();
            _componentsCache?.Clear();
        }

        private void EnsureCache()
        {
            if (_componentsCache == null)
                RebuildCache();
        }

        private void RebuildCache()
        {
            _componentsCache = new Dictionary<Type, GameRuntimeComponent>(_components.Count);

            for (var i = 0; i < _components.Count; i++)
            {
                var c = _components[i];
                if (c == null)
                    continue;
                
                _componentsCache[c.GetType()] = c;
            }
        }
        
        [OnDeserialized]
        private void OnDeserialized(StreamingContext _) => RebuildCache();
        void ISerializationCallbackReceiver.OnBeforeSerialize() { }
        void ISerializationCallbackReceiver.OnAfterDeserialize() => RebuildCache();
    }
}