using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using DingoUnityExtensions.MonoBehaviours.Singletons;
using NaughtyAttributes;
using Newtonsoft.Json;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS
{
#if UNITY_EDITOR
    public static class GameAssetManifestPlayHook
    {
        [InitializeOnLoadMethod]
        private static void Install()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                    GameAssetLibraryManifest.GetNoCheck()?.RebuildCacheInEditor();
            };
        }
    }
#endif

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
    
    // TODO Improve
    [CreateAssetMenu(menuName = MENU_PREFIX + nameof(GameAssetLibraryManifest), fileName = S_PREFIX + nameof(GameAssetLibraryManifest), order = 0)]
    public class GameAssetLibraryManifest : ProtectedSingletonScriptableObject<GameAssetLibraryManifest>
    {
        private const string MENU_PREFIX = "Game Assets/";
        private const string MANIFEST_FILE_NAME = "manifest.json";

        [SerializeField] private List<GameAssetFolderPath> _assetFolderPaths = new();
        [SerializeField] private List<GameAsset> _cachedAssets = new();
        [SerializeField] private bool _externalOverridesBuiltIn = true;

        public bool ExternalOverridesBuiltIn => _externalOverridesBuiltIn;

        [NonSerialized] private bool _runtimeCacheBuilt;

        [NonSerialized] private readonly Dictionary<Hash128, GameAsset> _builtInByGuid = new();
        [NonSerialized] private readonly Dictionary<GameAssetKey, GameAsset> _builtInByKey = new(new GameAssetKeyComparer());

        [NonSerialized] private readonly List<ModPackage> _packages = new();

        [NonSerialized] private readonly Dictionary<GameAssetKey, ExternalLocator> _externalByKey = new(new GameAssetKeyComparer());
        [NonSerialized] private readonly Dictionary<Hash128, ExternalLocator> _externalByGuid = new();

        [NonSerialized] private readonly object _cacheLock = new();
        [NonSerialized] private readonly SemaphoreSlim _buildGate = new(1, 1);
        [NonSerialized] private Task _inFlightBuild;

        public static void AddFolder(GameAssetFolderPath gameAssetFolderPath)
        {
            var s = GetNoCheck();
            if (s == null)
                return;
            s._assetFolderPaths.Add(gameAssetFolderPath);
            s.MarkRuntimeCacheDirty();
        }

        public static void RemoveFolder(GameAssetFolderPath gameAssetFolderPath)
        {
            var s = GetNoCheck();
            if (s == null)
                return;
            s._assetFolderPaths.Remove(gameAssetFolderPath);
            s.MarkRuntimeCacheDirty();
        }
        
        public static Task EnsureInitializedAsync(CancellationToken ct = default)
        {
            var s = GetNoCheck();
            if (s == null)
                return Task.CompletedTask;
            return s.EnsureRuntimeCacheAsync(ct);
        }
        
        public static void ClearRuntimeCaches(bool clearExternalPackages = true, bool unloadUnusedAssets = false)
        {
            var s = GetNoCheck();
            if (s == null)
                return;
            s.ClearRuntimeCache(clearExternalPackages);

            if (unloadUnusedAssets)
                _ = Resources.UnloadUnusedAssets();
        }

        public static bool TryResolve(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            asset = null;
            var s = GetNoCheck();
            if (s == null)
                return false;

            s.EnsureRuntimeCacheSync();

            if (s._externalOverridesBuiltIn)
                return s.TryResolveExternal(key, out asset) || s.TryResolveBuiltIn(key, out asset);

            return s.TryResolveBuiltIn(key, out asset) || s.TryResolveExternal(key, out asset);
        }

        public static bool TryResolveGuid(Hash128 guid, out GameAssetScriptableObject asset)
        {
            asset = null;
            var s = GetNoCheck();
            if (s == null)
                return false;

            s.EnsureRuntimeCacheSync();

            if (s._externalOverridesBuiltIn)
                return s.TryResolveExternal(guid, out asset) || s.TryResolveBuiltInGuid(guid, out asset);

            return s.TryResolveBuiltInGuid(guid, out asset) || s.TryResolveExternal(guid, out asset);
        }

        public static Dictionary<Hash128, GameAssetScriptableObject> CollectAllAssets(bool includeExternal)
        {
            var s = GetNoCheck();
            var dict = new Dictionary<Hash128, GameAssetScriptableObject>();
            if (s == null)
                return dict;

            s.EnsureRuntimeCacheSync();

            foreach (var e in s._cachedAssets)
            {
                if (e == null)
                    continue;
                dict[e.GUID] = e;
            }

            if (!includeExternal)
                return dict;

            foreach (var kv in s._externalByKey)
            {
                if (s.TryResolveExternal(kv.Key, out var a) && a != null)
                    dict[a.GUID] = a;
            }

            return dict;
        }

        private void MarkRuntimeCacheDirty()
        {
            lock (_cacheLock)
            {
                _runtimeCacheBuilt = false;
            }
        }

        private void EnsureRuntimeCacheSync()
        {
            Task inFlight;
            lock (_cacheLock)
            {
                if (_runtimeCacheBuilt)
                    return;
                inFlight = _inFlightBuild;
            }

            if (inFlight != null && !inFlight.IsCompleted)
            {
                inFlight.GetAwaiter().GetResult();
                return;
            }

            RebuildRuntimeCache();
        }

        private Task EnsureRuntimeCacheAsync(CancellationToken ct)
        {
            lock (_cacheLock)
            {
                if (_runtimeCacheBuilt)
                    return Task.CompletedTask;

                if (_inFlightBuild != null && !_inFlightBuild.IsCompleted)
                    return _inFlightBuild;

                _inFlightBuild = RebuildRuntimeCacheAsync(ct);
                return _inFlightBuild;
            }
        }

        [Button]
        public void RebuildRuntimeCache()
        {
            _buildGate.Wait();
            try
            {
                var builtInByGuid = new Dictionary<Hash128, GameAsset>();
                var builtInByKey = new Dictionary<GameAssetKey, GameAsset>(new GameAssetKeyComparer());

                BuildBuiltInCaches(builtInByGuid, builtInByKey);

                var mounts = BuildExternalMountsSnapshot();
                var packages = new List<ModPackage>();
                var externalByKey = new Dictionary<GameAssetKey, ExternalLocator>(new GameAssetKeyComparer());
                var externalByGuid = new Dictionary<Hash128, ExternalLocator>();

                foreach (var m in mounts)
                {
                    TryMountModRootSync(m, packages, externalByKey, externalByGuid);
                }

                SwapCaches(builtInByGuid, builtInByKey, packages, externalByKey, externalByGuid);
            }
            finally
            {
                _buildGate.Release();
            }
        }

        public async Task RebuildRuntimeCacheAsync(CancellationToken ct = default)
        {
            await _buildGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var builtInByGuid = new Dictionary<Hash128, GameAsset>();
                var builtInByKey = new Dictionary<GameAssetKey, GameAsset>(new GameAssetKeyComparer());

                BuildBuiltInCaches(builtInByGuid, builtInByKey);

                var mounts = BuildExternalMountsSnapshot();

                var packages = new List<ModPackage>();
                var externalByKey = new Dictionary<GameAssetKey, ExternalLocator>(new GameAssetKeyComparer());
                var externalByGuid = new Dictionary<Hash128, ExternalLocator>();

                foreach (var m in mounts)
                {
                    ct.ThrowIfCancellationRequested();
                    await TryMountModRootAsync(m, packages, externalByKey, externalByGuid, ct).ConfigureAwait(false);
                }

                SwapCaches(builtInByGuid, builtInByKey, packages, externalByKey, externalByGuid);
            }
            finally
            {
                _buildGate.Release();
            }
        }

        public void ClearRuntimeCache(bool clearExternalPackages = true)
        {
            lock (_cacheLock)
            {
                _externalByKey.Clear();
                _externalByGuid.Clear();

                if (clearExternalPackages)
                    _packages.Clear();

                _builtInByGuid.Clear();
                _builtInByKey.Clear();

                _runtimeCacheBuilt = false;
                _inFlightBuild = null;
            }
        }

        private void BuildBuiltInCaches(Dictionary<Hash128, GameAsset> builtInByGuid, Dictionary<GameAssetKey, GameAsset> builtInByKey)
        {
            foreach (var a in _cachedAssets)
            {
                if (a == null)
                    continue;

                if (a.GUID.isValid)
                    builtInByGuid[a.GUID] = a;

                builtInByKey[a.Key] = a;
            }
        }

        private List<MountInfo> BuildExternalMountsSnapshot()
        {
            var externalFolders = _assetFolderPaths.Select((f, idx) => (f, idx)).Where(x => x.f != null && x.f.Enabled && x.f.FolderType != GameAssetFolderType.BuildIn).OrderByDescending(x => x.f.Priority).ThenBy(x => x.idx).Select(x => x.f).ToList();

            var mounts = new List<MountInfo>(externalFolders.Count);

            var order = 0;
            foreach (var f in externalFolders)
            {
                order++;

                var abs = ResolveExternalAbsPath(f.SubPath);
                if (string.IsNullOrEmpty(abs))
                    continue;

                mounts.Add(new MountInfo(abs, f.Priority, order));
            }

            return mounts;
        }

        private void SwapCaches(Dictionary<Hash128, GameAsset> builtInByGuid, Dictionary<GameAssetKey, GameAsset> builtInByKey, List<ModPackage> packages, Dictionary<GameAssetKey, ExternalLocator> externalByKey, Dictionary<Hash128, ExternalLocator> externalByGuid)
        {
            lock (_cacheLock)
            {
                _builtInByGuid.Clear();
                _builtInByKey.Clear();
                _packages.Clear();
                _externalByKey.Clear();
                _externalByGuid.Clear();

                foreach (var kv in builtInByGuid)
                {
                    _builtInByGuid[kv.Key] = kv.Value;
                }

                foreach (var kv in builtInByKey)
                {
                    _builtInByKey[kv.Key] = kv.Value;
                }

                _packages.AddRange(packages);

                foreach (var kv in externalByKey)
                {
                    _externalByKey[kv.Key] = kv.Value;
                }

                foreach (var kv in externalByGuid)
                {
                    _externalByGuid[kv.Key] = kv.Value;
                }

                _runtimeCacheBuilt = true;
                _inFlightBuild = null;
            }
        }

        private void TryMountModRootSync(MountInfo mount, List<ModPackage> packages, Dictionary<GameAssetKey, ExternalLocator> externalByKey, Dictionary<Hash128, ExternalLocator> externalByGuid)
        {
            try
            {
                var manifestPath = Path.Combine(mount.ModRootAbs, MANIFEST_FILE_NAME);
                if (!File.Exists(manifestPath))
                {
                    Debug.LogError($"No {MANIFEST_FILE_NAME} in '{mount.ModRootAbs}', skip.");
                    return;
                }

                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonConvert.DeserializeObject<ModManifest>(manifestJson, GameRuntimeComponentJson.Settings);
                if (manifest == null)
                    return;

                var pkg = new ModPackage(mount.ModRootAbs, manifest);
                packages.Add(pkg);

                foreach (var e in manifest.Assets)
                {
                    var locator = new ExternalLocator(pkg, e.Key, mount.Priority, mount.Order);

                    UpsertExternalKey(externalByKey, locator);

                    if (e.GUID.isValid)
                        UpsertExternalGuid(externalByGuid, e.GUID, locator);
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private async Task TryMountModRootAsync(MountInfo mount, List<ModPackage> packages, Dictionary<GameAssetKey, ExternalLocator> externalByKey, Dictionary<Hash128, ExternalLocator> externalByGuid, CancellationToken ct)
        {
            try
            {
                var manifestPath = Path.Combine(mount.ModRootAbs, MANIFEST_FILE_NAME);
                if (!File.Exists(manifestPath))
                {
                    Debug.LogError($"No {MANIFEST_FILE_NAME} in '{mount.ModRootAbs}', skip.");
                    return;
                }

                var manifestJson = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);

                var manifest = JsonConvert.DeserializeObject<ModManifest>(manifestJson, GameRuntimeComponentJson.Settings);
                if (manifest == null)
                    return;

                var pkg = new ModPackage(mount.ModRootAbs, manifest);
                packages.Add(pkg);

                foreach (var e in manifest.Assets)
                {
                    var locator = new ExternalLocator(pkg, e.Key, mount.Priority, mount.Order);

                    UpsertExternalKey(externalByKey, locator);

                    if (e.GUID.isValid)
                        UpsertExternalGuid(externalByGuid, e.GUID, locator);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
        }

        private static void UpsertExternalKey(Dictionary<GameAssetKey, ExternalLocator> dict, ExternalLocator loc)
        {
            if (dict.TryGetValue(loc.Key, out var existing))
            {
                if (IsBetter(loc, existing))
                    dict[loc.Key] = loc;
                return;
            }

            dict.Add(loc.Key, loc);
        }

        private static void UpsertExternalGuid(Dictionary<Hash128, ExternalLocator> dict, Hash128 guid, ExternalLocator loc)
        {
            if (dict.TryGetValue(guid, out var existing))
            {
                if (IsBetter(loc, existing))
                    dict[guid] = loc;
                return;
            }

            dict.Add(guid, loc);
        }

        private static bool IsBetter(ExternalLocator a, ExternalLocator b)
        {
            if (a.Priority != b.Priority)
                return a.Priority > b.Priority;

            return a.Order > b.Order;
        }

        private bool TryResolveBuiltIn(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            asset = null;
            if (_builtInByKey.TryGetValue(key, out var ga) && ga != null)
            {
                asset = ga;
                return true;
            }

            return false;
        }

        private bool TryResolveBuiltInGuid(Hash128 guid, out GameAssetScriptableObject asset)
        {
            asset = null;
            if (_builtInByGuid.TryGetValue(guid, out var ga) && ga != null)
            {
                asset = ga;
                return true;
            }

            return false;
        }

        private bool TryResolveExternal(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            asset = null;

            if (!_externalByKey.TryGetValue(key, out var loc))
                return false;

            if (loc.Package.TryGet(key, out var so) && so != null)
            {
                asset = so;
                return true;
            }

            return false;
        }

        private bool TryResolveExternal(Hash128 guid, out GameAssetScriptableObject asset)
        {
            asset = null;

            if (!_externalByGuid.TryGetValue(guid, out var loc))
                return false;

            if (loc.Package.TryGet(loc.Key, out var so) && so != null)
            {
                asset = so;
                return true;
            }

            return false;
        }

        private static string ResolveExternalAbsPath(string subPath)
        {
            if (string.IsNullOrWhiteSpace(subPath))
                return string.Empty;

            var p = subPath.Replace('\\', '/').Trim();

            if (Path.IsPathRooted(p))
                return Path.GetFullPath(p);

            return Path.GetFullPath(Path.Combine(Application.persistentDataPath, p));
        }

#if UNITY_EDITOR
        [Button]
        public void RebuildCacheInEditor()
        {
            _cachedAssets.Clear();

            foreach (var folder in _assetFolderPaths)
            {
                if (folder == null || !folder.Enabled)
                    continue;

                if (folder.FolderType != GameAssetFolderType.BuildIn)
                    continue;

                var root = ResolveProjectFolder(folder);
                if (!AssetDatabase.IsValidFolder(root))
                    continue;

                var guids = AssetDatabase.FindAssets($"t:{nameof(GameAsset)}", new[] { root });
                foreach (var guidStr in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guidStr);
                    var asset = AssetDatabase.LoadAssetAtPath<GameAsset>(path);
                    if (asset == null)
                        continue;
                    _cachedAssets.Add(asset);
                }
            }

            _cachedAssets = _cachedAssets.OrderBy(x => x.GUID.ToString()).ToList();

            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();

            MarkRuntimeCacheDirty();
        }

        private static string ResolveProjectFolder(GameAssetFolderPath folder)
        {
            var sub = (folder.SubPath ?? string.Empty).Replace('\\', '/').Trim('/');

            if (string.IsNullOrEmpty(sub))
                return "Assets";

            if (sub.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || sub.Equals("Assets", StringComparison.OrdinalIgnoreCase) || sub.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) || sub.Equals("Packages", StringComparison.OrdinalIgnoreCase))
            {
                return sub;
            }

            return "Assets/" + sub;
        }
#endif

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

        private readonly struct MountInfo
        {
            public readonly string ModRootAbs;
            public readonly int Priority;
            public readonly int Order;

            public MountInfo(string modRootAbs, int priority, int order)
            {
                ModRootAbs = modRootAbs;
                Priority = priority;
                Order = order;
            }
        }

        private sealed class GameAssetKeyComparer : IEqualityComparer<GameAssetKey>
        {
            private static readonly StringComparer C = StringComparer.OrdinalIgnoreCase;

            public bool Equals(GameAssetKey a, GameAssetKey b) =>
                C.Equals(a.Mod, b.Mod) && C.Equals(a.Type, b.Type) && C.Equals(a.Key, b.Key) && C.Equals(a.Version, b.Version);

            public int GetHashCode(GameAssetKey k)
            {
                var hc = new HashCode();
                hc.Add(k.Mod, C);
                hc.Add(k.Type, C);
                hc.Add(k.Key, C);
                hc.Add(k.Version, C);
                return hc.ToHashCode();
            }
        }
    }
}