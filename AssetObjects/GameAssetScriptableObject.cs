using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using NaughtyAttributes;
using Newtonsoft.Json;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects
{
    public abstract class GameAssetScriptableObject : ScriptableObject
    {
        public const string MENU_PREFIX = "Game Assets/";

        [SerializeField, JsonProperty("Key")] private GameAssetKey _key = new (GameAssetKey.UNDEFINED, GameAssetKey.NONE, GameAssetKey.NONE, GameAssetKey.ZERO_V);
        [SerializeField, ReadOnly, JsonProperty("GUID")] private Hash128 _guid = IdUtils.NewHash128FromGuid();

        [JsonIgnore] public Hash128 GUID => _guid;
        [JsonIgnore] public GameAssetKey Key => _key;
        
        private void OnValidate()
        {
            if (_guid.isValid)
                return;
            _guid = Hash128.Parse(Guid.NewGuid().ToString());
        }
    }
}