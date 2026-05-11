#if NEWTONSOFT_EXISTS
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public enum GameAssetSaveMode
    {
        Create,
        UpsertByKey,
        UpdateExisting
    }

    public sealed class GameAssetSaveRequest
    {
        public GameAsset Asset;
        public GameAssetSaveMode Mode = GameAssetSaveMode.UpsertByKey;
        public string RelativeJsonPath;
        public bool PreserveExistingGuidOnUpsert = true;
        public bool RefreshLibrary = true;
        public bool ResolveSavedAsset = true;
    }

    public sealed class GameAssetSaveResult
    {
        public GameAssetKey Key;
        public Hash128 Guid;
        public string JsonPath;
        public string RelativeJsonPath;
        public ModManifestEntry ManifestEntry;
        public GameAsset Asset;
    }
    
    public sealed class GameAssetDeleteRequest
    {
        public GameAssetKey Key;
        public bool RefreshLibrary = true;
    }
}
#endif
