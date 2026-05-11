using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;

namespace DingoGameObjectsCMS.AssetLibrary
{
    public static class GameAssetLibraryLockBuilder
    {
        public static GameAssetLibraryLock Build()
        {
            var assetLock = new GameAssetLibraryLock();

            foreach (var mod in GameAssetLibraryManifest.CollectLoadedModManifests())
            {
                if (mod == null)
                    continue;

                assetLock.SetMod(new GameAssetLibraryLockMod(mod.Mod, mod.ManifestVersion, mod.GeneratedUtc));
            }

            foreach (var request in GameAssetLibraryManifest.CollectIdentityRequests())
            {
                if (!GameAssetLibraryManifest.TryResolve(request, out var asset) || asset == null)
                    continue;

                assetLock.Set(request, new GameAssetLibraryLockEntry(asset.Key, asset.GUID));
            }

            return assetLock;
        }

        public static bool TryResolve(GameAssetKey key, GameAssetLibraryLock assetLock, out GameAssetScriptableObject asset)
        {
            asset = null;

            if (!IsLatestVersionRequest(key))
                return GameAssetLibraryManifest.TryResolve(key, out asset);

            if (assetLock == null)
                return GameAssetLibraryManifest.TryResolve(key, out asset);

            if (!assetLock.TryGet(key, out var entry) || entry == null)
                return false;

            if (entry.ResolvedGuid.isValid && GameAssetLibraryManifest.TryResolveGuid(entry.ResolvedGuid, out asset))
                return true;

            if (GameAssetLibraryManifest.TryResolve(entry.ResolvedKey, out asset))
                return true;

            asset = null;
            return false;
        }

        public static GameAssetLibraryLockReport CompareWithCurrentLibrary(GameAssetLibraryLock assetLock)
        {
            var report = new GameAssetLibraryLockReport();
            if (assetLock == null)
                return report;

            CompareMods(assetLock, report);

            if (assetLock.Entries == null || assetLock.Entries.Count == 0)
                return report;

            foreach (var kv in assetLock.Entries)
            {
                var identity = kv.Key;
                var entry = kv.Value;
                var request = BuildIdentityRequest(entry.ResolvedKey);

                var hasCurrentIdentity = GameAssetLibraryManifest.TryResolve(request, out var currentIdentityAsset);
                GameAssetScriptableObject currentGuidAsset = null;
                var hasCurrentGuid = entry.ResolvedGuid.isValid && GameAssetLibraryManifest.TryResolveGuid(entry.ResolvedGuid, out currentGuidAsset);
                var hasCurrentResolvedKey = GameAssetLibraryManifest.TryResolve(entry.ResolvedKey, out var currentResolvedKeyAsset);

                if (!hasCurrentIdentity)
                {
                    report.Issues.Add(new GameAssetLibraryLockIssue
                    {
                        Kind = GameAssetLibraryLockIssueKinds.IDENTITY_MISSING_IN_CURRENT_LIBRARY,
                        Identity = identity,
                        LockedResolvedKey = entry.ResolvedKey,
                        LockedResolvedGuid = entry.ResolvedGuid,
                    });
                }

                if (entry.ResolvedGuid.isValid && !hasCurrentGuid)
                {
                    report.Issues.Add(new GameAssetLibraryLockIssue
                    {
                        Kind = GameAssetLibraryLockIssueKinds.MISSING_GUID,
                        Identity = identity,
                        LockedResolvedKey = entry.ResolvedKey,
                        LockedResolvedGuid = entry.ResolvedGuid,
                        CurrentResolvedKey = hasCurrentIdentity ? currentIdentityAsset.Key : default,
                        CurrentResolvedGuid = hasCurrentIdentity ? currentIdentityAsset.GUID : default,
                    });
                }

                if (!hasCurrentResolvedKey)
                {
                    report.Issues.Add(new GameAssetLibraryLockIssue
                    {
                        Kind = GameAssetLibraryLockIssueKinds.MISSING_RESOLVED_KEY,
                        Identity = identity,
                        LockedResolvedKey = entry.ResolvedKey,
                        LockedResolvedGuid = entry.ResolvedGuid,
                        CurrentResolvedKey = hasCurrentIdentity ? currentIdentityAsset.Key : default,
                        CurrentResolvedGuid = hasCurrentIdentity ? currentIdentityAsset.GUID : default,
                    });
                }

                if (HasGuidKeyMismatch(entry, hasCurrentGuid, currentGuidAsset, hasCurrentResolvedKey, currentResolvedKeyAsset))
                {
                    var currentAsset = hasCurrentResolvedKey ? currentResolvedKeyAsset : currentGuidAsset;
                    report.Issues.Add(new GameAssetLibraryLockIssue
                    {
                        Kind = GameAssetLibraryLockIssueKinds.GUID_KEY_MISMATCH,
                        Identity = identity,
                        LockedResolvedKey = entry.ResolvedKey,
                        LockedResolvedGuid = entry.ResolvedGuid,
                        CurrentResolvedKey = currentAsset != null ? currentAsset.Key : default,
                        CurrentResolvedGuid = currentAsset != null ? currentAsset.GUID : default,
                    });
                }
            }

            return report;
        }

