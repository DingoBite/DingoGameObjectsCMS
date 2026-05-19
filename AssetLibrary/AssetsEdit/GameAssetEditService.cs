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
            return CollectEditableAssets(includeExternal)
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

            return await SaveAsAsync(asset, CreateNewKey(asset), ct);
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

            var key = CreateNextVersionKey(previousKey);
            return await SaveAsAsync(asset, key, ct);
        }

        public GameAssetKey CreateNewKey(GameAsset asset)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            return GameAssetKeyPolicy.CreateUniqueKey(asset, Profile.EditableMod, Profile.FallbackType, KeyExists);
        }

        public GameAssetKey CreateNextVersionKey(GameAssetKey previousKey)
        {
            if (!IsEditableKey(previousKey))
                throw new InvalidOperationException($"Only {Profile.EditableMod} assets can create new versions.");

            return GameAssetKeyPolicy.CreateNextVersionKey(previousKey, KeyExists);
        }

        public async Task<GameAssetSaveResult> SaveAsAsync(GameAsset asset, GameAssetKey key, CancellationToken ct = default)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            key = NormalizeExplicitKey(asset, key);
            if (!IsEditableKey(key))
                throw new InvalidOperationException($"Only {Profile.EditableMod} assets can be saved by this editor.");
            if (KeyExists(key))
                throw new InvalidOperationException($"GameAsset key already exists: {key}");

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

        public bool KeyExists(GameAssetKey key)
        {
            if (GameAssetLibraryManifest.TryResolve(key, out _))
                return true;

            try
            {
                var entries = CreateRepository().List();
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i].Key == key)
                        return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to check editable GameAsset key '{key}': {ex.Message}");
            }

            return false;
        }

        private GameAssetKey NormalizeExplicitKey(GameAsset asset, GameAssetKey key)
        {
            var mod = GameAssetKeyPolicy.NormalizeModName(key.Mod, Profile.EditableMod);
            var typeFallback = string.IsNullOrWhiteSpace(asset.Key.Type) || asset.Key.Type == GameAssetKey.NONE
                ? Profile.FallbackType
                : asset.Key.Type;
            var keyFallback = string.IsNullOrWhiteSpace(asset.Key.Key) || asset.Key.Key == GameAssetKey.NONE
                ? typeFallback
                : asset.Key.Key;
            var type = GameAssetKeyPolicy.NormalizeSegment(key.Type, typeFallback);
            var slug = GameAssetKeyPolicy.BuildSlug(key.Key, keyFallback);
            var version = string.IsNullOrWhiteSpace(key.Version) ? GameAssetKey.ZERO_V : key.Version.Trim();
            return new GameAssetKey(mod, type, slug, version);
        }

        private DiskModAssetRepository CreateRepository()
        {
            return new DiskModAssetRepository(GetModRootPath(Profile.EditableMod), Profile.EditableMod);
        }

        private IEnumerable<GameAsset> CollectEditableAssets(bool includeExternal)
        {
            var byGuid = new Dictionary<Hash128, GameAsset>();
            var byKey = new Dictionary<string, GameAsset>(StringComparer.OrdinalIgnoreCase);

            foreach (var asset in GameAssetLibraryManifest.CollectAllAssets(includeExternal).Values.OfType<GameAsset>())
                AddAsset(asset, byGuid, byKey);

            if (includeExternal)
                AddRepositoryAssets(byGuid, byKey);

            return byGuid.Values.Concat(byKey.Values);
        }

        private void AddRepositoryAssets(Dictionary<Hash128, GameAsset> byGuid, Dictionary<string, GameAsset> byKey)
        {
            try
            {
                var repository = CreateRepository();
                var entries = repository.List();
                for (var i = 0; i < entries.Count; i++)
                {
                    if (repository.Load(entries[i].Key) is GameAsset asset)
                        AddAsset(asset, byGuid, byKey);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to list editable GameAssets for '{Profile.EditableMod}': {ex.Message}");
            }
        }

        private void AddAsset(GameAsset asset, Dictionary<Hash128, GameAsset> byGuid, Dictionary<string, GameAsset> byKey)
        {
            if (asset == null || !Profile.CanEditAsset(asset))
                return;

            if (asset.GUID.isValid)
            {
                byGuid[asset.GUID] = asset;
                return;
            }

            byKey[asset.Key.ToString()] = asset;
        }
    }
}
#endif
