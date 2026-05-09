using System;
using System.Collections.Generic;
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

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLock
    {
        public int FormatVersion = 1;
        public Dictionary<string, GameAssetLibraryLockMod> Mods = new();
        public Dictionary<string, GameAssetLibraryLockEntry> Entries = new();

        public bool TryGet(GameAssetKey key, out GameAssetLibraryLockEntry entry)
        {
            entry = null;
            return Entries != null && Entries.TryGetValue(GameAssetIdentityKey.Normalize(key), out entry);
        }

        public void Set(GameAssetKey key, GameAssetLibraryLockEntry entry)
        {
            Entries ??= new Dictionary<string, GameAssetLibraryLockEntry>();
            Entries[GameAssetIdentityKey.Normalize(key)] = entry;
        }

        public bool TryGetMod(string mod, out GameAssetLibraryLockMod entry)
        {
            entry = null;
            return Mods != null && Mods.TryGetValue(GameAssetIdentityKey.NormalizeMod(mod), out entry);
        }

        public void SetMod(GameAssetLibraryLockMod entry)
        {
            if (entry == null)
                return;

            Mods ??= new Dictionary<string, GameAssetLibraryLockMod>();
            Mods[GameAssetIdentityKey.NormalizeMod(entry.Mod)] = entry;
        }
    }

    [Serializable, Preserve]
    public sealed class GameAssetLibraryLockMod
    {
        public string Mod;
        public int ManifestVersion;
        public string GeneratedUtc;

        public GameAssetLibraryLockMod() { }

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
        public GameAssetKey ResolvedKey;
        public Hash128 ResolvedGuid;

        public GameAssetLibraryLockEntry() { }

        public GameAssetLibraryLockEntry(GameAssetKey resolvedKey, Hash128 resolvedGuid)
        {
            ResolvedKey = resolvedKey;
            ResolvedGuid = resolvedGuid;
        }
    }

    public static class GameAssetLibraryLockIssueKinds
    {
        public const string MissingGuid = "missing_guid";
        public const string MissingResolvedKey = "missing_resolved_key";
        public const string GuidKeyMismatch = "guid_key_mismatch";
        public const string IdentityMissingInCurrentLibrary = "identity_missing_in_current_library";
        public const string MissingMod = "missing_mod";
        public const string ModChanged = "mod_changed";
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
