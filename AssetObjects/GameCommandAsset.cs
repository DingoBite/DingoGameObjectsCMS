using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using Newtonsoft.Json;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects
{
    [CreateAssetMenu(menuName = MENU_PREFIX + "GameCommand")]
    public class GameCommandAsset : GameAssetScriptableObject
    {
        [SerializeReference, SubclassSelector, JsonProperty("Parameters", ItemTypeNameHandling = TypeNameHandling.Auto)] private List<GameAssetParameter> _parameters;
        [JsonIgnore] public IReadOnlyList<GameAssetParameter> Parameters => _parameters;
        
        public virtual void SetupRuntimeObject(GameRuntimeCommand c)
        {
            c.Key = Key;
            c.AssetGUID = GUID;
            if (_parameters == null)
                return;

            foreach (var parameter in _parameters)
            {
                parameter.SetupRuntimeCommand(c);
            }
        }
    }
}