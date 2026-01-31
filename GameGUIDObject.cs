using System;
using NaughtyAttributes;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS
{
    [Serializable, Preserve]
    public abstract class GameGUIDObject
    {
        [SerializeField, ReadOnly, AllowNesting, JsonProperty("GUID")] private Hash128 _guid = IdUtils.NewHash128FromGuid();
        
        [JsonIgnore]
        public Hash128 GUID => _guid;
    }
}