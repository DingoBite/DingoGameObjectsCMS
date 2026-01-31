using System;
using NaughtyAttributes;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS
{
    [Serializable, Preserve]
    public abstract class RuntimeGUIDObject : GameGUIDObject
    {
        [SerializeField, ReadOnly, JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)] public long InstanceId;
        [SerializeField, ReadOnly, JsonProperty] public Hash128 StoreId;
    }
}