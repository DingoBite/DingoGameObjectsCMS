using System;
using System.Collections.Generic;
using System.IO;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public readonly struct GameAssetModuleResourceUse : IEquatable<GameAssetModuleResourceUse>
    {
        public readonly GameAssetKey AssetKey;
        public readonly GameAssetResourceRef Resource;

        public GameAssetModuleResourceUse(
            GameAssetKey assetKey,
            GameAssetResourceRef resource)
        {
            AssetKey = assetKey;
            Resource = resource;
        }

        public bool Equals(GameAssetModuleResourceUse other)
        {
            return AssetKey.Equals(other.AssetKey)
                   && Resource.Equals(other.Resource);
        }

        public override bool Equals(object obj)
        {
            return obj is GameAssetModuleResourceUse other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(AssetKey, Resource);
        }
    }

    /// <summary>
    /// Discovers physical resources from references serialized into GameAssets
    /// listed by the mounted module manifest. Project code never supplies a
    /// parallel file-name catalog.
    /// </summary>
    public static class GameAssetModuleResourceDiscovery
    {
        public static IReadOnlyList<GameAssetResourceRef> CollectLocalResources(
            ModPackage package,
            string requiredKind)
        {
            var uses = CollectLocalResourceUses(package, requiredKind);
            var discovered = new HashSet<GameAssetResourceRef>();
            for (var index = 0; index < uses.Count; index++)
            {
                discovered.Add(uses[index].Resource);
            }

            var result = new List<GameAssetResourceRef>(discovered);
            result.Sort(CompareResources);
            return result;
        }

        public static IReadOnlyList<GameAssetModuleResourceUse> CollectLocalResourceUses(
            ModPackage package,
            string requiredKind)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }
            if (string.IsNullOrWhiteSpace(requiredKind))
            {
                throw new ArgumentException("A package resource kind is required.", nameof(requiredKind));
            }

            var manifestResource = new GameAssetResourceRef(package.ModuleId, "manifest.json");
            var manifest = package.LoadJson<ModManifest>(manifestResource);
            if (!string.Equals(manifest.Mod, package.ModuleId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Module manifest '{manifestResource}' does not belong to '{package.ModuleId}'.");
            }

            manifest.Assets ??= new List<ModManifestEntry>();
            var discovered = new HashSet<GameAssetModuleResourceUse>();
            for (var index = 0; index < manifest.Assets.Count; index++)
            {
                var entry = manifest.Assets[index]
                            ?? throw new InvalidDataException($"Module manifest asset entry {index} is null.");
                var assetResource = new GameAssetResourceRef(package.ModuleId, entry.RelativeJsonPath);
                if (!string.Equals(package.RequireKind(assetResource), "gameAsset", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Module manifest asset '{assetResource}' is not classified as a GameAsset.");
                }

                JToken document;
                try
                {
                    document = JToken.Parse(package.ReadAllText(assetResource));
                }
                catch (Exception exception) when (exception is Newtonsoft.Json.JsonException
                                                  || exception is ArgumentException)
                {
                    throw new InvalidDataException(
                        $"Module GameAsset '{assetResource}' is not valid JSON.",
                        exception);
                }

                CollectFromToken(
                    document,
                    package,
                    requiredKind,
                    entry.Key,
                    discovered);
            }

            var result = new List<GameAssetModuleResourceUse>(discovered);
            result.Sort(CompareUses);
            return result;
        }

        private static void CollectFromToken(
            JToken token,
            ModPackage package,
            string requiredKind,
            GameAssetKey assetKey,
            HashSet<GameAssetModuleResourceUse> destination)
        {
            if (token is JObject value
                && TryGetString(value, "moduleId", out var moduleId)
                && TryGetString(value, "relativePath", out var relativePath)
                && !string.IsNullOrWhiteSpace(moduleId)
                && !string.IsNullOrWhiteSpace(relativePath))
            {
                var resource = new GameAssetResourceRef(moduleId, relativePath);
                if (string.Equals(resource.ModuleId, package.ModuleId, StringComparison.Ordinal))
                {
                    if (!package.Contains(resource.RelativePath))
                    {
                        throw new InvalidDataException(
                            $"Module GameAsset references missing local resource '{resource}'.");
                    }
                    if (string.Equals(package.RequireKind(resource), requiredKind, StringComparison.Ordinal))
                    {
                        destination.Add(new GameAssetModuleResourceUse(
                            assetKey,
                            resource));
                    }
                }
            }

            if (token is JContainer container)
            {
                foreach (var child in container.Children())
                {
                    CollectFromToken(
                        child,
                        package,
                        requiredKind,
                        assetKey,
                        destination);
                }
            }
        }

        private static bool TryGetString(JObject value, string name, out string result)
        {
            foreach (var property in value.Properties())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result = property.Value.Type == JTokenType.String
                    ? property.Value.Value<string>()
                    : null;
                return result != null;
            }

            result = null;
            return false;
        }

        private static int CompareResources(GameAssetResourceRef left, GameAssetResourceRef right)
        {
            var module = string.CompareOrdinal(left.ModuleId, right.ModuleId);
            return module != 0 ? module : string.CompareOrdinal(left.RelativePath, right.RelativePath);
        }

        private static int CompareUses(
            GameAssetModuleResourceUse left,
            GameAssetModuleResourceUse right)
        {
            var resource = CompareResources(left.Resource, right.Resource);
            if (resource != 0)
            {
                return resource;
            }
            var type = string.CompareOrdinal(left.AssetKey.Type, right.AssetKey.Type);
            if (type != 0)
            {
                return type;
            }
            var key = string.CompareOrdinal(left.AssetKey.Key, right.AssetKey.Key);
            return key != 0
                ? key
                : string.CompareOrdinal(
                    left.AssetKey.Version,
                    right.AssetKey.Version);
        }
    }
}
