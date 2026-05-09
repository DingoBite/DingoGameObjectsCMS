using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects
{
    [CreateAssetMenu(menuName = MENU_PREFIX + "GameAsset")]
    public class GameAsset : GameAssetScriptableObject
    {
        [SerializeReference, SubclassSelector, JsonProperty("Components", ItemTypeNameHandling = TypeNameHandling.Auto)] private List<GameAssetComponent> _components;

        [Header("Keep value by default if there is no SourceAsset")]
        [SerializeField, Tooltip("If this GameAsset is representation of other GameAsset"), JsonProperty("SourceAssetGUID")] private Hash128 _sourceAssetGUID;

        [JsonIgnore] public List<GameAssetComponent> Components => _components;
        [JsonIgnore] public Hash128 SourceAssetGUID => _sourceAssetGUID;

        public void ResetToDefault(GameAssetKey key, Hash128 guid = default)
        {
            name = $"{key.Key}@{key.Version}";
            SetIdentity(key, guid);
            _sourceAssetGUID = default;
            _components = new List<GameAssetComponent>();
        }

        public void SetComponents(IEnumerable<GameAssetComponent> components)
        {
            _components = components != null ? new List<GameAssetComponent>(components) : new List<GameAssetComponent>();
        }

        public void SetSourceAssetGuid(Hash128 sourceAssetGuid)
        {
            _sourceAssetGUID = sourceAssetGuid;
        }

        public void ClearComponents()
        {
            _components ??= new List<GameAssetComponent>();
            _components.Clear();
        }

        public void AddOrReplaceComponent(GameAssetComponent component)
        {
            if (component == null)
                return;

            _components ??= new List<GameAssetComponent>();
            var type = component.GetType();
            var index = _components.FindIndex(c => c != null && c.GetType() == type);
            if (index >= 0)
                _components[index] = component;
            else
                _components.Add(component);
        }

        public bool TryGetComponent<T>(out T component) where T : GameAssetComponent
        {
            _components ??= new List<GameAssetComponent>();
            for (var i = 0; i < _components.Count; i++)
            {
                if (_components[i] is T typed)
                {
                    component = typed;
                    return true;
                }
            }

            component = null;
            return false;
        }

        public bool RemoveComponent<T>() where T : GameAssetComponent
        {
            _components ??= new List<GameAssetComponent>();
            var index = _components.FindIndex(c => c is T);
            if (index < 0)
                return false;

            _components.RemoveAt(index);
            return true;
        }

        public virtual void SetupRuntimeObject(GameRuntimeObject g)
        {
            g.Key = Key;
            g.AssetGUID = GUID;
            g.SourceAssetGUID = _sourceAssetGUID;
            if (_components == null)
                return;

            foreach (var component in _components)
            {
                component.SetupRuntimeComponent(g);
            }
        }

        public GameRuntimeCommand CreateRuntimeCommand()
        {
            var g = new GameRuntimeCommand();
            g.Key = Key;
            g.AssetGUID = GUID;
            if (_components == null)
                return null;

            foreach (var component in _components)
            {
                component.SetupRuntimeCommand(g);
            }

            return g;
        }
    }
}