        private static void CompareMods(GameAssetLibraryLock assetLock, GameAssetLibraryLockReport report)
        {
            if (assetLock.Mods == null || assetLock.Mods.Count == 0)
                return;

            var currentMods = BuildCurrentModIndex();
            foreach (var kv in assetLock.Mods)
            {
                var lockedMod = kv.Value;
                if (lockedMod == null)
                    continue;

                if (!currentMods.TryGetValue(kv.Key, out var currentMod))
                {
                    report.Issues.Add(new GameAssetLibraryLockIssue
                    {
                        Kind = GameAssetLibraryLockIssueKinds.MISSING_MOD,
                        Mod = lockedMod.Mod,
                        LockedManifestVersion = lockedMod.ManifestVersion,
                        LockedGeneratedUtc = lockedMod.GeneratedUtc,
                    });
                    continue;
                }

                if (currentMod.ManifestVersion != lockedMod.ManifestVersion || !string.Equals(currentMod.GeneratedUtc, lockedMod.GeneratedUtc, StringComparison.Ordinal))
                {
                    report.Issues.Add(new GameAssetLibraryLockIssue
                    {
                        Kind = GameAssetLibraryLockIssueKinds.MOD_CHANGED,
                        Mod = lockedMod.Mod,
                        LockedManifestVersion = lockedMod.ManifestVersion,
                        LockedGeneratedUtc = lockedMod.GeneratedUtc,
                        CurrentManifestVersion = currentMod.ManifestVersion,
                        CurrentGeneratedUtc = currentMod.GeneratedUtc,
                    });
                }
            }
        }

        private static Dictionary<string, GameAssetLibraryLockMod> BuildCurrentModIndex()
        {
            var mods = new Dictionary<string, GameAssetLibraryLockMod>(StringComparer.Ordinal);
            foreach (var mod in GameAssetLibraryManifest.CollectLoadedModManifests())
            {
                if (mod == null)
                    continue;

                mods[GameAssetIdentityKey.NormalizeMod(mod.Mod)] = new GameAssetLibraryLockMod(mod.Mod, mod.ManifestVersion, mod.GeneratedUtc);
            }

            return mods;
        }

        private static bool IsLatestVersionRequest(GameAssetKey key)
        {
            return string.IsNullOrWhiteSpace(key.Version);
        }

        private static GameAssetKey BuildIdentityRequest(GameAssetKey key)
        {
            return new GameAssetKey(key.Mod, key.Type, key.Key, string.Empty);
        }

        private static bool HasGuidKeyMismatch(
            GameAssetLibraryLockEntry entry,
            bool hasCurrentGuid,
            GameAssetScriptableObject currentGuidAsset,
            bool hasCurrentResolvedKey,
            GameAssetScriptableObject currentResolvedKeyAsset)
        {
            if (!hasCurrentGuid && !hasCurrentResolvedKey)
                return false;

            if (hasCurrentGuid && currentGuidAsset != null && !KeysEqual(currentGuidAsset.Key, entry.ResolvedKey))
                return true;

            if (hasCurrentResolvedKey && currentResolvedKeyAsset != null && entry.ResolvedGuid.isValid && currentResolvedKeyAsset.GUID != entry.ResolvedGuid)
                return true;

            if (hasCurrentGuid && hasCurrentResolvedKey && currentGuidAsset != null && currentResolvedKeyAsset != null && currentGuidAsset.GUID != currentResolvedKeyAsset.GUID)
                return true;

            return false;
        }

        private static bool KeysEqual(GameAssetKey left, GameAssetKey right)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(left.Mod, right.Mod)
                   && StringComparer.OrdinalIgnoreCase.Equals(left.Type, right.Type)
                   && StringComparer.OrdinalIgnoreCase.Equals(left.Key, right.Key)
                   && StringComparer.OrdinalIgnoreCase.Equals(left.Version, right.Version);
        }
    }
}
