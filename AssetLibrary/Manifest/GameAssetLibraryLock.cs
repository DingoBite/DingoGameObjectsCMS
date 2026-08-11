using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary
{
    public static class GameAssetIdentityKey
    {
        public static string Normalize(GameAssetKey key) => Normalize(key.Mod, key.Type, key.Key);
        public static string NormalizeMod(string mod) => NormalizePart(mod, GameAssetKey.UNDEFINED);

        public static string Normalize(string mod, string type, string key)
        {
            return string.Concat(
                EncodePart(NormalizePart(mod, GameAssetKey.UNDEFINED)),
                "|",
                EncodePart(NormalizePart(type, GameAssetKey.NONE)),
                "|",
                EncodePart(NormalizePart(key, GameAssetKey.NONE)));
        }

        private static string EncodePart(string value) =>
            $"{value.Length}:{value}";

        private static string NormalizePart(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value))
                return fallback;

            return value.Trim().ToLowerInvariant();
        }
    }

    public static class GameAssetRequestKey
    {
        private const string LATEST = "<latest>";

        public static string Normalize(GameAssetKey key)
        {
            var identity = GameAssetIdentityKey.Normalize(key);
            if (string.IsNullOrWhiteSpace(key.Version))
                return $"{identity}|L:{LATEST}";

            var version = key.Version.Trim().ToLowerInvariant();
            return $"{identity}|V:{version.Length}:{version}";
        }
    }

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLock
    {
        public const int CURRENT_FORMAT_VERSION = 2;

        private readonly Dictionary<string, GameAssetLibraryLockMod> _mods = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GameAssetLibraryLockEntry> _entries = new(StringComparer.Ordinal);
        private readonly Dictionary<string, GameAssetKey> _requestsByNormalizedKey = new(StringComparer.Ordinal);
        private readonly ReadOnlyDictionary<string, GameAssetLibraryLockMod> _readOnlyMods;
        private readonly ReadOnlyDictionary<string, GameAssetLibraryLockEntry> _readOnlyEntries;
        private GameAssetSessionCatalog _assetCatalog;

        public int FormatVersion { get; }
        public bool IsReadOnly { get; private set; }
        public IReadOnlyDictionary<string, GameAssetLibraryLockMod> Mods => _readOnlyMods;
        public IReadOnlyDictionary<string, GameAssetLibraryLockEntry> Entries => _readOnlyEntries;
        public GameAssetSessionCatalog AssetCatalog => IsReadOnly
            ? _assetCatalog
            : throw new InvalidOperationException(
                "The GameAsset session catalog is available only after the "
                + "library lock is sealed.");

        public GameAssetLibraryLock(int formatVersion = CURRENT_FORMAT_VERSION)
        {
            FormatVersion = formatVersion;
            _readOnlyMods = new ReadOnlyDictionary<string, GameAssetLibraryLockMod>(_mods);
            _readOnlyEntries = new ReadOnlyDictionary<string, GameAssetLibraryLockEntry>(_entries);
        }

        public bool TryGet(GameAssetKey key, out GameAssetLibraryLockEntry entry)
        {
            entry = null;
            return _entries.TryGetValue(GameAssetRequestKey.Normalize(key), out entry);
        }

        public void Set(GameAssetKey key, GameAssetLibraryLockEntry entry)
        {
            RequireMutable();
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));
            var normalized = GameAssetRequestKey.Normalize(key);
            _entries[normalized] = entry;
            _requestsByNormalizedKey[normalized] = key;
        }

        public bool TryGetMod(string mod, out GameAssetLibraryLockMod entry)
        {
            entry = null;
            return _mods.TryGetValue(GameAssetIdentityKey.NormalizeMod(mod), out entry);
        }

        public void SetMod(GameAssetLibraryLockMod entry)
        {
            RequireMutable();
            if (entry == null)
                throw new ArgumentNullException(nameof(entry));

            _mods[GameAssetIdentityKey.NormalizeMod(entry.Mod)] = entry;
        }

        public GameAssetLibraryLock Seal()
        {
            if (IsReadOnly)
                return this;

            _assetCatalog = GameAssetSessionCatalog.Build(_entries.Values);
            IsReadOnly = true;
            return this;
        }

        /// <summary>
        /// Resolves an authored request key, including a latest-version
        /// request, to the compact exact index of its pinned session asset.
        /// </summary>
        public bool TryResolveAssetIndex(
            in GameAssetKey requestedKey,
            out GameAssetIndex index)
        {
            RequireSealed();
            if (!TryGet(requestedKey, out var entry) || entry == null)
            {
                index = default;
                return false;
            }

            var resolvedKey = entry.ResolvedKey;
            return _assetCatalog.TryGetIndex(in resolvedKey, out index);
        }

        public GameAssetIndex ResolveAssetIndex(
            in GameAssetKey requestedKey)
        {
            if (TryResolveAssetIndex(in requestedKey, out var index))
                return index;

            throw new KeyNotFoundException(
                $"GameAsset request '{requestedKey}' is absent from the "
                + "sealed library lock.");
        }

        public GameAssetIdentityIndex ResolveAssetIdentityIndex(
            in GameAssetKey requestedKey)
        {
            RequireSealed();
            if (!TryGet(requestedKey, out var entry) || entry == null)
            {
                throw new KeyNotFoundException(
                    $"GameAsset request '{requestedKey}' is absent from the "
                    + "sealed library lock.");
            }

            var resolvedKey = entry.ResolvedKey;
            return _assetCatalog.GetRequiredIdentityIndex(in resolvedKey);
        }

        internal bool TryGetRequestedKey(string normalizedKey, out GameAssetKey key)
        {
            return _requestsByNormalizedKey.TryGetValue(normalizedKey, out key);
        }

        private void RequireMutable()
        {
            if (IsReadOnly)
                throw new InvalidOperationException("The GameAsset session resolution snapshot is sealed and cannot be changed.");
        }

        private void RequireSealed()
        {
            if (!IsReadOnly)
            {
                throw new InvalidOperationException(
                    "GameAsset indices are available only after the session "
                    + "resolution snapshot is sealed.");
            }
        }
    }

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLockMod
    {
        public string Mod { get; }
        public int ManifestVersion { get; }
        public string GeneratedUtc { get; }
        public string ContentHash { get; }

        public GameAssetLibraryLockMod(
            string mod,
            int manifestVersion,
            string generatedUtc,
            string contentHash)
        {
            Mod = mod;
            ManifestVersion = manifestVersion;
            GeneratedUtc = generatedUtc;
            ContentHash = contentHash;
        }
    }

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLockEntry
    {
        public GameAssetKey ResolvedKey { get; }
        public Hash128 ResolvedGuid { get; }
        public string MaterializedContentHash { get; }
        public GameAssetScriptableObject ResolvedAsset { get; }
        public bool HasResolvedAsset { get; }

        public GameAssetLibraryLockEntry(
            GameAssetKey resolvedKey,
            Hash128 resolvedGuid,
            string materializedContentHash,
            GameAssetScriptableObject resolvedAsset = null)
        {
            ResolvedKey = resolvedKey;
            ResolvedGuid = resolvedGuid;
            MaterializedContentHash = materializedContentHash;
            ResolvedAsset = resolvedAsset;
            HasResolvedAsset = !ReferenceEquals(resolvedAsset, null);
        }
    }

    public static class GameAssetLibraryLockIssueKinds
    {
        public const string MISSING_GUID = "missing_guid";
        public const string MISSING_RESOLVED_KEY = "missing_resolved_key";
        public const string GUID_KEY_MISMATCH = "guid_key_mismatch";
        public const string IDENTITY_MISSING_IN_CURRENT_LIBRARY = "identity_missing_in_current_library";
        public const string MISSING_MOD = "missing_mod";
        public const string MOD_CHANGED = "mod_changed";
        public const string MISSING_CONTENT_HASH = "missing_content_hash";
    }

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLockReport
    {
        public List<GameAssetLibraryLockIssue> Issues = new();
    }

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLockIssue
    {
        public string Kind;
        public string Identity;
        public string Mod;
        public GameAssetKey LockedResolvedKey;
        public Hash128 LockedResolvedGuid;
        public GameAssetKey CurrentResolvedKey;
        public Hash128 CurrentResolvedGuid;
        public int LockedManifestVersion;
        public string LockedGeneratedUtc;
        public int CurrentManifestVersion;
        public string CurrentGeneratedUtc;
    }
}
