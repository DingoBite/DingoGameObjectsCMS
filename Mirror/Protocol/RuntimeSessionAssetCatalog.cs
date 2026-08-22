using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;

namespace DingoGameObjectsCMS.Mirror.Protocol
{
    public sealed class RuntimeSessionAssetCatalog
    {
        private readonly GameAssetSessionCatalog _catalog;
        private readonly RuntimeAssetCatalogEntry[] _manifestEntries;

        public IReadOnlyList<RuntimeAssetCatalogEntry> ManifestEntries => _manifestEntries;

        private RuntimeSessionAssetCatalog(
            GameAssetSessionCatalog catalog,
            RuntimeAssetCatalogEntry[] manifestEntries)
        {
            _catalog = catalog;
            _manifestEntries = manifestEntries;
        }

        public static RuntimeSessionAssetCatalog FromLock(GameAssetLibraryLock assetLock)
        {
            if (assetLock == null)
                throw new ArgumentNullException(nameof(assetLock));
            if (assetLock.FormatVersion != GameAssetLibraryLock.CURRENT_FORMAT_VERSION)
            {
                throw new InvalidOperationException(
                    $"Asset lock format {assetLock.FormatVersion} does not match required format {GameAssetLibraryLock.CURRENT_FORMAT_VERSION}.");
            }
            if (!assetLock.IsReadOnly)
            {
                throw new InvalidOperationException(
                    "A sealed GameAsset library lock is required to create "
                    + "a runtime session asset catalog.");
            }

            var catalog = assetLock.AssetCatalog;
            if (catalog.Count == 0)
                throw new InvalidOperationException("Asset lock has no resolved GameAssets.");

            var manifestEntries = new RuntimeAssetCatalogEntry[catalog.Count];
            for (var i = 0; i < catalog.Entries.Count; i++)
            {
                var entry = catalog.Entries[i];
                var resolved = entry.Asset;
                manifestEntries[i] = new RuntimeAssetCatalogEntry
                {
                    AssetNetId = entry.AssetIndex.Value,
                    ExactKey = CanonicalKey(resolved.ExactKey),
                    AssetGuid = resolved.AssetGuid.ToString(),
                    MaterializedContentHash =
                        resolved.MaterializedContentHash,
                };
            }

            return new RuntimeSessionAssetCatalog(catalog, manifestEntries);
        }

        public bool TryGet(uint assetNetId, out ResolvedGameAssetReference asset)
        {
            var index = new GameAssetIndex(assetNetId);
            return _catalog.TryGet(in index, out asset);
        }

        public ResolvedGameAssetReference GetRequired(uint assetNetId)
        {
            var index = new GameAssetIndex(assetNetId);
            return _catalog.GetRequired(in index);
        }

        public uint GetRequiredNetId(in ResolvedGameAssetReference asset)
        {
            return _catalog.GetRequiredIndex(in asset).Value;
        }

        private static string CanonicalKey(in GameAssetKey key)
        {
            return $"{Part(key.Mod)}|{Part(key.Type)}|{Part(key.Key)}|{Part(key.Version)}";
        }

        private static string Part(string value)
        {
            value ??= string.Empty;
            return $"{value.Length}:{value}";
        }

    }
}
