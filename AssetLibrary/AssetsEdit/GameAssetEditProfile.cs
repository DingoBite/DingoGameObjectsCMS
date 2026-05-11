#if NEWTONSOFT_EXISTS
using System;
using DingoGameObjectsCMS.AssetObjects;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public abstract class GameAssetEditProfile
    {
        public abstract string EditableMod { get; }
        public abstract string FallbackType { get; }

        public virtual string DisplayName => "GameAsset Edit";
        public virtual string AssetsRootSubPath => GameAssetModPathPolicy.DEFAULT_ASSETS_ROOT_SUB_PATH;

        public abstract GameAsset CreateDefaultAsset();

        public virtual bool CanEditAsset(GameAsset asset)
        {
            return asset != null;
        }

        public virtual string BuildAssetLabel(GameAsset asset)
        {
            if (asset == null)
                return string.Empty;

            return !string.IsNullOrWhiteSpace(asset.name)
                ? $"{asset.name.Trim()} ({asset.Key})"
                : asset.Key.ToString();
        }

        public virtual GameAssetEditValidationReport Validate(GameAsset asset)
        {
            return asset == null
                ? GameAssetEditValidationReport.Valid("No GameAsset open. Nothing to validate.")
                : GameAssetEditValidationReport.Valid($"Valid: {asset.Key}");
        }
    }
}
#endif
