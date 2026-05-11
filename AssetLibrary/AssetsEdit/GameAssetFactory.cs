#if NEWTONSOFT_EXISTS
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class GameAssetFactory
    {
        public static GameAsset Create(GameAssetKey key, Hash128 guid, IEnumerable<GameAssetComponent> components, Hash128 sourceGuid = default)
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            asset.ResetToDefault(key, guid);
            asset.SetSourceAssetGuid(sourceGuid);
            asset.SetComponents(components);
            return asset;
        }
    }
}
#endif