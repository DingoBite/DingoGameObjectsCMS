#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public sealed class DiskModAssetRepository
    {
        private readonly string _modRootAbs;
        private readonly string _mod;

        public DiskModAssetRepository(string modRootAbs, string mod)
        {
            _modRootAbs = Path.GetFullPath(modRootAbs ?? throw new ArgumentNullException(nameof(modRootAbs)));
            _mod = string.IsNullOrWhiteSpace(mod) ? GameAssetKey.UNDEFINED : mod.Trim();
        }

        public async Task<GameAssetSaveResult> SaveAsync(GameAssetSaveRequest request, CancellationToken ct = default)
        {
            if (request?.Asset == null)
                throw new ArgumentNullException(nameof(GameAssetSaveRequest.Asset));

            var asset = request.Asset;
            ValidateKey(asset.Key);

            var manifest = await ModManifestStore.LoadAsync(_modRootAbs, _mod, ct);
            var existingByKey = ModManifestStore.FindByKey(manifest, asset.Key);
            var existingByGuid = asset.GUID.isValid ? ModManifestStore.FindByGuid(manifest, asset.GUID) : null;

            switch (request.Mode)
            {
                case GameAssetSaveMode.Create:
                    if (existingByKey != null)
                        throw new InvalidOperationException($"External asset key already exists: {asset.Key}");
                    if (existingByGuid != null)
                        throw new InvalidOperationException($"External asset GUID already exists: {asset.GUID}");
                    break;
                case GameAssetSaveMode.UpdateExisting:
                    if (existingByKey == null || existingByKey.GUID != asset.GUID)
                        throw new InvalidOperationException($"External asset does not exist for update: {asset.Key} / {asset.GUID}");
                    break;
                case GameAssetSaveMode.UpsertByKey:
                    if (existingByKey != null && request.PreserveExistingGuidOnUpsert && existingByKey.GUID.isValid)
                        asset.SetGuid(existingByKey.GUID);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.Mode), request.Mode, null);
            }

            var relativeJsonPath = !string.IsNullOrWhiteSpace(request.RelativeJsonPath)
                ? GameAssetPathPolicy.NormalizeSlashes(request.RelativeJsonPath)
                : existingByKey?.RelativeJsonPath ?? GameAssetPathPolicy.BuildDefaultRelativeJsonPath(asset.Key);
            var jsonPath = GameAssetPathPolicy.CombineAbsolute(_modRootAbs, relativeJsonPath);

            await GameAssetJsonStore.WriteAsync(jsonPath, asset, ct);

            if (existingByKey != null && !string.Equals(existingByKey.RelativeJsonPath, relativeJsonPath, StringComparison.OrdinalIgnoreCase))
                await GameAssetJsonStore.DeleteAsync(GameAssetPathPolicy.CombineAbsolute(_modRootAbs, existingByKey.RelativeJsonPath), ct);

            var entry = new ModManifestEntry
            {
                Key = asset.Key,
                GUID = asset.GUID,
                RelativeJsonPath = relativeJsonPath
            };
            ModManifestStore.Upsert(manifest, entry);
            await ModManifestStore.SaveAsync(_modRootAbs, manifest, ct);

            var resolvedAsset = asset;
            if (request.RefreshLibrary)
            {
                GameAssetLibraryManifest.ClearRuntimeCaches();
                await GameAssetLibraryManifest.EnsureInitializedAsync(ct);
            }

            if (request.ResolveSavedAsset && GameAssetLibraryManifest.TryResolve(asset.Key, out var resolved) && resolved is GameAsset gameAsset)
                resolvedAsset = gameAsset;

            return new GameAssetSaveResult
            {
                Key = asset.Key,
                Guid = asset.GUID,
                JsonPath = jsonPath,
                RelativeJsonPath = relativeJsonPath,
                ManifestEntry = entry,
                Asset = resolvedAsset
            };
        }

        public async Task<GameAssetScriptableObject> LoadAsync(GameAssetKey key, CancellationToken ct = default)
        {
            ValidateKey(key);
            var manifest = await ModManifestStore.LoadAsync(_modRootAbs, _mod, ct);
            var entry = ModManifestStore.FindByKey(manifest, key);
            if (entry == null)
                return null;

            return await GameAssetJsonStore.ReadAsync(GameAssetPathPolicy.CombineAbsolute(_modRootAbs, entry.RelativeJsonPath), ct);
        }

        public async Task<IReadOnlyList<ModManifestEntry>> ListAsync(CancellationToken ct = default)
        {
            var manifest = await ModManifestStore.LoadAsync(_modRootAbs, _mod, ct);
            return manifest.Assets.ToArray();
        }

        public async Task<bool> DeleteAsync(GameAssetDeleteRequest request, CancellationToken ct = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ValidateKey(request.Key);
            var manifest = await ModManifestStore.LoadAsync(_modRootAbs, _mod, ct);
            if (!ModManifestStore.Remove(manifest, request.Key, out var removed))
                return false;

            await GameAssetJsonStore.DeleteAsync(GameAssetPathPolicy.CombineAbsolute(_modRootAbs, removed.RelativeJsonPath), ct);
            await ModManifestStore.SaveAsync(_modRootAbs, manifest, ct);

            if (request.RefreshLibrary)
            {
                GameAssetLibraryManifest.ClearRuntimeCaches();
                await GameAssetLibraryManifest.EnsureInitializedAsync(ct);
            }

            return true;
        }

        private static void ValidateKey(GameAssetKey key)
        {
            if (string.IsNullOrWhiteSpace(key.Mod)
                || string.IsNullOrWhiteSpace(key.Type)
                || string.IsNullOrWhiteSpace(key.Key)
                || string.IsNullOrWhiteSpace(key.Version))
            {
                throw new ArgumentException($"Full GameAssetKey is required: {key}");
            }
        }

    }
}
#endif
