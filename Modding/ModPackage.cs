#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
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
            ModRootAbs = modRootAbs;
            Manifest = manifest;

            _byKey = new Dictionary<GameAssetKey, ModManifestEntry>(new GameAssetKeyComparer());
            foreach (var e in manifest.Assets)
            {
                _byKey[e.Key] = e;
            }
        }

        public bool TryGet(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            if (_cache.TryGetValue(key, out asset))
                return asset != null;

            if (!_byKey.TryGetValue(key, out var entry))
            {
                asset = null;
                return false;
            }

            var jsonPath = Path.Combine(ModRootAbs, entry.RelativeJsonPath);
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
