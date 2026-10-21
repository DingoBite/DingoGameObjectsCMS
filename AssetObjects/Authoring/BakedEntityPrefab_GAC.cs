using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using Newtonsoft.Json;
using Unity.Entities.Serialization;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    [Serializable, Preserve]
    public class BakedEntityPrefab_GAC : GameAssetComponent
    {
        [JsonConverter(typeof(BakedEntityPrefabReferenceJsonConverter))] public EntityPrefabReference Prefab;

        public override void SetupRuntimeComponent(GameRuntimeObject runtimeObject)
        {
            runtimeObject.AddOrReplace(new BakedEntityPrefab_GRC { PrefabGuid = Prefab.AssetGUID });
        }
    }

    [Serializable, Preserve]
    public class BakedEntityPrefab_GRC : GameRuntimeComponent
    {
        public Hash128 PrefabGuid;

        [JsonIgnore] public EntityPrefabReference Prefab => new EntityPrefabReference(PrefabGuid);
    }
}
