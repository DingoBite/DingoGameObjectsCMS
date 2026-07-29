#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;

namespace DingoGameObjectsCMS.Modding
{
    public sealed class ModPackage
    {
        public readonly string ModRootAbs;
        public readonly ModManifest Manifest;

        private readonly Dictionary<GameAssetKey, ModManifestEntry> _byKey;
        private readonly Dictionary<GameAssetKey, GameAssetScriptableObject> _cache = new();

        public ModPackage(string modRootAbs, ModManifest manifest)
        {
            ModRootAbs = Path.GetFullPath(modRootAbs ?? throw new ArgumentNullException(nameof(modRootAbs)));
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Manifest.Assets ??= new List<ModManifestEntry>();

            _byKey = new Dictionary<GameAssetKey, ModManifestEntry>(new GameAssetKeyComparer());
            foreach (var e in Manifest.Assets)
            {
                if (e == null)
                    throw new InvalidDataException($"Mod manifest '{ModRootAbs}' contains a null asset entry.");

                try
                {
                    GameAssetVersionUtils.RequireCanonical(
                        e.Key.Version,
                        nameof(e.Key.Version));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"Mod manifest '{ModRootAbs}' contains invalid asset version '{e.Key.Version}'.",
                        exception);
                }
                GameAssetPathPolicy.CombineAbsolute(ModRootAbs, e.RelativeJsonPath);
                if (!_byKey.TryAdd(e.Key, e))
                    throw new InvalidDataException($"Mod manifest '{ModRootAbs}' contains duplicate asset key '{e.Key}'.");
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

            var jsonPath = GameAssetPathPolicy.CombineAbsolute(ModRootAbs, entry.RelativeJsonPath);
            var json = File.ReadAllText(jsonPath);
            asset = GameAssetJson.FromJson(json);
            if (asset == null)
                return false;

            _cache[key] = asset;
            return true;
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
