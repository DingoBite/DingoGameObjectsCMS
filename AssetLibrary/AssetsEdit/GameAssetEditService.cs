#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public sealed class GameAssetEditService : IGameAssetEditService
    {
        private readonly string _assetsRootAbs;
        private readonly bool _refreshLibraryOnChange;
        private readonly bool _resolveSavedAsset;

        public GameAssetEditProfile Profile { get; }

        public GameAssetEditService(
            GameAssetEditProfile profile,
            string assetsRootAbs = null,
            bool refreshLibraryOnChange = true,
            bool resolveSavedAsset = true)
        {
            Profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _assetsRootAbs = GameAssetModPathPolicy.GetAssetsRootPath(assetsRootAbs, Profile.AssetsRootSubPath);
            _refreshLibraryOnChange = refreshLibraryOnChange;
            _resolveSavedAsset = resolveSavedAsset;
        }

        public IReadOnlyList<GameAsset> ListEditableAssets(bool includeExternal = true)
        {
            return GameAssetLibraryManifest.CollectAllAssets(includeExternal)
                .Values
                .OfType<GameAsset>()
                .Where(Profile.CanEditAsset)
                .OrderBy(asset => asset.Key.Mod)
                .ThenBy(Profile.BuildAssetLabel)
                .ToArray();
        }

        public GameAsset CreateDefaultAsset()
        {
            var asset = Profile.CreateDefaultAsset();
            if (asset == null)
                throw new InvalidOperationException($"{Profile.GetType().Name} returned null default asset.");

            return asset;
        }

        public async Task<GameAssetSaveResult> SaveNewAsync(GameAsset asset, CancellationToken ct = default)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            var key = GameAssetKeyPolicy.CreateUniqueKey(asset, Profile.EditableMod, Profile.FallbackType, KeyExists);
            asset.SetIdentity(key, IdUtils.NewHash128FromGuid());
            return await SaveCreatedAssetAsync(asset, ct);
        }

        public async Task<GameAssetSaveResult> SaveNewVersionAsync(GameAsset asset, GameAssetKey previousKey, Hash128 previousGuid, CancellationToken ct = default)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));
            if (!previousGuid.isValid)
                throw new ArgumentException("Previous asset GUID is required.", nameof(previousGuid));
            if (!IsEditableKey(previousKey))
                throw new InvalidOperationException($"Only {Profile.EditableMod} assets can create new versions.");

            var existing = await CreateRepository().LoadAsync(previousKey, ct);
            if (existing == null || existing.GUID != previousGuid)
                throw new InvalidOperationException($"Existing editable asset does not match key/guid: {previousKey} / {previousGuid}");

            var key = GameAssetKeyPolicy.CreateNextVersionKey(previousKey, KeyExists);
            asset.SetIdentity(key, IdUtils.NewHash128FromGuid());
            return await SaveCreatedAssetAsync(asset, ct);
        }

        public async Task<bool> DeleteAsync(GameAssetKey key, CancellationToken ct = default)
        {
            if (!IsEditableKey(key))
                throw new InvalidOperationException($"Only {Profile.EditableMod} assets can be deleted.");

            return await CreateRepository().DeleteAsync(new GameAssetDeleteRequest
            {
                Key = key,
                RefreshLibrary = _refreshLibraryOnChange,
            }, ct);
        }

        public GameAssetEditValidationReport Validate(GameAsset asset)
        {
            return Profile.Validate(asset);
        }

        public string GetAssetsRootPath()
        {
            return _assetsRootAbs;
        }

        public string GetModRootPath(string mod)
        {
            return GameAssetModPathPolicy.GetModRootPath(mod, Profile.EditableMod, _assetsRootAbs, Profile.AssetsRootSubPath);
        }

        public bool IsEditableKey(GameAssetKey key)
        {
            return string.Equals(key.Mod, Profile.EditableMod, StringComparison.OrdinalIgnoreCase);
        }

        private async Task<GameAssetSaveResult> SaveCreatedAssetAsync(GameAsset asset, CancellationToken ct)
        {
            return await CreateRepository().SaveAsync(new GameAssetSaveRequest
            {
                Asset = asset,
                Mode = GameAssetSaveMode.Create,
                PreserveExistingGuidOnUpsert = true,
                RefreshLibrary = _refreshLibraryOnChange,
                ResolveSavedAsset = _resolveSavedAsset,
            }, ct);
        }

        private bool KeyExists(GameAssetKey key)
        {
            return GameAssetLibraryManifest.TryResolve(key, out _);
        }

        private DiskModAssetRepository CreateRepository()
        {
            return new DiskModAssetRepository(GetModRootPath(Profile.EditableMod), Profile.EditableMod);
        }
    }
}
#endif
