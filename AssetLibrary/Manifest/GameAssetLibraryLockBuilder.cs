using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
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
            return Build(templateCache, mountedModules, null);
        }

        public static GameAssetLibraryLock Build(GameAssetTemplateCache templateCache, IReadOnlyList<ModPackage> mountedModules, PrefabGameAssetDefinitions prefabDefinitions)
        {
            if (templateCache == null)
                throw new ArgumentNullException(nameof(templateCache));
            if (mountedModules == null)
                throw new ArgumentNullException(nameof(mountedModules));
            if (prefabDefinitions?.IsDisposed == true)
                throw new ObjectDisposedException(nameof(prefabDefinitions));
            if (prefabDefinitions?.Assets.Count > 0 && string.IsNullOrWhiteSpace(prefabDefinitions.NativeContentHash))
                throw new InvalidOperationException("Prefab GameAsset definitions require native content hashes. Reconcile the prefab index before building the session library.");

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
                if (!moduleIds.Add(GameAssetIdentityKey.NormalizeMod(module.ModuleId)))
                {
                    throw new InvalidOperationException(
                        $"Mounted GameAsset module '{module.ModuleId}' is duplicated.");
                }
                if (prefabDefinitions != null && string.Equals(GameAssetIdentityKey.NormalizeMod(module.ModuleId), PrefabGameAssetDefinitions.MOD, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Mounted GameAsset module '{module.ModuleId}' conflicts with the reserved prefab definition module '{PrefabGameAssetDefinitions.MOD}'.");
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

                    authoredAssets.Add(gameAsset);
                }
            }

            if (prefabDefinitions != null)
                authoredAssets.AddRange(prefabDefinitions.Assets);

            var exactKeys = new HashSet<string>(StringComparer.Ordinal);
            var guids = new Dictionary<UnityEngine.Hash128, GameAssetKey>();
            foreach (var gameAsset in authoredAssets)
            {
                if (!exactKeys.Add(GameAssetRequestKey.Normalize(gameAsset.Key)))
                    throw new InvalidOperationException($"GameAsset '{gameAsset.Key}' is defined more than once in the session library.");
                if (guids.TryGetValue(gameAsset.GUID, out var existingKey))
                    throw new InvalidOperationException($"GameAsset GUID '{gameAsset.GUID}' is shared by '{existingKey}' and '{gameAsset.Key}'.");
                guids.Add(gameAsset.GUID, gameAsset.Key);
            }

            foreach (var gameAsset in authoredAssets)
            {
                AddResolvedEntry(assetLock, templateCache, gameAsset.Key, gameAsset);
                var identity = GameAssetIdentityKey.Normalize(gameAsset.Key);
                if (!latestByIdentity.TryGetValue(identity, out var currentLatest) || GameAssetVersionUtils.Compare(gameAsset.Key.Version, currentLatest.Key.Version) > 0)
                    latestByIdentity[identity] = gameAsset;
            }

            foreach (var gameAsset in latestByIdentity.Values)
            {
                AddResolvedEntry(
                    assetLock,
                    templateCache,
                    BuildIdentityRequest(gameAsset.Key),
                    gameAsset);
            }

            if (prefabDefinitions != null)
                authoredAssets.AddRange(prefabDefinitions.RegisterOverrides(assetLock, templateCache));

            if (prefabDefinitions?.Assets.Count > 0)
                assetLock.SetMod(new GameAssetLibraryLockMod(PrefabGameAssetDefinitions.MOD, 1, string.Empty, CalculateNativeModuleHash(templateCache, prefabDefinitions)));

            GameAssetDerivedAssetRegistrar.RegisterAuthoredOverrides(
                assetLock,
                templateCache,
                authoredAssets);

            return assetLock.Seal();
        }

        private static string CalculateNativeModuleHash(GameAssetTemplateCache templateCache, PrefabGameAssetDefinitions prefabDefinitions)
        {
            using var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(1);
            writer.WriteString(prefabDefinitions.NativeContentHash);
            writer.WriteInt32(prefabDefinitions.Assets.Count);
            foreach (var asset in prefabDefinitions.Assets.OrderBy(asset => GameAssetRequestKey.Normalize(asset.Key), StringComparer.Ordinal))
            {
                var blueprint = templateCache.GetOrCreate(new GameAssetReference(asset.Key), asset);
                writer.WriteString(asset.Key.Mod);
                writer.WriteString(asset.Key.Type);
                writer.WriteString(asset.Key.Key);
                writer.WriteString(asset.Key.Version);
                writer.WriteHash128(asset.GUID);
                writer.WriteString(blueprint.Asset.MaterializedContentHash);
            }
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(writer.ToArray())).Replace("-", string.Empty).ToLowerInvariant();
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

        // Both catalogs describe prepared content. Comparison never consults the global package manifest or live authoring.
        public static GameAssetLibraryLockReport CompareWithCurrentLibrary(GameAssetLibraryLock assetLock, GameAssetLibraryLock currentLibrary)
        {
            if (currentLibrary == null)
                throw new ArgumentNullException(nameof(currentLibrary));
            if (!currentLibrary.IsReadOnly)
                throw new InvalidOperationException("The current GameAsset library must be a sealed preparation snapshot.");
            var report = new GameAssetLibraryLockReport();
            if (assetLock == null)
                return report;

            CompareMods(assetLock, currentLibrary, report);

            if (assetLock.Entries == null || assetLock.Entries.Count == 0)
                return report;

            var currentByGuid = currentLibrary.Entries.Values.GroupBy(entry => entry.ResolvedGuid).ToDictionary(group => group.Key, group => group.First());
            foreach (var kv in assetLock.Entries.GroupBy(pair => GameAssetRequestKey.Normalize(pair.Value.ResolvedKey)).Select(group => group.First()))
            {
                var identity = kv.Key;
                var entry = kv.Value;

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

                var hasCurrentResolvedKey = currentLibrary.TryGet(entry.ResolvedKey, out var currentResolvedKeyAsset);
                var hasCurrentIdentity = currentLibrary.TryGet(request, out var currentIdentityAsset);
                // Derived definitions are exact catalog entries and intentionally have no latest alias.
                if (!hasCurrentIdentity && hasCurrentResolvedKey)
                {
                    currentIdentityAsset = currentResolvedKeyAsset;
                    hasCurrentIdentity = true;
                }
                GameAssetLibraryLockEntry currentGuidAsset = null;
                var hasCurrentGuid = entry.ResolvedGuid.isValid && currentByGuid.TryGetValue(entry.ResolvedGuid, out currentGuidAsset);

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
                        CurrentResolvedKey = hasCurrentIdentity ? currentIdentityAsset.ResolvedKey : default,
                        CurrentResolvedGuid = hasCurrentIdentity ? currentIdentityAsset.ResolvedGuid : default,
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
                        CurrentResolvedKey = hasCurrentIdentity ? currentIdentityAsset.ResolvedKey : default,
                        CurrentResolvedGuid = hasCurrentIdentity ? currentIdentityAsset.ResolvedGuid : default,
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
                        CurrentResolvedKey = currentAsset != null ? currentAsset.ResolvedKey : default,
                        CurrentResolvedGuid = currentAsset != null ? currentAsset.ResolvedGuid : default,
                    });
                }
                if (hasCurrentResolvedKey && !string.Equals(entry.MaterializedContentHash, currentResolvedKeyAsset.MaterializedContentHash, StringComparison.Ordinal))
                {
                    report.Issues.Add(new GameAssetLibraryLockIssue
                    {
                        Kind = GameAssetLibraryLockIssueKinds.CONTENT_HASH_MISMATCH,
                        Identity = identity,
                        LockedResolvedKey = entry.ResolvedKey,
                        LockedResolvedGuid = entry.ResolvedGuid,
                        CurrentResolvedKey = currentResolvedKeyAsset.ResolvedKey,
                        CurrentResolvedGuid = currentResolvedKeyAsset.ResolvedGuid,
                        LockedContentHash = entry.MaterializedContentHash,
                        CurrentContentHash = currentResolvedKeyAsset.MaterializedContentHash,
                    });
                }
            }

            return report;
        }

        private static void CompareMods(GameAssetLibraryLock assetLock, GameAssetLibraryLock currentLibrary, GameAssetLibraryLockReport report)
        {
            if (assetLock.Mods == null || assetLock.Mods.Count == 0)
                return;

            var currentMods = currentLibrary.Mods;
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
                        LockedContentHash = lockedMod.ContentHash,
                        CurrentContentHash = currentMod.ContentHash,
                    });
                }
            }
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
            GameAssetLibraryLockEntry currentGuidAsset,
            bool hasCurrentResolvedKey,
            GameAssetLibraryLockEntry currentResolvedKeyAsset)
        {
            if (!hasCurrentGuid && !hasCurrentResolvedKey)
                return false;

            if (hasCurrentGuid && currentGuidAsset != null && !KeysEqual(currentGuidAsset.ResolvedKey, entry.ResolvedKey))
                return true;

            if (hasCurrentResolvedKey && currentResolvedKeyAsset != null && entry.ResolvedGuid.isValid && currentResolvedKeyAsset.ResolvedGuid != entry.ResolvedGuid)
                return true;

            if (hasCurrentGuid && hasCurrentResolvedKey && currentGuidAsset != null && currentResolvedKeyAsset != null && currentGuidAsset.ResolvedGuid != currentResolvedKeyAsset.ResolvedGuid)
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
