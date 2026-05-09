#if NEWTONSOFT_EXISTS
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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary
{
    public enum GameAssetSaveMode
    {
        Create,
        UpsertByKey,
        UpdateExisting
    }

    public sealed class GameAssetSaveRequest
    {
        public string ModRootAbs;
        public string Mod;
        public GameAsset Asset;
        public GameAssetSaveMode Mode = GameAssetSaveMode.UpsertByKey;
        public string RelativeJsonPath;
        public bool PreserveExistingGuidOnUpsert = true;
        public bool RefreshLibrary = true;
        public bool ResolveSavedAsset = true;
    }

    public sealed class GameAssetDeleteRequest
    {
        public string ModRootAbs;
        public string Mod;
        public GameAssetKey Key;
        public bool RefreshLibrary = true;
    }

    public sealed class GameAssetSaveResult
    {
        public GameAssetKey Key;
        public Hash128 Guid;
        public string JsonPath;
        public string RelativeJsonPath;
        public ModManifestEntry ManifestEntry;
        public GameAsset Asset;
    }

    public static class GameAssetFactory
    {
        public static GameAsset Create(
            GameAssetKey key,
            Hash128 guid,
            IEnumerable<GameAssetComponent> components,
            Hash128 sourceGuid = default)
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            asset.name = $"{key.Key}@{key.Version}";
            asset.SetIdentity(key, guid);
            asset.SetSourceAssetGuid(sourceGuid);
            asset.SetComponents(components);
            return asset;
        }
    }

    public static class GameAssetPathPolicy
    {
        private const string AssetJsonFolder = "assets";

        public static string BuildDefaultRelativeJsonPath(GameAssetKey key)
        {
            return NormalizeSlashes(Path.Combine(AssetJsonFolder, key.Type, key.Key, $"{key.Key}@{key.Version}.json"));
        }

        public static string CombineAbsolute(string rootAbs, string relativePath)
        {
            return Path.GetFullPath(Path.Combine(rootAbs, (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
        }

        public static string NormalizeSlashes(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/').TrimStart('/');
        }
    }

    public sealed class GameAssetJsonStore
    {
        public async Task WriteAsync(string jsonPathAbs, GameAssetScriptableObject asset, CancellationToken ct = default)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            Directory.CreateDirectory(Path.GetDirectoryName(jsonPathAbs) ?? throw new InvalidOperationException($"Invalid JSON path: {jsonPathAbs}"));
            await WriteTextAtomicAsync(jsonPathAbs, asset.ToJson(), ct);
        }

        public async Task<GameAssetScriptableObject> ReadAsync(string jsonPathAbs, ModManifestEntry entry, CancellationToken ct = default)
        {
            var json = await File.ReadAllTextAsync(jsonPathAbs, ct);
            var type = ResolveRootType(entry, json) ?? typeof(GameAsset);
            if (!typeof(GameAssetScriptableObject).IsAssignableFrom(type))
                throw new InvalidOperationException($"Asset JSON root type is not a GameAssetScriptableObject: {type.FullName}");

            var asset = ScriptableObject.CreateInstance(type) as GameAssetScriptableObject;
            JsonConvert.PopulateObject(json, asset, GameAssetJsonRuntime.Settings);
            return asset;
        }

        public Task DeleteAsync(string jsonPathAbs, CancellationToken ct = default)
        {
            if (File.Exists(jsonPathAbs))
                File.Delete(jsonPathAbs);

            return Task.CompletedTask;
        }

        internal static async Task WriteTextAtomicAsync(string path, string text, CancellationToken ct)
        {
            var tempPath = $"{path}.tmp";
            await File.WriteAllTextAsync(tempPath, text, ct);

            if (File.Exists(path))
                File.Delete(path);

            File.Move(tempPath, path);
        }

        private static Type ResolveRootType(ModManifestEntry entry, string json)
        {
            if (!string.IsNullOrWhiteSpace(entry?.SoType))
            {
                var type = GameAssetJsonRuntime.Settings.SerializationBinder.BindToType(null, entry.SoType);
                if (type != null)
                    return type;
            }

            try
            {
                var typeToken = JObject.Parse(json)["$type"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(typeToken))
                    return typeof(GameAsset);

                SplitTypeName(typeToken, out var typeName, out var assemblyName);
                return GameAssetJsonRuntime.Settings.SerializationBinder.BindToType(assemblyName, typeName) ?? typeof(GameAsset);
            }
            catch
            {
                return typeof(GameAsset);
            }
        }

        private static void SplitTypeName(string value, out string typeName, out string assemblyName)
        {
            typeName = value;
            assemblyName = null;

            var comma = value.IndexOf(',');
            if (comma < 0)
                return;

            typeName = value.Substring(0, comma).Trim();
            assemblyName = value.Substring(comma + 1).Trim();
        }
    }

    public sealed class ModManifestStore
    {
        public const string ManifestFileName = "manifest.json";

        public async Task<ModManifest> LoadAsync(string modRootAbs, string mod, CancellationToken ct = default)
        {
            Directory.CreateDirectory(modRootAbs);
            var path = GetManifestPath(modRootAbs);
            if (!File.Exists(path))
                return CreateEmpty(mod);

            var json = await File.ReadAllTextAsync(path, ct);
            var manifest = JsonConvert.DeserializeObject<ModManifest>(json, GameAssetJsonRuntime.Settings) ?? CreateEmpty(mod);
            Normalize(manifest, mod);
            return manifest;
        }

        public async Task SaveAsync(string modRootAbs, ModManifest manifest, CancellationToken ct = default)
        {
            Directory.CreateDirectory(modRootAbs);
            Normalize(manifest, manifest?.Mod);
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented, GameAssetJsonRuntime.Settings);
            await GameAssetJsonStore.WriteTextAtomicAsync(GetManifestPath(modRootAbs), json, ct);
        }

        public static ModManifestEntry FindByKey(ModManifest manifest, GameAssetKey key)
        {
            return manifest?.Assets?.FirstOrDefault(entry => KeysEqual(entry.Key, key));
        }

        public static ModManifestEntry FindByGuid(ModManifest manifest, Hash128 guid)
        {
            return manifest?.Assets?.FirstOrDefault(entry => entry.GUID == guid);
        }

        public static void Upsert(ModManifest manifest, ModManifestEntry entry)
        {
            Normalize(manifest, manifest.Mod);
            manifest.Assets.RemoveAll(existing => KeysEqual(existing.Key, entry.Key));
            manifest.Assets.Add(entry);
            manifest.Assets = manifest.Assets
                .OrderBy(item => item.Key.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Key.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Key.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool Remove(ModManifest manifest, GameAssetKey key, out ModManifestEntry removed)
        {
            removed = null;
            if (manifest?.Assets == null)
                return false;

            var index = manifest.Assets.FindIndex(entry => KeysEqual(entry.Key, key));
            if (index < 0)
                return false;

            removed = manifest.Assets[index];
            manifest.Assets.RemoveAt(index);
            return true;
        }

        public static bool KeysEqual(GameAssetKey left, GameAssetKey right)
        {
            return string.Equals(left.Mod, right.Mod, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
        }

        public static string GetManifestPath(string modRootAbs)
        {
            return Path.Combine(modRootAbs, ManifestFileName);
        }

        private static ModManifest CreateEmpty(string mod)
        {
            return new ModManifest
            {
                Mod = string.IsNullOrWhiteSpace(mod) ? GameAssetKey.UNDEFINED : mod,
                GeneratedUtc = DateTime.UtcNow.ToString("O"),
                ManifestVersion = 1,
                Assets = new List<ModManifestEntry>()
            };
        }

        private static void Normalize(ModManifest manifest, string mod)
        {
            if (manifest == null)
                return;

            manifest.Mod = string.IsNullOrWhiteSpace(manifest.Mod)
                ? string.IsNullOrWhiteSpace(mod) ? GameAssetKey.UNDEFINED : mod
                : manifest.Mod;
            manifest.GeneratedUtc = DateTime.UtcNow.ToString("O");
            manifest.ManifestVersion = Math.Max(1, manifest.ManifestVersion);
            manifest.Assets ??= new List<ModManifestEntry>();
        }
    }

    public sealed class DiskModAssetRepository
    {
        private readonly string _modRootAbs;
        private readonly string _mod;
        private readonly ModManifestStore _manifestStore;
        private readonly GameAssetJsonStore _jsonStore;

        public DiskModAssetRepository(
            string modRootAbs,
            string mod,
            ModManifestStore manifestStore = null,
            GameAssetJsonStore jsonStore = null)
        {
            _modRootAbs = Path.GetFullPath(modRootAbs ?? throw new ArgumentNullException(nameof(modRootAbs)));
            _mod = string.IsNullOrWhiteSpace(mod) ? GameAssetKey.UNDEFINED : mod.Trim();
            _manifestStore = manifestStore ?? new ModManifestStore();
            _jsonStore = jsonStore ?? new GameAssetJsonStore();
        }

        public async Task<GameAssetSaveResult> SaveAsync(GameAssetSaveRequest request, CancellationToken ct = default)
        {
            if (request?.Asset == null)
                throw new ArgumentNullException(nameof(GameAssetSaveRequest.Asset));

            var asset = request.Asset;
            ValidateKey(asset.Key);

            var manifest = await _manifestStore.LoadAsync(_modRootAbs, _mod, ct);
            var existingByKey = ModManifestStore.FindByKey(manifest, asset.Key);
            var existingByGuid = asset.GUID.isValid ? ModManifestStore.FindByGuid(manifest, asset.GUID) : null;

            switch (request.Mode)
            {
                case GameAssetSaveMode.Create:
                    if (existingByKey != null)
                        throw new InvalidOperationException($"External asset key already exists: {asset.Key}");
                    if (existingByGuid != null)
                        throw new InvalidOperationException($"External asset GUID already exists: {asset.GUID}");
                    break;
                case GameAssetSaveMode.UpdateExisting:
                    if (existingByKey == null || existingByKey.GUID != asset.GUID)
                        throw new InvalidOperationException($"External asset does not exist for update: {asset.Key} / {asset.GUID}");
                    break;
                case GameAssetSaveMode.UpsertByKey:
                    if (existingByKey != null && request.PreserveExistingGuidOnUpsert && existingByKey.GUID.isValid)
                        asset.SetGuid(existingByKey.GUID);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(request.Mode), request.Mode, null);
            }

            var relativeJsonPath = !string.IsNullOrWhiteSpace(request.RelativeJsonPath)
                ? GameAssetPathPolicy.NormalizeSlashes(request.RelativeJsonPath)
                : existingByKey?.RelativeJsonPath ?? GameAssetPathPolicy.BuildDefaultRelativeJsonPath(asset.Key);
            var jsonPath = GameAssetPathPolicy.CombineAbsolute(_modRootAbs, relativeJsonPath);

            await _jsonStore.WriteAsync(jsonPath, asset, ct);

            if (existingByKey != null && !string.Equals(existingByKey.RelativeJsonPath, relativeJsonPath, StringComparison.OrdinalIgnoreCase))
                await _jsonStore.DeleteAsync(GameAssetPathPolicy.CombineAbsolute(_modRootAbs, existingByKey.RelativeJsonPath), ct);

            var entry = new ModManifestEntry
            {
                Key = asset.Key,
                GUID = asset.GUID,
                RelativeJsonPath = relativeJsonPath,
                SoType = asset.GetType().FullName
            };
            ModManifestStore.Upsert(manifest, entry);
            await _manifestStore.SaveAsync(_modRootAbs, manifest, ct);

            GameAsset resolvedAsset = asset;
            if (request.RefreshLibrary)
            {
                GameAssetLibraryManifest.ClearRuntimeCaches();
                await GameAssetLibraryManifest.EnsureInitializedAsync(ct);
            }

            if (request.ResolveSavedAsset && GameAssetLibraryManifest.TryResolve(asset.Key, out var resolved) && resolved is GameAsset gameAsset)
                resolvedAsset = gameAsset;

            return new GameAssetSaveResult
            {
                Key = asset.Key,
                Guid = asset.GUID,
                JsonPath = jsonPath,
                RelativeJsonPath = relativeJsonPath,
                ManifestEntry = entry,
                Asset = resolvedAsset
            };
        }

        public async Task<GameAssetScriptableObject> LoadAsync(GameAssetKey key, CancellationToken ct = default)
        {
            ValidateKey(key);
            var manifest = await _manifestStore.LoadAsync(_modRootAbs, _mod, ct);
            var entry = ModManifestStore.FindByKey(manifest, key);
            if (entry == null)
                return null;

            return await _jsonStore.ReadAsync(GameAssetPathPolicy.CombineAbsolute(_modRootAbs, entry.RelativeJsonPath), entry, ct);
        }

        public async Task<IReadOnlyList<ModManifestEntry>> ListAsync(CancellationToken ct = default)
        {
            var manifest = await _manifestStore.LoadAsync(_modRootAbs, _mod, ct);
            return manifest.Assets.ToArray();
        }

        public async Task<bool> DeleteAsync(GameAssetDeleteRequest request, CancellationToken ct = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            ValidateKey(request.Key);
            var manifest = await _manifestStore.LoadAsync(_modRootAbs, _mod, ct);
            if (!ModManifestStore.Remove(manifest, request.Key, out var removed))
                return false;

            await _jsonStore.DeleteAsync(GameAssetPathPolicy.CombineAbsolute(_modRootAbs, removed.RelativeJsonPath), ct);
            await _manifestStore.SaveAsync(_modRootAbs, manifest, ct);

            if (request.RefreshLibrary)
            {
                GameAssetLibraryManifest.ClearRuntimeCaches();
                await GameAssetLibraryManifest.EnsureInitializedAsync(ct);
            }

            return true;
        }

        private static void ValidateKey(GameAssetKey key)
        {
            if (string.IsNullOrWhiteSpace(key.Mod)
                || string.IsNullOrWhiteSpace(key.Type)
                || string.IsNullOrWhiteSpace(key.Key)
                || string.IsNullOrWhiteSpace(key.Version))
            {
                throw new ArgumentException($"Full GameAssetKey is required: {key}");
            }
        }
    }

    public static class GameAssetCrud
    {
        public static Task<GameAssetSaveResult> SaveAsync(GameAssetSaveRequest request, CancellationToken ct = default)
        {
            var repository = CreateRepository(request?.ModRootAbs, request?.Mod);
            return repository.SaveAsync(request, ct);
        }

        public static Task<GameAssetSaveResult> UpdateAsync(GameAssetSaveRequest request, CancellationToken ct = default)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            return SaveAsync(new GameAssetSaveRequest
            {
                ModRootAbs = request.ModRootAbs,
                Mod = request.Mod,
                Asset = request.Asset,
                Mode = GameAssetSaveMode.UpdateExisting,
                RelativeJsonPath = request.RelativeJsonPath,
                PreserveExistingGuidOnUpsert = request.PreserveExistingGuidOnUpsert,
                RefreshLibrary = request.RefreshLibrary,
                ResolveSavedAsset = request.ResolveSavedAsset,
            }, ct);
        }

        public static async Task<GameAssetScriptableObject> LoadAsync(GameAssetKey key, CancellationToken ct = default)
        {
            await GameAssetLibraryManifest.EnsureInitializedAsync(ct);
            return GameAssetLibraryManifest.TryResolve(key, out var asset) ? asset : null;
        }

        public static Task<GameAssetScriptableObject> LoadAsync(string modRootAbs, string mod, GameAssetKey key, CancellationToken ct = default)
        {
            return CreateRepository(modRootAbs, mod).LoadAsync(key, ct);
        }

        public static GameAssetScriptableObject Load(GameAssetKey key)
        {
            return GameAssetLibraryManifest.TryResolve(key, out var asset) ? asset : null;
        }

        public static IReadOnlyList<GameAssetScriptableObject> List(bool includeExternal = true)
        {
            return GameAssetLibraryManifest.CollectAllAssets(includeExternal).Values.Where(asset => asset != null).ToArray();
        }

        public static async Task<IReadOnlyList<GameAssetScriptableObject>> ListAsync(bool includeExternal = true, CancellationToken ct = default)
        {
            await GameAssetLibraryManifest.EnsureInitializedAsync(ct);
            return List(includeExternal);
        }

        public static Task<IReadOnlyList<ModManifestEntry>> ListAsync(string modRootAbs, string mod, CancellationToken ct = default)
        {
            return CreateRepository(modRootAbs, mod).ListAsync(ct);
        }

        public static Task<bool> DeleteAsync(GameAssetDeleteRequest request, CancellationToken ct = default)
        {
            return CreateRepository(request?.ModRootAbs, request?.Mod).DeleteAsync(request, ct);
        }

        private static DiskModAssetRepository CreateRepository(string modRootAbs, string mod)
        {
            if (string.IsNullOrWhiteSpace(modRootAbs))
                throw new ArgumentException("External mod root path is required.", nameof(modRootAbs));

            return new DiskModAssetRepository(modRootAbs, mod);
        }
    }
}
#endif
