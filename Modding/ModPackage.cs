#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;

namespace DingoGameObjectsCMS.Modding
{
    public sealed class ModPackage
    {
        public readonly GameAssetModuleContentSnapshot ContentSnapshot;

        private readonly Dictionary<GameAssetKey, ModManifestEntry> _byKey;
        private readonly Dictionary<GameAssetKey, GameAssetScriptableObject> _cache = new();

        public string ModuleId => ContentSnapshot.ModuleId;
        public string ContentHash => ContentSnapshot.ContentHash;
        public string RootPath => ContentSnapshot.SourceRootPath;
        public int ManifestVersion => ContentSnapshot.ManifestVersion;
        public string GeneratedUtc => ContentSnapshot.GeneratedUtc;
        public IReadOnlyList<GameAssetModuleAssetEntry> Assets =>
            ContentSnapshot.Assets;

        public ModPackage(GameAssetModuleContentSnapshot contentSnapshot)
        {
            ContentSnapshot = contentSnapshot
                              ?? throw new ArgumentNullException(nameof(contentSnapshot));

            _byKey = new Dictionary<GameAssetKey, ModManifestEntry>(new GameAssetKeyComparer());
            for (var index = 0; index < Assets.Count; index++)
            {
                var source = Assets[index];
                var entry = new ModManifestEntry
                {
                    Key = source.Key,
                    GUID = source.GUID,
                    RelativeJsonPath = source.RelativeJsonPath,
                };

                try
                {
                    GameAssetVersionUtils.RequireCanonical(
                        entry.Key.Version,
                        nameof(entry.Key.Version));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"Mod manifest '{RootPath}' contains invalid asset version '{entry.Key.Version}'.",
                        exception);
                }
                if (!ContentSnapshot.Contains(entry.RelativeJsonPath))
                {
                    throw new FileNotFoundException(
                        $"Mod manifest '{RootPath}' references missing asset '{entry.RelativeJsonPath}'.",
                        entry.RelativeJsonPath);
                }
                if (!_byKey.TryAdd(entry.Key, entry))
                {
                    throw new InvalidDataException(
                        $"Mod manifest '{RootPath}' contains duplicate asset key '{entry.Key}'.");
                }
            }
        }

        public bool Contains(string relativePath)
        {
            return ContentSnapshot.Contains(relativePath);
        }

        public ModManifest CreateManifestCopy()
        {
            var manifest = new ModManifest
            {
                Mod = ModuleId,
                ManifestVersion = ManifestVersion,
                GeneratedUtc = GeneratedUtc,
                Assets = new List<ModManifestEntry>(Assets.Count),
            };
            for (var index = 0; index < Assets.Count; index++)
            {
                var entry = Assets[index];
                manifest.Assets.Add(new ModManifestEntry
                {
                    Key = entry.Key,
                    GUID = entry.GUID,
                    RelativeJsonPath = entry.RelativeJsonPath,
                });
            }
            return manifest;
        }

        public byte[] ReadAllBytes(in GameAssetResourceRef resource)
        {
            _ = RequireFile(resource);
            return ContentSnapshot.ReadAllBytes(resource.RelativePath);
        }

        public string ReadAllText(in GameAssetResourceRef resource)
        {
            _ = RequireFile(resource);
            return ContentSnapshot.ReadAllText(resource.RelativePath);
        }

        public string RequireKind(in GameAssetResourceRef resource)
        {
            return RequireFile(resource).Kind;
        }

        public T LoadJson<T>(in GameAssetResourceRef resource)
        {
            try
            {
                var value = JsonConvert.DeserializeObject<T>(
                    ReadAllText(resource),
                    GameAssetJson.Settings);
                if (value == null)
                {
                    throw new InvalidDataException(
                        $"GameAsset resource '{resource}' produced a null {typeof(T).FullName} value.");
                }
                return value;
            }
            catch (Exception exception) when (
                exception is JsonException
                || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    $"GameAsset resource '{resource}' is not valid {typeof(T).FullName} JSON.",
                    exception);
            }
        }

        public bool TryGet(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            if (_cache.TryGetValue(key, out asset))
            {
                if (asset != null)
                    return true;

                _cache.Remove(key);
            }

            if (!_byKey.TryGetValue(key, out var entry))
            {
                asset = null;
                return false;
            }

            asset = ReadAsset(entry);

            _cache[key] = asset;
            return true;
        }

        public GameAssetModuleContentFile RequireFile(
            in GameAssetResourceRef resource)
        {
            if (!string.Equals(
                    resource.ModuleId,
                    ModuleId,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Resource module '{resource.ModuleId}' cannot be resolved from mounted module '{ModuleId}'.");
            }

            return ContentSnapshot.RequireFile(resource.RelativePath);
        }

        private GameAssetScriptableObject ReadAsset(
            ModManifestEntry entry)
        {
            GameAssetScriptableObject asset;
            try
            {
                asset = GameAssetJson.FromJson(
                    ContentSnapshot.ReadAllText(entry.RelativeJsonPath));
            }
            catch (Exception exception) when (
                exception is JsonException
                || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    $"GameAsset '{ModuleId}:{entry.RelativeJsonPath}' is invalid JSON.",
                    exception);
            }
            if (asset == null)
            {
                throw new InvalidDataException(
                    $"GameAsset '{ModuleId}:{entry.RelativeJsonPath}' produced a null asset.");
            }
            if (asset.Key != entry.Key)
            {
                throw new InvalidDataException(
                    $"GameAsset '{ModuleId}:{entry.RelativeJsonPath}' declares key '{asset.Key}', expected '{entry.Key}'.");
            }
            if (asset.GUID != entry.GUID)
            {
                throw new InvalidDataException(
                    $"GameAsset '{ModuleId}:{entry.RelativeJsonPath}' declares GUID '{asset.GUID}', expected '{entry.GUID}'.");
            }
            return asset;
        }

        private sealed class GameAssetKeyComparer : IEqualityComparer<GameAssetKey>
        {
            public bool Equals(GameAssetKey a, GameAssetKey b) =>
                string.Equals(a.Mod, b.Mod, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Type, b.Type, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(a.Version, b.Version, StringComparison.OrdinalIgnoreCase);

            public int GetHashCode(GameAssetKey k)
            {
                unchecked
                {
                    int h = 17;
                    h = h * 31 + (k.Mod?.ToLowerInvariant().GetHashCode() ?? 0);
                    h = h * 31 + (k.Type?.ToLowerInvariant().GetHashCode() ?? 0);
                    h = h * 31 + (k.Key?.ToLowerInvariant().GetHashCode() ?? 0);
                    h = h * 31 + (k.Version?.ToLowerInvariant().GetHashCode() ?? 0);
                    return h;
                }
            }
        }
    }
}
#endif
