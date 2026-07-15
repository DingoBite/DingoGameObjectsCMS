using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
            return $"{NormalizePart(mod, GameAssetKey.UNDEFINED)}.{NormalizePart(type, GameAssetKey.NONE)}.{NormalizePart(key, GameAssetKey.NONE)}";
        }

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
            var version = string.IsNullOrWhiteSpace(key.Version)
                ? LATEST
                : key.Version.Trim().ToLowerInvariant();
            return $"{identity}@{version}";
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

        public int FormatVersion { get; }
        public bool IsReadOnly { get; private set; }
        public IReadOnlyDictionary<string, GameAssetLibraryLockMod> Mods => _readOnlyMods;
        public IReadOnlyDictionary<string, GameAssetLibraryLockEntry> Entries => _readOnlyEntries;

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
            IsReadOnly = true;
            return this;
        }

        internal bool TryGetRequestedKey(string normalizedKey, out GameAssetKey key)
        {
            return _requestsByNormalizedKey.TryGetValue(normalizedKey, out key);
        }

        private void RequireMutable()
        {
            if (IsReadOnly)
                throw new InvalidOperationException("The GameAsset library lock is sealed and cannot be changed.");
        }
    }

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLockMod
    {
        public string Mod { get; }
        public int ManifestVersion { get; }
        public string GeneratedUtc { get; }

        public GameAssetLibraryLockMod(string mod, int manifestVersion, string generatedUtc)
        {
            Mod = mod;
            ManifestVersion = manifestVersion;
            GeneratedUtc = generatedUtc;
        }
    }

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLockEntry
    {
        public GameAssetKey ResolvedKey { get; }
        public Hash128 ResolvedGuid { get; }
        public string MaterializedContentHash { get; }

        public GameAssetLibraryLockEntry(GameAssetKey resolvedKey, Hash128 resolvedGuid, string materializedContentHash)
        {
            ResolvedKey = resolvedKey;
            ResolvedGuid = resolvedGuid;
            MaterializedContentHash = materializedContentHash;
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
