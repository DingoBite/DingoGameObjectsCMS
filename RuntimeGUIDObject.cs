using System;
using Newtonsoft.Json;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS
{
    public enum StoreRealm : byte
    {
        Server = 0,
        Client = 1,
    }
    
    [Serializable, Preserve]
    public abstract class RuntimeGUIDObject : GameGUIDObject
    {
        [SerializeField, NaughtyAttributes.ReadOnly, JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)] public long InstanceId;
        [SerializeField, NaughtyAttributes.ReadOnly, JsonProperty] public FixedString32Bytes StoreId;
        
        [NonSerialized, JsonIgnore] public StoreRealm Realm;
    }
}