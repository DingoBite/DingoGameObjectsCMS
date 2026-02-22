#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
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

            var soType = ResolveType(entry, json);
            if (soType == null || !typeof(GameAssetScriptableObject).IsAssignableFrom(soType))
            {
                asset = null;
                return false;
            }

            var so = ScriptableObject.CreateInstance(soType) as GameAssetScriptableObject;
            JsonConvert.PopulateObject(json, so, GameAssetJsonRuntime.Settings);

            _cache[key] = so;
            asset = so;
            return true;
        }

        private static Type ResolveType(ModManifestEntry entry, string json)
        {
            if (!string.IsNullOrWhiteSpace(entry.SoType))
            {
                var binder = GameAssetJsonRuntime.Settings.SerializationBinder;
                var t = binder.BindToType(assemblyName: null, typeName: entry.SoType);
                if (t != null)
                    return t;
            }

            try
            {
                var jo = JObject.Parse(json);
                var typeToken = jo["$type"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(typeToken))
                    return null;

                SplitTypeName(typeToken, out var typeName, out var asm);
                var binder = GameAssetJsonRuntime.Settings.SerializationBinder;
                return binder.BindToType(asm, typeName);
            }
            catch
            {
                return null;
            }
        }

        private static void SplitTypeName(string s, out string typeName, out string assemblyName)
        {
            typeName = s;
            assemblyName = null;
            var comma = s.IndexOf(',');
            if (comma < 0)
                return;
            typeName = s[..comma].Trim();
            assemblyName = s[(comma + 1)..].Trim();
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