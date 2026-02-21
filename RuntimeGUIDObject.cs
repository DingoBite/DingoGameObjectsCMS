using System;
using Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS
{
    [Serializable, Preserve]
    public abstract class RuntimeGUIDObject : GameGUIDObject
    {
        [SerializeField, NaughtyAttributes.ReadOnly, JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)] public long InstanceId;
        [SerializeField, NaughtyAttributes.ReadOnly, JsonProperty] public FixedString32Bytes StoreId;
    }
}