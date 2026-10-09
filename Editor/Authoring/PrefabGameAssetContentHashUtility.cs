using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    public static class PrefabGameAssetContentHashUtility
    {
        public static string Compute(string assetPath, PrefabGameAssetContentHashCache cache = null)
        {
            IReadOnlyList<GameAssetComponent> components = Array.Empty<GameAssetComponent>();
            if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                if (prefab != null && prefab.TryGetComponent<GameAssetAuthoring>(out var authoring) && GameAssetAuthoringEditorUtility.TryCollect(authoring, out var collection, out _))
                    components = collection.Components;
            }
            return Compute(assetPath, components, cache);
        }

        public static string Compute(string assetPath, IReadOnlyList<GameAssetComponent> components, PrefabGameAssetContentHashCache cache = null)
        {
            var resources = PrefabGameAssetIndexConfiguration.ResourceDependencies?.Invoke(components ?? Array.Empty<GameAssetComponent>()) ?? Array.Empty<string>();
            return (cache ?? new PrefabGameAssetContentHashCache()).Compute(assetPath, resources);
        }
    }

    public class PrefabGameAssetContentHashCache
    {
        private readonly Dictionary<string, string[]> _dependencyPaths = new(StringComparer.Ordinal);
        private readonly Dictionary<string, KeyValuePair<string, string>> _dependencyHashes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _contentHashes = new(StringComparer.Ordinal);

        public IEnumerable<string> DependencyPaths => _dependencyPaths.Values.SelectMany(paths => paths).Distinct(StringComparer.Ordinal);

        public string Compute(string assetPath, IEnumerable<string> resourcePaths)
        {
            var roots = resourcePaths.Append(assetPath).Where(path => !string.IsNullOrEmpty(path)).Select(path => path.Replace('\\', '/')).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var cacheKey = string.Join("\n", roots);
            if (_contentHashes.TryGetValue(cacheKey, out var cached))
                return cached;
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                if (!_dependencyPaths.TryGetValue(root, out var paths))
                {
                    paths = AssetDatabase.GetDependencies(root, true).Append(root).Where(path => !string.Equals(path, PrefabGameAssetIndexConfiguration.IndexAssetPath, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
                    _dependencyPaths.Add(root, paths);
                }
                dependencies.UnionWith(paths);
            }
            var digests = new List<KeyValuePair<string, string>>(dependencies.Count);
            foreach (var dependency in dependencies)
            {
                if (!_dependencyHashes.TryGetValue(dependency, out var digest))
                {
                    var guid = AssetDatabase.AssetPathToGUID(dependency);
                    digest = new KeyValuePair<string, string>(string.IsNullOrEmpty(guid) ? "builtin:" + dependency : guid, AssetDatabase.GetAssetDependencyHash(dependency).ToString());
                    _dependencyHashes.Add(dependency, digest);
                }
                digests.Add(digest);
            }
            using var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(1);
            writer.WriteInt32(digests.Count);
            foreach (var digest in digests.OrderBy(digest => digest.Key, StringComparer.Ordinal).ThenBy(digest => digest.Value, StringComparer.Ordinal))
            {
                writer.WriteString(digest.Key);
                writer.WriteString(digest.Value);
            }
            using var hash = SHA256.Create();
            var result = BitConverter.ToString(hash.ComputeHash(writer.ToArray())).Replace("-", string.Empty).ToLowerInvariant();
            _contentHashes.Add(cacheKey, result);
            return result;
        }

        public void Clear()
        {
            _dependencyPaths.Clear();
            _dependencyHashes.Clear();
            _contentHashes.Clear();
        }
    }
}
