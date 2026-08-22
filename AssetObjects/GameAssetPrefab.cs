using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects
{
    [Serializable, Preserve]
    public class GameAssetOverrides
    {
        public List<string> RemovedComponents;

        public List<JObject> OverrideComponents;

        public List<string> RemovedFields;

        public Dictionary<string, JToken> OverrideFields;

        public bool HasAny =>
            RemovedComponents is { Count: > 0 } || OverrideComponents is { Count: > 0 }
            || RemovedFields is { Count: > 0 } || OverrideFields is { Count: > 0 };
    }

    [Serializable, Preserve]
    public class GameAssetPrefab : GameAssetOverrides
    {
        public GameAssetKey Base;

        public bool HasBase =>
            !string.IsNullOrWhiteSpace(Base.Mod) || !string.IsNullOrWhiteSpace(Base.Type)
            || !string.IsNullOrWhiteSpace(Base.Key) || !string.IsNullOrWhiteSpace(Base.Version);
    }
}
