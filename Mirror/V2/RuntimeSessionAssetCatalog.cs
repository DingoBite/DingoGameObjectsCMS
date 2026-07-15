using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Immutable session-local mapping. AssetNetId values never escape the
    /// manifest/session that created this catalog.
    /// </summary>
    public sealed class RuntimeSessionAssetCatalog
    {
        private readonly Dictionary<uint, ResolvedGameAssetReference> _byNetId;
        private readonly Dictionary<AssetIdentity, uint> _netIdByIdentity;
        private readonly RuntimeAssetCatalogEntry[] _manifestEntries;

        public IReadOnlyList<RuntimeAssetCatalogEntry> ManifestEntries => _manifestEntries;

        private RuntimeSessionAssetCatalog(
            Dictionary<uint, ResolvedGameAssetReference> byNetId,
            Dictionary<AssetIdentity, uint> netIdByIdentity,
            RuntimeAssetCatalogEntry[] manifestEntries)
        {
            _byNetId = byNetId;
            _netIdByIdentity = netIdByIdentity;
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
            if (assetLock.Entries == null || assetLock.Entries.Count == 0)
                throw new InvalidOperationException("Asset lock has no resolved GameAssets.");

            var requestedEntries = assetLock.Entries
                .Select(pair => pair.Value ?? throw new InvalidOperationException($"Asset lock entry '{pair.Key}' is null."))
                .OrderBy(entry => GameAssetIdentityKey.Normalize(entry.ResolvedKey), StringComparer.Ordinal)
                .ThenBy(entry => entry.ResolvedKey.Version, StringComparer.Ordinal)
                .ThenBy(entry => entry.ResolvedGuid.ToString(), StringComparer.Ordinal)
                .ToArray();

            var uniqueEntries = new Dictionary<AssetIdentity, GameAssetLibraryLockEntry>();
            for (var i = 0; i < requestedEntries.Length; i++)
            {
                var entry = requestedEntries[i];
                Validate(entry);
                var resolved = new ResolvedGameAssetReference(
                    entry.ResolvedKey,
                    entry.ResolvedGuid,
                    entry.MaterializedContentHash);
                uniqueEntries.TryAdd(new AssetIdentity(resolved), entry);
            }

            var ordered = uniqueEntries.Values
                .OrderBy(entry => GameAssetIdentityKey.Normalize(entry.ResolvedKey), StringComparer.Ordinal)
                .ThenBy(entry => entry.ResolvedKey.Version, StringComparer.Ordinal)
                .ThenBy(entry => entry.ResolvedGuid.ToString(), StringComparer.Ordinal)
                .ToArray();

            var byNetId = new Dictionary<uint, ResolvedGameAssetReference>(ordered.Length);
            var netIdByIdentity = new Dictionary<AssetIdentity, uint>(ordered.Length);
            var manifestEntries = new RuntimeAssetCatalogEntry[ordered.Length];
            for (var i = 0; i < ordered.Length; i++)
            {
                var entry = ordered[i];
                var netId = checked((uint)i + 1u);
                var resolved = new ResolvedGameAssetReference(entry.ResolvedKey, entry.ResolvedGuid, entry.MaterializedContentHash);
                var identity = new AssetIdentity(resolved);
                netIdByIdentity.Add(identity, netId);
                byNetId.Add(netId, resolved);
                manifestEntries[i] = new RuntimeAssetCatalogEntry
                {
                    AssetNetId = netId,
                    ExactKey = CanonicalKey(entry.ResolvedKey),
                    AssetGuid = entry.ResolvedGuid.ToString(),
                    MaterializedContentHash = entry.MaterializedContentHash,
                };
            }

            return new RuntimeSessionAssetCatalog(byNetId, netIdByIdentity, manifestEntries);
        }

        public bool TryGet(uint assetNetId, out ResolvedGameAssetReference asset)
        {
            return _byNetId.TryGetValue(assetNetId, out asset);
        }

        public ResolvedGameAssetReference GetRequired(uint assetNetId)
        {
            if (_byNetId.TryGetValue(assetNetId, out var asset))
                return asset;
            throw new KeyNotFoundException($"Session asset catalog does not contain AssetNetId {assetNetId}.");
        }

        public uint GetRequiredNetId(in ResolvedGameAssetReference asset)
        {
            if (_netIdByIdentity.TryGetValue(new AssetIdentity(asset), out var netId))
                return netId;
            throw new KeyNotFoundException($"Session asset catalog does not contain exact GameAsset '{asset.ExactKey}' ({asset.AssetGuid}, {asset.MaterializedContentHash}).");
        }

        private static void Validate(GameAssetLibraryLockEntry entry)
        {
            if (!entry.ResolvedGuid.isValid)
                throw new InvalidOperationException($"Asset lock entry '{entry.ResolvedKey}' has no resolved GUID.");
            if (string.IsNullOrWhiteSpace(entry.ResolvedKey.Version))
                throw new InvalidOperationException($"Asset lock entry '{entry.ResolvedKey}' is not exact.");
            if (string.IsNullOrWhiteSpace(entry.MaterializedContentHash))
                throw new InvalidOperationException($"Asset lock entry '{entry.ResolvedKey}' has no materialized content hash.");
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

        private readonly struct AssetIdentity : IEquatable<AssetIdentity>
        {
            private readonly GameAssetKey _key;
            private readonly Hash128 _guid;
            private readonly string _contentHash;

            public AssetIdentity(in ResolvedGameAssetReference asset)
            {
                _key = asset.ExactKey;
                _guid = asset.AssetGuid;
                _contentHash = asset.MaterializedContentHash;
            }

            public bool Equals(AssetIdentity other)
            {
                return KeysEqual(_key, other._key)
                       && _guid == other._guid
                       && string.Equals(_contentHash, other._contentHash, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is AssetIdentity other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(_key.Mod, StringComparer.Ordinal);
                hash.Add(_key.Type, StringComparer.Ordinal);
                hash.Add(_key.Key, StringComparer.Ordinal);
                hash.Add(_key.Version, StringComparer.Ordinal);
                hash.Add(_guid);
                hash.Add(_contentHash, StringComparer.Ordinal);
                return hash.ToHashCode();
            }

            private static bool KeysEqual(in GameAssetKey left, in GameAssetKey right)
            {
                return string.Equals(left.Mod, right.Mod, StringComparison.Ordinal)
                       && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
                       && string.Equals(left.Key, right.Key, StringComparison.Ordinal)
                       && string.Equals(left.Version, right.Version, StringComparison.Ordinal);
            }
        }
    }
}
