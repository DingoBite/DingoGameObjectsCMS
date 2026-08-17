using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects
{
    public class GameAsset : GameAssetScriptableObject
    {
        [SerializeReference, SubclassSelector, JsonProperty("Components", ItemTypeNameHandling = TypeNameHandling.Auto)] private List<GameAssetComponent> _components;

        /// <summary>
        /// Source/presentation linkage: the GameAsset this one represents. It is
        /// not lineage and not the baseline identity; use <see cref="Prefab"/>
        /// for inheritance.
        /// </summary>
        public GameAssetKey SourceAssetKey;

        public GameAssetPrefab Prefab;

        [JsonIgnore] public List<GameAssetComponent> Components => _components;

        public void ResetToDefault(GameAssetKey key, Hash128 guid = default)
        {
            name = $"{key.Key}@{key.Version}";
            SetIdentity(key, guid);
            SourceAssetKey = default;
            Prefab = null;
            _components = new List<GameAssetComponent>();
        }

        public void SetComponents(IEnumerable<GameAssetComponent> components)
        {
            _components = components != null ? new List<GameAssetComponent>(components) : new List<GameAssetComponent>();
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
            g.SourceAssetKey = SourceAssetKey;
            if (_components == null)
                return;

            foreach (var component in SetupOrdered())
            {
                component.SetupRuntimeComponent(g);
            }
        }

        /// <summary>
        /// Components in materialization order. OrderBy is a stable sort, so
        /// components sharing a <see cref="GameAssetSetupOrderAttribute"/> order
        /// keep their authored order.
        /// </summary>
        public IEnumerable<GameAssetComponent> SetupOrdered()
        {
            return _components.OrderBy(GameAssetSetupOrderUtils.GetOrder);
        }

        public GameRuntimeCommand CreateRuntimeCommand()
        {
            var g = new GameRuntimeCommand();
            g.Key = Key;
            g.AssetGUID = GUID;
            if (_components == null)
                return g;

            foreach (var component in SetupOrdered())
            {
                component.SetupRuntimeCommand(g);
            }

            return g;
        }
    }
}

