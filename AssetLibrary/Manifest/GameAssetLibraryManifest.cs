using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary
{
    public enum GameAssetFolderType
    {
        BuildIn = 0,
        ExternalModRoot = 1
    }

    [Serializable, Preserve]
    public class GameAssetFolderPath
    {
        public string Name;
        public GameAssetFolderType FolderType;
        public string SubPath;
        public int Priority = 0;
        public bool Enabled = true;
    }

    public sealed class GameAssetLibraryManifest
    {
        private const string BASE_MOD = "base";
        private const string MANIFEST_FILE_NAME = ModManifestStore.MANIFEST_FILE_NAME;

        private static readonly GameAssetLibraryManifest Instance = new();

        private readonly object _cacheLock = new();
        private readonly Dictionary<GameAssetKey, ExternalLocator> _byKey = new(new GameAssetKeyComparer());
        private readonly Dictionary<Hash128, ExternalLocator> _byGuid = new();
        private readonly List<ModPackage> _packages = new();
        private readonly Dictionary<GameAssetKey, ExternalLocator> _immutableByKey = new(new GameAssetKeyComparer());
        private readonly Dictionary<Hash128, ExternalLocator> _immutableByGuid = new();
        private readonly List<ModPackage> _immutablePackages = new();

        private bool _runtimeCacheBuilt;

        public bool ExternalOverridesBuiltIn => true;

        public static GameAssetLibraryManifest GetNoCheck()
        {
            return Instance;
        }

        public static void AddFolder(GameAssetFolderPath gameAssetFolderPath)
        {
            ClearRuntimeCaches();
        }

        public static void RemoveFolder(GameAssetFolderPath gameAssetFolderPath)
        {
            ClearRuntimeCaches();
        }

        public static void EnsureInitialized()
        {
            Instance.EnsureRuntimeCacheSync();
        }

        public static void ClearRuntimeCaches(bool clearExternalPackages = true, bool unloadUnusedAssets = false)
        {
            Instance.ClearRuntimeCache();

            if (unloadUnusedAssets)
                _ = Resources.UnloadUnusedAssets();
        }

        public static bool TryResolve(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            return Instance.TryResolveInternal(key, out asset);
        }

        public static bool TryResolveGuid(Hash128 guid, out GameAssetScriptableObject asset)
        {
            return Instance.TryResolveGuidInternal(guid, out asset);
        }

        public static bool TryResolveImmutable(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            return Instance.TryResolveImmutableInternal(key, out asset);
        }

        public static bool TryResolveImmutableGuid(Hash128 guid, out GameAssetScriptableObject asset)
        {
            return Instance.TryResolveImmutableGuidInternal(guid, out asset);
        }

        public static Dictionary<Hash128, GameAssetScriptableObject> CollectAllAssets(bool includeExternal)
        {
            return Instance.CollectAllAssetsInternal(includeExternal);
        }

        public static List<GameAssetKey> CollectIdentityRequests()
        {
            return Instance.CollectIdentityRequestsInternal();
        }

        public static List<ModManifest> CollectLoadedModManifests()
        {
            return Instance.CollectLoadedModManifestsInternal();
        }

        public static Dictionary<Hash128, GameAssetScriptableObject> CollectImmutableAssets()
        {
            return Instance.CollectAllAssetsInternal(includeExternal: false);
        }

        public static List<GameAssetKey> CollectImmutableIdentityRequests()
        {
            return Instance.CollectIdentityRequestsInternal(immutableOnly: true);
        }

        public static List<ModManifest> CollectImmutableModManifests()
        {
            return Instance.CollectLoadedModManifestsInternal(immutableOnly: true);
        }

        public void RebuildRuntimeCache()
        {
            RebuildRuntimeCacheInternal();
        }
        
        public void ClearRuntimeCache(bool clearExternalPackages = true)
        {
            ClearRuntimeCache();
        }

        private bool TryResolveInternal(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            asset = null;
            EnsureRuntimeCacheSync();

            if (!TryGetLocator(key, out var locator))
                return false;

            return locator.Package.TryGet(locator.Key, out asset);
        }

        private bool TryResolveGuidInternal(Hash128 guid, out GameAssetScriptableObject asset)
        {
            asset = null;
            EnsureRuntimeCacheSync();

            ExternalLocator locator;
            lock (_cacheLock)
            {
                if (!_byGuid.TryGetValue(guid, out locator))
                    return false;
            }

            return locator.Package.TryGet(locator.Key, out asset);
        }

        private bool TryResolveImmutableInternal(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            asset = null;
            EnsureRuntimeCacheSync();

            if (!TryGetLocator(key, _immutableByKey, out var locator))
                return false;

            return locator.Package.TryGet(locator.Key, out asset);
        }

        private bool TryResolveImmutableGuidInternal(Hash128 guid, out GameAssetScriptableObject asset)
        {
            asset = null;
            EnsureRuntimeCacheSync();

            ExternalLocator locator;
            lock (_cacheLock)
            {
                if (!_immutableByGuid.TryGetValue(guid, out locator))
                    return false;
            }

            return locator.Package.TryGet(locator.Key, out asset);
        }

        private Dictionary<Hash128, GameAssetScriptableObject> CollectAllAssetsInternal(bool includeExternal)
        {
            EnsureRuntimeCacheSync();

            List<ExternalLocator> locators;
            lock (_cacheLock)
            {
                locators = (includeExternal ? _byKey : _immutableByKey).Values.ToList();
            }

            var result = new Dictionary<Hash128, GameAssetScriptableObject>();
            for (var i = 0; i < locators.Count; i++)
            {
                if (locators[i].Package.TryGet(locators[i].Key, out var asset) && asset != null)
                    result[asset.GUID] = asset;
            }

            return result;
        }

        private List<GameAssetKey> CollectIdentityRequestsInternal(bool immutableOnly = false)
        {
            EnsureRuntimeCacheSync();

            var result = new List<GameAssetKey>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            lock (_cacheLock)
            {
                var keys = immutableOnly ? _immutableByKey.Keys : _byKey.Keys;
                foreach (var key in keys)
                {
                    if (seen.Add(GameAssetIdentityKey.Normalize(key)))
                        result.Add(new GameAssetKey(key.Mod, key.Type, key.Key, string.Empty));
                }
            }

            return result;
        }

        private List<ModManifest> CollectLoadedModManifestsInternal(bool immutableOnly = false)
        {
            EnsureRuntimeCacheSync();

            lock (_cacheLock)
            {
                return (immutableOnly ? _immutablePackages : _packages)
                    .Where(package => package?.Manifest != null)
                    .Select(package => package.Manifest)
                    .ToList();
            }
        }

        private void EnsureRuntimeCacheSync()
        {
            lock (_cacheLock)
            {
                if (_runtimeCacheBuilt)
                    return;
            }

            RebuildRuntimeCacheInternal();
        }

        private void RebuildRuntimeCacheInternal()
        {
            var packages = new List<ModPackage>();
            var byKey = new Dictionary<GameAssetKey, ExternalLocator>(new GameAssetKeyComparer());
            var byGuid = new Dictionary<Hash128, ExternalLocator>();
            var immutablePackages = new List<ModPackage>();
            var immutableByKey = new Dictionary<GameAssetKey, ExternalLocator>(new GameAssetKeyComparer());
            var immutableByGuid = new Dictionary<Hash128, ExternalLocator>();

            var mounts = BuildModRootSnapshot();
            for (var i = 0; i < mounts.Count; i++)
            {
                TryMountModRoot(
                    mounts[i],
                    packages,
                    byKey,
                    byGuid,
                    immutablePackages,
                    immutableByKey,
                    immutableByGuid);
            }

            lock (_cacheLock)
            {
                _packages.Clear();
                _packages.AddRange(packages);
                _byKey.Clear();
                foreach (var kv in byKey)
                    _byKey[kv.Key] = kv.Value;
                _byGuid.Clear();
                foreach (var kv in byGuid)
                    _byGuid[kv.Key] = kv.Value;
                _immutablePackages.Clear();
                _immutablePackages.AddRange(immutablePackages);
                _immutableByKey.Clear();
                foreach (var kv in immutableByKey)
                    _immutableByKey[kv.Key] = kv.Value;
                _immutableByGuid.Clear();
                foreach (var kv in immutableByGuid)
                    _immutableByGuid[kv.Key] = kv.Value;
                _runtimeCacheBuilt = true;
            }
        }

        private void ClearRuntimeCache()
        {
            lock (_cacheLock)
            {
                _packages.Clear();
                _byKey.Clear();
                _byGuid.Clear();
                _immutablePackages.Clear();
                _immutableByKey.Clear();
                _immutableByGuid.Clear();
                _runtimeCacheBuilt = false;
            }
        }

        private static List<MountInfo> BuildModRootSnapshot()
        {
            var result = new List<MountInfo>();
            var order = 0;

            // The built-in package is an immutable part of the player. Runtime
            // code never falls back to live Unity assets or an inline GRO.
            var builtInAssetsRoot = Path.Combine(
                Application.streamingAssetsPath,
                GameAssetModPathPolicy.DEFAULT_ASSETS_ROOT_SUB_PATH);
            var builtInBaseRoot = Path.Combine(builtInAssetsRoot, BASE_MOD);
            result.Add(new MountInfo(builtInBaseRoot, BASE_MOD, priority: 0, order: order++, isBuiltIn: true));

            if (Directory.Exists(builtInAssetsRoot))
            {
                var builtInDirectories = Directory.GetDirectories(builtInAssetsRoot)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                for (var i = 0; i < builtInDirectories.Length; i++)
                {
                    var mod = Path.GetFileName(builtInDirectories[i]);
                    if (string.Equals(mod, BASE_MOD, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(new MountInfo(builtInDirectories[i], mod, priority: 0, order: order++, isBuiltIn: true));
                }
            }

            var assetsRoot = GameAssetModPathPolicy.GetAssetsRootPath();
            Directory.CreateDirectory(assetsRoot);

            // A persisted base package is an explicit external override of the
            // immutable built-in package, not a missing-content fallback.
            var externalBaseRoot = Path.Combine(assetsRoot, BASE_MOD);
            result.Add(new MountInfo(externalBaseRoot, BASE_MOD, priority: 100, order: order++, isBuiltIn: false));

            var directories = Directory.GetDirectories(assetsRoot)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            for (var i = 0; i < directories.Length; i++)
            {
                var mod = Path.GetFileName(directories[i]);
                if (string.Equals(mod, BASE_MOD, StringComparison.OrdinalIgnoreCase))
                    continue;

                result.Add(new MountInfo(directories[i], mod, priority: 200, order, isBuiltIn: false));
                order++;
            }

            return result;
        }

        private static void TryMountModRoot(
            MountInfo mount,
            List<ModPackage> packages,
            Dictionary<GameAssetKey, ExternalLocator> byKey,
            Dictionary<Hash128, ExternalLocator> byGuid,
            List<ModPackage> immutablePackages,
            Dictionary<GameAssetKey, ExternalLocator> immutableByKey,
            Dictionary<Hash128, ExternalLocator> immutableByGuid)
        {
            try
            {
                var manifestPath = Path.Combine(mount.ModRootAbs, MANIFEST_FILE_NAME);
                if (!File.Exists(manifestPath))
                    return;

                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonConvert.DeserializeObject<ModManifest>(manifestJson, GameAssetJson.Settings);
                if (manifest == null)
                    return;
                if (!string.Equals(manifest.Mod, mount.Mod, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest '{manifestPath}' declares mod '{manifest.Mod}' but is mounted as '{mount.Mod}'.");
                }

                manifest.Assets ??= new List<ModManifestEntry>();
                var package = new ModPackage(mount.ModRootAbs, manifest);
                packages.Add(package);
                if (mount.IsBuiltIn)
                    immutablePackages.Add(package);

                for (var i = 0; i < manifest.Assets.Count; i++)
                {
                    var entry = manifest.Assets[i];
                    if (entry == null)
                        continue;

                    var locator = new ExternalLocator(package, entry.Key, mount.Priority, mount.Order);
                    UpsertKey(byKey, locator);

                    if (entry.GUID.isValid)
                        UpsertGuid(byGuid, entry.GUID, locator);

                    if (!mount.IsBuiltIn)
                        continue;

                    UpsertKey(immutableByKey, locator);
                    if (entry.GUID.isValid)
                        UpsertGuid(immutableByGuid, entry.GUID, locator);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private bool TryGetLocator(GameAssetKey key, out ExternalLocator locator)
        {
            return TryGetLocator(key, _byKey, out locator);
        }

        private bool TryGetLocator(
            GameAssetKey key,
            Dictionary<GameAssetKey, ExternalLocator> source,
            out ExternalLocator locator)
        {
            EnsureRuntimeCacheSync();

            lock (_cacheLock)
            {
                if (!IsLatestVersionRequest(key))
                    return source.TryGetValue(key, out locator);

                var found = false;
                var bestVersion = string.Empty;
                locator = default;
                foreach (var kv in source)
                {
                    if (!MatchesIdentity(kv.Key, key))
                        continue;

                    if (!found || CompareVersions(kv.Key.Version, bestVersion) > 0)
                    {
                        found = true;
                        bestVersion = kv.Key.Version;
                        locator = kv.Value;
                    }
                }

                return found;
            }
        }

        private static void UpsertKey(Dictionary<GameAssetKey, ExternalLocator> dict, ExternalLocator locator)
        {
            if (dict.TryGetValue(locator.Key, out var existing))
            {
                if (IsBetter(locator, existing))
                    dict[locator.Key] = locator;
                return;
            }

            dict.Add(locator.Key, locator);
        }

        private static void UpsertGuid(Dictionary<Hash128, ExternalLocator> dict, Hash128 guid, ExternalLocator locator)
        {
            if (dict.TryGetValue(guid, out var existing))
            {
                if (IsBetter(locator, existing))
                    dict[guid] = locator;
                return;
            }

            dict.Add(guid, locator);
        }

        private static bool IsBetter(ExternalLocator left, ExternalLocator right)
        {
            if (left.Priority != right.Priority)
                return left.Priority > right.Priority;

            return left.Order > right.Order;
        }

        private static bool IsLatestVersionRequest(GameAssetKey key)
        {
            return string.IsNullOrWhiteSpace(key.Version);
        }

        private static bool MatchesIdentity(GameAssetKey candidate, GameAssetKey requested)
        {
            return StringComparer.OrdinalIgnoreCase.Equals(candidate.Mod, requested.Mod)
                   && StringComparer.OrdinalIgnoreCase.Equals(candidate.Type, requested.Type)
                   && StringComparer.OrdinalIgnoreCase.Equals(candidate.Key, requested.Key);
        }

        private static int CompareVersions(string left, string right)
        {
            var leftParsed = Version.TryParse(string.IsNullOrWhiteSpace(left) ? string.Empty : left.Trim(), out var leftVersion);
            var rightParsed = Version.TryParse(string.IsNullOrWhiteSpace(right) ? string.Empty : right.Trim(), out var rightVersion);

            if (leftParsed && rightParsed)
                return leftVersion.CompareTo(rightVersion);
            if (leftParsed)
                return 1;
            if (rightParsed)
                return -1;

            return StringComparer.OrdinalIgnoreCase.Compare(left ?? string.Empty, right ?? string.Empty);
        }

        private readonly struct MountInfo
        {
            public readonly string ModRootAbs;
            public readonly string Mod;
            public readonly int Priority;
            public readonly int Order;
            public readonly bool IsBuiltIn;

            public MountInfo(string modRootAbs, string mod, int priority, int order, bool isBuiltIn)
            {
                ModRootAbs = modRootAbs;
                Mod = mod;
                Priority = priority;
                Order = order;
                IsBuiltIn = isBuiltIn;
            }
        }

        private readonly struct ExternalLocator
        {
            public readonly ModPackage Package;
            public readonly GameAssetKey Key;
            public readonly int Priority;
            public readonly int Order;

            public ExternalLocator(ModPackage package, GameAssetKey key, int priority, int order)
            {
                Package = package;
                Key = key;
                Priority = priority;
                Order = order;
            }
        }

        private sealed class GameAssetKeyComparer : IEqualityComparer<GameAssetKey>
        {
            private static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

            public bool Equals(GameAssetKey a, GameAssetKey b)
            {
                return C.Equals(a.Mod, b.Mod)
                       && C.Equals(a.Type, b.Type)
                       && C.Equals(a.Key, b.Key)
                       && C.Equals(a.Version, b.Version);
            }

            public int GetHashCode(GameAssetKey key)
            {
                var hash = new HashCode();
                hash.Add(key.Mod, C);
                hash.Add(key.Type, C);
                hash.Add(key.Key, C);
                hash.Add(key.Version, C);
                return hash.ToHashCode();
            }
        }
    }
}
