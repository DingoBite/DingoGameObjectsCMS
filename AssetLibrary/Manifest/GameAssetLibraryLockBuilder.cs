using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;

namespace DingoGameObjectsCMS.AssetLibrary
{
    public static class GameAssetLibraryLockBuilder
    {
        public static GameAssetLibraryLock Build(
            GameAssetTemplateCache templateCache,
            IReadOnlyList<ModPackage> mountedModules)
        {
            if (templateCache == null)
                throw new ArgumentNullException(nameof(templateCache));
            if (mountedModules == null)
                throw new ArgumentNullException(nameof(mountedModules));

            var assetLock = new GameAssetLibraryLock();
            var latestByIdentity = new Dictionary<string, GameAsset>(
                StringComparer.Ordinal);
            var authoredAssets = new List<GameAsset>();
            var moduleIds = new HashSet<string>(StringComparer.Ordinal);
            var orderedModules = mountedModules
                .OrderBy(module => module?.ModuleId, StringComparer.Ordinal)
                .ToArray();
            for (var moduleIndex = 0;
                 moduleIndex < orderedModules.Length;
                 moduleIndex++)
            {
                var module = orderedModules[moduleIndex]
                             ?? throw new InvalidOperationException(
                                 $"Mounted GameAsset module {moduleIndex} is null.");
                if (!moduleIds.Add(module.ModuleId))
                {
                    throw new InvalidOperationException(
                        $"Mounted GameAsset module '{module.ModuleId}' is duplicated.");
                }
                assetLock.SetMod(new GameAssetLibraryLockMod(
                    module.ModuleId,
                    module.ManifestVersion,
                    module.GeneratedUtc,
                    module.ContentHash));

                for (var assetIndex = 0;
                     assetIndex < module.Assets.Count;
                     assetIndex++)
                {
                    var authored = module.Assets[assetIndex];
                    if (!module.TryGet(authored.Key, out var asset)
                        || asset is not GameAsset gameAsset)
                    {
                        throw new InvalidOperationException(
                            $"Mounted asset '{authored.Key}' is not a loadable {nameof(GameAsset)}.");
                    }

                    AddResolvedEntry(
                        assetLock,
                        templateCache,
                        gameAsset.Key,
                        gameAsset);
                    authoredAssets.Add(gameAsset);
                    var identity = GameAssetIdentityKey.Normalize(
                        gameAsset.Key);
                    if (!latestByIdentity.TryGetValue(
                            identity,
                            out var currentLatest)
                        || GameAssetVersionUtils.Compare(
                            gameAsset.Key.Version,
                            currentLatest.Key.Version) > 0)
                    {
                        latestByIdentity[identity] = gameAsset;
                    }
                }
            }

            foreach (var gameAsset in latestByIdentity.Values)
            {
                AddResolvedEntry(
                    assetLock,
                    templateCache,
                    BuildIdentityRequest(gameAsset.Key),
                    gameAsset);
            }

            GameAssetDerivedAssetRegistrar.RegisterAuthoredOverrides(
                assetLock,
                templateCache,
                authoredAssets);

            return assetLock.Seal();
        }

        private static void AddResolvedEntry(
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            GameAssetKey request,
            GameAsset gameAsset)
        {
            var blueprint = templateCache.GetOrCreate(new GameAssetReference(request), gameAsset);
            assetLock.Set(request, new GameAssetLibraryLockEntry(
                gameAsset.Key,
                gameAsset.GUID,
                blueprint.Asset.MaterializedContentHash,
                gameAsset));
        }

        public static bool TryResolve(GameAssetKey key, GameAssetLibraryLock assetLock, out GameAssetScriptableObject asset)
        {
            asset = null;

            if (assetLock == null)
                return false;

            if (assetLock.FormatVersion != GameAssetLibraryLock.CURRENT_FORMAT_VERSION)
                return false;

            if (!assetLock.TryGet(key, out var entry) || entry == null)
                return false;

            if (!entry.ResolvedGuid.isValid
                || string.IsNullOrWhiteSpace(entry.ResolvedKey.Version)
                || string.IsNullOrWhiteSpace(entry.MaterializedContentHash))
                return false;

            if (!IsLatestVersionRequest(key) && !KeysEqual(key, entry.ResolvedKey))
                return false;

            if (!entry.HasResolvedAsset)
                return false;

            if (ReferenceEquals(entry.ResolvedAsset, null)
                || entry.ResolvedAsset == null)
            {
                return false;
            }
            if (entry.ResolvedAsset.GUID != entry.ResolvedGuid
                || !KeysEqual(
                    entry.ResolvedAsset.Key,
                    entry.ResolvedKey))
            {
                return false;
            }

            asset = entry.ResolvedAsset;
            return true;
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

                if (GameAssetDerivedIdentity.IsDerived(entry.ResolvedKey))
                    continue;

                var request = BuildIdentityRequest(entry.ResolvedKey);

                if (string.IsNullOrWhiteSpace(entry.MaterializedContentHash))
                {
                    report.Issues.Add(new GameAssetLibraryLockIssue
                    {
                        Kind = GameAssetLibraryLockIssueKinds.MISSING_CONTENT_HASH,
                        Identity = identity,
                        LockedResolvedKey = entry.ResolvedKey,
                        LockedResolvedGuid = entry.ResolvedGuid,
                    });
                }

                var hasCurrentIdentity = GameAssetLibraryManifest.TryResolveImmutable(request, out var currentIdentityAsset);
                GameAssetScriptableObject currentGuidAsset = null;
                var hasCurrentGuid = entry.ResolvedGuid.isValid && GameAssetLibraryManifest.TryResolveImmutableGuid(entry.ResolvedGuid, out currentGuidAsset);
                var hasCurrentResolvedKey = GameAssetLibraryManifest.TryResolveImmutable(entry.ResolvedKey, out var currentResolvedKeyAsset);

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

                if (currentMod.ManifestVersion != lockedMod.ManifestVersion
                    || !string.Equals(currentMod.GeneratedUtc, lockedMod.GeneratedUtc, StringComparison.Ordinal)
                    || !string.Equals(currentMod.ContentHash, lockedMod.ContentHash, StringComparison.Ordinal))
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
            foreach (var module in GameAssetLibraryManifest.CollectImmutableModPackages())
            {
                if (module == null)
                    continue;

                mods[GameAssetIdentityKey.NormalizeMod(module.ModuleId)] =
                    new GameAssetLibraryLockMod(
                        module.ModuleId,
                        module.ManifestVersion,
                        module.GeneratedUtc,
                        module.ContentHash);
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
