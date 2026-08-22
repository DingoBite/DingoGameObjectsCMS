#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMS.Serialization
{
    public static class GameAssetDocumentComposer
    {
        public const int MAX_BASE_DEPTH = 8;

        public const string TYPE_PROPERTY = "$type";
        public const string KEY_PROPERTY = "Key";
        public const string GUID_PROPERTY = "GUID";
        public const string PREFAB_PROPERTY = "Prefab";
        public const string COMPONENTS_PROPERTY = "Components";
        public const string SOURCE_ASSET_KEY_PROPERTY = "SourceAssetKey";
        public const string PARENT_SEGMENT = "..";

        private static readonly string[] NON_COMPONENT_SEGMENTS =
        {
            TYPE_PROPERTY, KEY_PROPERTY, GUID_PROPERTY, PREFAB_PROPERTY, SOURCE_ASSET_KEY_PROPERTY, COMPONENTS_PROPERTY,
        };

        public static JObject Compose(GameAssetKey key, JObject document, Func<GameAssetKey, JObject> resolveBaseDocument)
        {
            return Compose(key, document, resolveBaseDocument, new List<GameAssetKey>());
        }

        public static JObject ApplyOverrides(GameAssetKey context, JObject baseDocument, GameAssetOverrides overrides)
        {
            var result = (JObject)baseDocument.DeepClone();
            if (overrides == null)
                return result;

            RemoveComponents(context, result, overrides.RemovedComponents);
            OverrideComponents(context, result, overrides.OverrideComponents);
            RemoveFields(context, result, overrides.RemovedFields);
            OverrideFields(context, result, overrides.OverrideFields);
            return result;
        }

        public static string CanonicalizeOverrides(GameAssetOverrides overrides)
        {
            if (overrides == null || !overrides.HasAny)
                return "{}";

            var canonical = new JObject();
            if (overrides.RemovedComponents is { Count: > 0 })
            {
                canonical["RemovedComponents"] = new JArray(
                    overrides.RemovedComponents.OrderBy(value => value, StringComparer.Ordinal));
            }
            if (overrides.OverrideComponents is { Count: > 0 })
            {
                canonical["OverrideComponents"] = new JArray(overrides.OverrideComponents
                    .OrderBy(value => value[TYPE_PROPERTY]?.Value<string>(), StringComparer.Ordinal)
                    .Select(CanonicalizeToken));
            }
            if (overrides.RemovedFields is { Count: > 0 })
            {
                canonical["RemovedFields"] = new JArray(
                    overrides.RemovedFields.OrderBy(value => value, StringComparer.Ordinal));
            }
            if (overrides.OverrideFields is { Count: > 0 })
            {
                var fields = new JObject();
                foreach (var path in overrides.OverrideFields.Keys.OrderBy(value => value, StringComparer.Ordinal))
                {
                    fields[path] = CanonicalizeToken(overrides.OverrideFields[path]);
                }
                canonical["OverrideFields"] = fields;
            }

            return canonical.ToString(Formatting.None);
        }

        private static JToken CanonicalizeToken(JToken token)
        {
            if (token is not JObject owner)
                return token is JArray array ? new JArray(array.Select(CanonicalizeToken)) : token;

            var result = new JObject();
            foreach (var property in owner.Properties().OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                result[property.Name] = CanonicalizeToken(property.Value);
            }

            return result;
        }

        public static bool HasBase(JObject document)
        {
            return TryReadPrefab(document, out var prefab) && prefab.HasBase;
        }

        public static bool TryReadBaseKey(JObject document, out GameAssetKey baseKey)
        {
            if (TryReadPrefab(document, out var prefab) && prefab.HasBase)
            {
                baseKey = prefab.Base;
                return true;
            }

            baseKey = default;
            return false;
        }

        private static JObject Compose(GameAssetKey key, JObject document, Func<GameAssetKey, JObject> resolveBaseDocument, List<GameAssetKey> chain)
        {
            if (!TryReadPrefab(document, out var prefab))
                return document;

            if (!prefab.HasBase)
            {
                if (prefab.HasAny)
                    throw new InvalidDataException($"GameAsset '{key}' declares prefab overrides without a base to apply them to.");

                return document;
            }

            var baseKey = RequireExactBaseKey(key, prefab.Base);
            for (var index = 0; index < chain.Count; index++)
            {
                if (chain[index] == baseKey)
                    throw new InvalidDataException($"GameAsset '{key}' forms a prefab cycle through '{baseKey}': {DescribeChain(chain, key, baseKey)}.");
            }
            if (chain.Count + 1 > MAX_BASE_DEPTH)
                throw new InvalidDataException($"GameAsset '{key}' exceeds the maximum prefab depth of {MAX_BASE_DEPTH}: {DescribeChain(chain, key, baseKey)}.");

            var rawBaseDocument = resolveBaseDocument(baseKey)
                ?? throw new InvalidDataException($"GameAsset '{key}' declares base '{baseKey}', which no mounted module provides.");

            chain.Add(key);
            JObject baseDocument;
            try
            {
                baseDocument = Compose(baseKey, rawBaseDocument, resolveBaseDocument, chain);
            }
            finally
            {
                chain.RemoveAt(chain.Count - 1);
            }

            var result = ApplyOverrides(key, baseDocument, prefab);
            ApplyRootType(key, result, document);
            ApplyIdentity(result, document);

            result.Remove(PREFAB_PROPERTY);
            return result;
        }

        private static readonly string[] PREFAB_MEMBERS =
        {
            "Base", "RemovedComponents", "OverrideComponents", "RemovedFields", "OverrideFields",
        };

        private static bool TryReadPrefab(JObject document, out GameAssetPrefab prefab)
        {
            prefab = null;
            if (document?[PREFAB_PROPERTY] is not JObject node)
                return false;

            foreach (var member in node.Properties())
            {
                if (Array.IndexOf(PREFAB_MEMBERS, member.Name) < 0)
                {
                    throw new InvalidDataException(
                        $"GameAsset prefab declaration has unknown member '{member.Name}'. Expected one of {string.Join(", ", PREFAB_MEMBERS)}.");
                }
            }

            try
            {
                prefab = node.ToObject<GameAssetPrefab>(GameAssetJson.JsonSerializer);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("GameAsset prefab declaration is not valid JSON.", exception);
            }

            return prefab != null;
        }

        private static GameAssetKey RequireExactBaseKey(GameAssetKey key, GameAssetKey baseKey)
        {
            if (string.IsNullOrWhiteSpace(baseKey.Mod) || string.IsNullOrWhiteSpace(baseKey.Type) || string.IsNullOrWhiteSpace(baseKey.Key)
                || baseKey.Mod == GameAssetKey.UNDEFINED || baseKey.Type == GameAssetKey.NONE || baseKey.Key == GameAssetKey.NONE)
            {
                throw new InvalidDataException($"GameAsset '{key}' declares an incomplete prefab base '{baseKey}'.");
            }

            try
            {
                GameAssetVersionUtils.RequireCanonical(baseKey.Version, nameof(baseKey.Version));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"GameAsset '{key}' declares base '{baseKey}' without an exact version. A prefab base must be pinned so the derived content cannot drift.",
                    exception);
            }

            if (baseKey == key)
                throw new InvalidDataException($"GameAsset '{key}' declares itself as its own prefab base.");

            return baseKey;
        }

        private static void ApplyRootType(GameAssetKey key, JObject result, JObject document)
        {
            var derivedType = document[TYPE_PROPERTY]?.Value<string>();
            if (string.IsNullOrWhiteSpace(derivedType))
                return;

            var baseType = result[TYPE_PROPERTY]?.Value<string>();
            if (string.IsNullOrWhiteSpace(baseType))
            {
                result[TYPE_PROPERTY] = derivedType;
                return;
            }
            if (derivedType != baseType)
                throw new InvalidDataException($"GameAsset '{key}' is '{derivedType}' but its prefab base is '{baseType}'. A derived asset must declare the same concrete type as its base.");
        }

        private static void ApplyIdentity(JObject result, JObject document)
        {
            CopyOrRemove(result, document, KEY_PROPERTY);
            CopyOrRemove(result, document, GUID_PROPERTY);
            CopyOrRemove(result, document, SOURCE_ASSET_KEY_PROPERTY);
        }

        private static void CopyOrRemove(JObject result, JObject document, string property)
        {
            if (document[property] is { } value)
                result[property] = value.DeepClone();
            else
                result.Remove(property);
        }

        private static void RemoveComponents(GameAssetKey key, JObject result, IReadOnlyList<string> removed)
        {
            if (removed == null || removed.Count == 0)
                return;

            var components = RequireComponents(key, result);
            var byType = IndexComponents(key, components);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < removed.Count; index++)
            {
                var alias = removed[index];
                if (string.IsNullOrWhiteSpace(alias))
                    throw new InvalidDataException($"GameAsset '{key}' removed component {index} has no type alias.");

                if (!seen.Add(alias))
                    throw new InvalidDataException($"GameAsset '{key}' removes component '{alias}' more than once.");

                if (!byType.TryGetValue(alias, out var existing))
                    throw new InvalidDataException($"GameAsset '{key}' removes component '{alias}', which its prefab base does not provide.");

                components.Remove(existing);
                byType.Remove(alias);
            }
        }

        private static void OverrideComponents(GameAssetKey key, JObject result, IReadOnlyList<JObject> overrides)
        {
            if (overrides == null || overrides.Count == 0)
                return;

            if (result[COMPONENTS_PROPERTY] is not JArray components)
            {
                components = new JArray();
                result[COMPONENTS_PROPERTY] = components;
            }

            var byType = IndexComponents(key, components);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < overrides.Count; index++)
            {
                var component = overrides[index]
                    ?? throw new InvalidDataException($"GameAsset '{key}' component override {index} is null.");
                var alias = component[TYPE_PROPERTY]?.Value<string>();
                if (string.IsNullOrWhiteSpace(alias))
                    throw new InvalidDataException($"GameAsset '{key}' component override {index} has no '{TYPE_PROPERTY}'.");

                if (!seen.Add(alias))
                    throw new InvalidDataException($"GameAsset '{key}' overrides component '{alias}' more than once.");

                var replacement = (JObject)component.DeepClone();
                if (byType.TryGetValue(alias, out var existing))
                {
                    components[components.IndexOf(existing)] = replacement;
                }
                else
                {
                    components.Add(replacement);
                }
                byType[alias] = replacement;
            }
        }

        private static void RemoveFields(GameAssetKey key, JObject result, IReadOnlyList<string> removed)
        {
            if (removed == null || removed.Count == 0)
                return;

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < removed.Count; index++)
            {
                var path = removed[index];
                var segments = RequireFieldPath(key, path);
                if (!seen.Add(path))
                    throw new InvalidDataException($"GameAsset '{key}' removes field '{path}' more than once.");

                var parent = ResolveParent(key, result, segments, path, out var leaf);
                if (parent is JObject owner)
                {
                    if (!owner.Remove(leaf))
                        throw new InvalidDataException($"GameAsset '{key}' removes field '{path}', which its prefab base does not provide.");
                }
                else
                {
                    var array = (JArray)parent;
                    array.Remove(ResolveArrayElement(key, array, leaf, path));
                }
            }
        }

        private static void OverrideFields(GameAssetKey key, JObject result, Dictionary<string, JToken> overrides)
        {
            if (overrides == null || overrides.Count == 0)
                return;

            var paths = overrides.Keys.OrderBy(path => path, StringComparer.Ordinal).ToArray();
            for (var index = 0; index < paths.Length; index++)
            {
                var path = paths[index];
                var segments = RequireFieldPath(key, path);
                var value = overrides[path]
                    ?? throw new InvalidDataException(
                        $"GameAsset '{key}' sets field '{path}' to null. Use RemovedFields to fall back to the declared default.");

                var parent = ResolveParent(key, result, segments, path, out var leaf);
                if (parent is JObject owner)
                {
                    owner[leaf] = value.DeepClone();
                }
                else
                {
                    var array = (JArray)parent;
                    array[array.IndexOf(ResolveArrayElement(key, array, leaf, path))] = value.DeepClone();
                }
            }
        }

        private static string[] RequireFieldPath(GameAssetKey key, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path[0] != '/')
                throw new InvalidDataException($"GameAsset '{key}' field path '{path}' must start with '/'.");

            var segments = SplitPath(path);
            if (segments[0] != PARENT_SEGMENT && Array.IndexOf(NON_COMPONENT_SEGMENTS, segments[0]) >= 0)
            {
                throw new InvalidDataException(
                    $"GameAsset '{key}' field path '{path}' must start with a component type alias or '{PARENT_SEGMENT}', not '{segments[0]}'.");
            }
            if (segments.Length == 1)
            {
                throw new InvalidDataException(
                    $"GameAsset '{key}' field path '{path}' targets a whole component. Use RemovedComponents and OverrideComponents.");
            }
            if (segments[^1] == PARENT_SEGMENT)
                throw new InvalidDataException($"GameAsset '{key}' field path '{path}' ends with '{PARENT_SEGMENT}' and targets nothing.");

            return segments;
        }

        private static JContainer ResolveParent(GameAssetKey key, JObject root, string[] segments, string path, out string leaf)
        {
            leaf = segments[^1];
            JToken current = RequireComponents(key, root);
            for (var index = 0; index < segments.Length - 1; index++)
            {
                current = segments[index] == PARENT_SEGMENT
                    ? MoveUp(key, current, path)
                    : ResolveSegment(key, current, segments[index], path);
            }

            var parent = current as JContainer
                         ?? throw new InvalidDataException($"GameAsset '{key}' field path '{path}' passes through a value that has no members.");
            if (ReferenceEquals(parent, root) && Array.IndexOf(NON_COMPONENT_SEGMENTS, leaf) >= 0)
                throw new InvalidDataException($"GameAsset '{key}' field path '{path}' targets reserved property '{leaf}'.");

            return parent;
        }

        private static JToken MoveUp(GameAssetKey key, JToken current, string path)
        {
            var parent = current.Parent is JProperty property ? property.Parent : current.Parent;
            return parent
                   ?? throw new InvalidDataException($"GameAsset '{key}' field path '{path}' walks above the document root.");
        }

        private static JToken ResolveSegment(GameAssetKey key, JToken current, string segment, string path)
        {
            switch (current)
            {
                case JObject owner:
                    return owner[segment]
                           ?? throw new InvalidDataException($"GameAsset '{key}' field path '{path}' does not exist in its prefab base at '{segment}'.");

                case JArray array:
                    return ResolveArrayElement(key, array, segment, path);

                default:
                    throw new InvalidDataException($"GameAsset '{key}' field path '{path}' passes through a value that has no members at '{segment}'.");
            }
        }

        private static JToken ResolveArrayElement(GameAssetKey key, JArray array, string segment, string path)
        {
            if (int.TryParse(segment, out var arrayIndex))
            {
                if (arrayIndex < 0 || arrayIndex >= array.Count)
                    throw new InvalidDataException($"GameAsset '{key}' field path '{path}' index {arrayIndex} is outside its prefab base array.");

                return array[arrayIndex];
            }

            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is JObject element
                    && element[TYPE_PROPERTY]?.Value<string>() == segment)
                {
                    return element;
                }
            }

            throw new InvalidDataException($"GameAsset '{key}' field path '{path}' does not match any '{segment}' element in its prefab base.");
        }

        private static string[] SplitPath(string path)
        {
            var segments = path.Substring(1).Split('/');
            for (var index = 0; index < segments.Length; index++)
            {
                segments[index] = segments[index].Replace("~1", "/").Replace("~0", "~");
            }

            return segments;
        }

        private static JArray RequireComponents(GameAssetKey key, JObject result)
        {
            return result[COMPONENTS_PROPERTY] as JArray
                   ?? throw new InvalidDataException($"GameAsset '{key}' prefab base has no component list.");
        }

        private static Dictionary<string, JObject> IndexComponents(GameAssetKey key, JArray components)
        {
            var result = new Dictionary<string, JObject>(components.Count, StringComparer.Ordinal);
            for (var index = 0; index < components.Count; index++)
            {
                if (components[index] is not JObject component)
                    throw new InvalidDataException($"GameAsset '{key}' prefab base contains a non-object component at index {index}.");

                var alias = component[TYPE_PROPERTY]?.Value<string>();
                if (string.IsNullOrWhiteSpace(alias))
                    throw new InvalidDataException($"GameAsset '{key}' prefab base contains a component at index {index} without '{TYPE_PROPERTY}', so it cannot be overridden.");

                if (!result.TryAdd(alias, component))
                    throw new InvalidDataException($"GameAsset '{key}' prefab base contains component '{alias}' more than once, so an override would be ambiguous.");
            }

            return result;
        }

        private static string DescribeChain(IReadOnlyList<GameAssetKey> chain, GameAssetKey key, GameAssetKey baseKey)
        {
            var parts = new List<string>(chain.Count + 2);
            for (var index = 0; index < chain.Count; index++)
            {
                parts.Add(chain[index].ToString());
            }
            parts.Add(key.ToString());
            parts.Add(baseKey.ToString());
            return string.Join(" -> ", parts);
        }
    }
}
#endif
