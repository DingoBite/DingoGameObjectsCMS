using System.Collections.Generic;
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

        [JsonIgnore] public IReadOnlyList<GameAssetComponent> Components => _components;

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
        
        public GameRuntimeCommand CreateRuntimeCommand(FixedString32Bytes attachToStoreId)
        {
            var g = new GameRuntimeCommand();
            g.Key = Key;
            g.AssetGUID = GUID;
            g.ApplyToStoreId = attachToStoreId;
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