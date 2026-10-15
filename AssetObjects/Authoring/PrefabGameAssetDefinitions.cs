using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    public class PrefabGameAssetDefinitions : IDisposable
    {
        public const string MOD = "_p_unity";
        public const string TYPE = "prefab";
        public const string VERSION = "0.0.0";

        private readonly Dictionary<GameAssetAuthoring, GameAsset> _assets = new();
        private readonly Dictionary<string, GameAssetAuthoring> _sourcesByGuid = new(StringComparer.Ordinal);
        private readonly List<PrefabPreparedGameAssetOverride> _overrides = new();
        private readonly Dictionary<string, GameAsset> _overrideAssets = new(StringComparer.Ordinal);
        private IReadOnlyList<GameAsset> _assetView = Array.Empty<GameAsset>();
        private IReadOnlyList<GameAsset> _resourceAssetView = Array.Empty<GameAsset>();
        private bool _disposed;

        public bool IsDisposed => _disposed;
        public IReadOnlyList<GameAsset> Assets => _assetView;
        public IReadOnlyList<GameAsset> ResourceAssets => _resourceAssetView;
        public string NativeContentHash { get; private set; }

        private PrefabGameAssetDefinitions() {}

        public static bool TryGetAuthoredReference(GameAssetAuthoring authoring, out GameAssetReference reference)
        {
            reference = default;
            if (authoring == null || !TryParseGuid(authoring.SourceDefinitionGuid, out _))
                return false;
            reference = new GameAssetReference(new GameAssetKey(MOD, TYPE, authoring.SourceDefinitionGuid, VERSION));
            return true;
        }

        public static bool TryPrepare(PrefabGameAssetIndex index, out PrefabGameAssetDefinitions definitions, out string error)
        {
            definitions = null;
            error = null;
            if (index == null || index.Entries == null)
            {
                error = "A prefab GameAsset index with an entry list is required.";
                return false;
            }

            var prepared = new PrefabGameAssetDefinitions();
            var guids = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                for (var entryIndex = 0; entryIndex < index.Entries.Count; entryIndex++)
                {
                    var entry = index.Entries[entryIndex];
                    if (entry == null || entry.Authoring == null)
                    {
                        error = $"Prefab GameAsset index entry #{entryIndex + 1} has no authoring reference.";
                        return false;
                    }
                    if (!TryParseGuid(entry.GUID, out var guid))
                    {
                        error = $"Prefab GameAsset index entry '{entry.Authoring.name}' has an invalid prefab GUID '{entry.GUID}'.";
                        return false;
                    }
                    if (!guids.Add(entry.GUID) || prepared._assets.ContainsKey(entry.Authoring))
                    {
                        error = $"Prefab GameAsset index entry '{entry.Authoring.name}' repeats GUID '{entry.GUID}' or its authoring reference.";
                        return false;
                    }
                    if (!GameAssetAuthoringCollector.TryCollect(entry.Authoring, out var collection, out var collectionError))
                        Debug.LogError($"Prefab '{entry.Authoring.name}' ({entry.GUID}): {collectionError} Using an empty GameAsset definition.", entry.Authoring);

                    var key = new GameAssetKey(MOD, TYPE, entry.GUID, VERSION);
                    var asset = CloneDefinition(entry.Authoring, collection?.Components ?? Array.Empty<GameAssetComponent>(), key, guid);
                    prepared._assets.Add(entry.Authoring, asset);
                    prepared._sourcesByGuid.Add(entry.GUID, entry.Authoring);
                }
                var overrideIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in index.Overrides ?? Array.Empty<PrefabGameAssetOverrideEntry>())
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !overrideIds.Add(entry.Id) || entry.Source == null || !prepared._assets.TryGetValue(entry.Source, out var source))
                    {
                        error = "A prefab GameAsset override requires a unique instance id and an indexed source prefab.";
                        return false;
                    }
                    if (!TryReadOverride(entry, source, out var overrides, out var overrideError))
                    {
                        Debug.LogError($"Prefab instance override '{entry.Id}' of '{entry.Source.name}': {overrideError} Using base GameAsset definition '{source.Key}'.", entry.Source);
                        prepared._overrideAssets.Add(entry.Id, source);
                        continue;
                    }
                    var asset = CloneDefinition(entry.Source, entry.Components, source.Key, source.GUID);
                    asset.SourceAssetKey = source.Key;
                    prepared._overrides.Add(new PrefabPreparedGameAssetOverride(entry.Id, new GameAssetReference(source.Key), overrides, asset));
                }
                prepared._assetView = Array.AsReadOnly(prepared._assets.Values.OrderBy(asset => asset.Key.Key, StringComparer.Ordinal).ToArray());
                prepared._resourceAssetView = Array.AsReadOnly(prepared._assetView.Concat(prepared._overrides.Select(entry => entry.Asset)).ToArray());
                prepared.NativeContentHash = CalculateNativeContentHash(index.Entries, index.Overrides);
                definitions = prepared;
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, index);
                error = $"Prefab GameAsset definitions could not be prepared: {exception.Message}";
                return false;
            }
            finally
            {
                if (definitions == null)
                    prepared.Dispose();
            }
        }

        public bool TryResolveReference(PrefabGameAssetReference reference, out GameAssetReference resolved)
        {
            return TryResolveReference(reference.Authoring, out resolved);
        }

        public bool TryGetAsset(PrefabGameAssetReference reference, out GameAsset asset)
        {
            return TryGetAsset(reference.Authoring, out asset);
        }

        public bool TryResolveReference(GameAssetAuthoring authoring, out GameAssetReference resolved)
        {
            resolved = default;
            if (!TryGetAsset(authoring, out var asset))
                return false;
            resolved = new GameAssetReference(asset.Key);
            return true;
        }

        public bool TryGetAsset(GameAssetAuthoring authoring, out GameAsset asset)
        {
            asset = null;
            if (_disposed || authoring == null)
                return false;
            if (_assets.TryGetValue(authoring, out asset))
                return true;
            if (!string.IsNullOrEmpty(authoring.RuntimeOverrideId))
                return _overrideAssets.TryGetValue(authoring.RuntimeOverrideId, out asset);
            return !string.IsNullOrEmpty(authoring.SourceDefinitionGuid) && _sourcesByGuid.TryGetValue(authoring.SourceDefinitionGuid, out var source) && _assets.TryGetValue(source, out asset);
        }

        public IReadOnlyList<GameAsset> RegisterOverrides(GameAssetLibraryLock assetLock, GameAssetTemplateCache templateCache)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(PrefabGameAssetDefinitions));
            var registered = new List<GameAsset>();
            var byGuid = new Dictionary<Hash128, GameAsset>();
            foreach (var entry in _overrides)
            {
                var blueprint = templateCache.ResolveStrict(entry.Source, assetLock);
                var key = GameAssetDerivedIdentity.CreateKey(blueprint.Asset, entry.Overrides, templateCache.CodecRegistry.SchemaHash);
                var guid = GameAssetDerivedIdentity.CreateGuid(blueprint.Asset, entry.Overrides, templateCache.CodecRegistry.SchemaHash);
                if (!byGuid.TryGetValue(guid, out var asset))
                {
                    if (assetLock.TryGet(key, out _))
                        throw new InvalidOperationException($"Prefab instance override '{entry.Id}' conflicts with existing GameAsset '{key}'.");
                    asset = entry.Asset;
                    asset.SetIdentity(key, guid);
                    var derived = templateCache.RegisterDerived(blueprint.Asset, entry.Overrides, asset);
                    assetLock.Set(key, new GameAssetLibraryLockEntry(key, guid, derived.Asset.MaterializedContentHash, asset));
                    byGuid.Add(guid, asset);
                    registered.Add(asset);
                }
                _overrideAssets[entry.Id] = asset;
            }
            return Array.AsReadOnly(registered.ToArray());
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var asset in _assets.Values)
            {
                DestroyDefinition(asset);
            }
            _assets.Clear();
            _sourcesByGuid.Clear();
            foreach (var entry in _overrides)
            {
                DestroyDefinition(entry.Asset);
            }
            _overrides.Clear();
            _overrideAssets.Clear();
            _assetView = Array.Empty<GameAsset>();
            _resourceAssetView = Array.Empty<GameAsset>();
        }

        private static GameAsset CloneDefinition(GameAssetAuthoring authoring, IEnumerable<GameAssetComponent> components, GameAssetKey key, Hash128 guid)
        {
            var source = ScriptableObject.CreateInstance<GameAsset>();
            try
            {
                source.hideFlags = HideFlags.HideAndDontSave;
                source.ResetToDefault(key, guid);
                source.SetComponents(components);
                var clone = Object.Instantiate(source);
                clone.hideFlags = HideFlags.HideAndDontSave;
                clone.name = authoring.name;
                return clone;
            }
            finally
            {
                DestroyDefinition(source);
            }
        }

        private static bool TryReadOverride(PrefabGameAssetOverrideEntry entry, GameAsset source, out GameAssetOverrides overrides, out string error)
        {
            overrides = null;
            error = null;
            if (entry.Components == null)
                error = "The effective component snapshot is missing.";
            else
            {
                var types = new HashSet<Type>(source.Components.Select(component => component.GetType()));
                var effectiveTypes = new HashSet<Type>();
                foreach (var component in entry.Components)
                {
                    if (component == null || !effectiveTypes.Add(component.GetType()))
                    {
                        error = "The effective snapshot contains a null or repeated GAC type.";
                        break;
                    }
                }
                if (error == null && !types.SetEquals(effectiveTypes))
                    error = "The effective snapshot changes the source GAC composition.";
                if (error == null && types.Count == 0)
                    error = "The source definition has no components to override.";
            }
            if (error != null)
                return false;
            if (string.IsNullOrWhiteSpace(entry.OverridesJson))
            {
                error = "The value overrides JSON is missing.";
                return false;
            }
            try
            {
                overrides = JsonConvert.DeserializeObject<GameAssetOverrides>(entry.OverridesJson, GameAssetJson.DataSettings);
            }
            catch (JsonException exception)
            {
                Debug.LogException(exception, entry.Source);
                error = "The value overrides JSON is invalid: " + exception.Message;
                return false;
            }
            if (overrides?.HasAny != true)
            {
                error = "The snapshot has no effective value overrides.";
                return false;
            }
            try
            {
                return TryValidateOverrideShape(source, overrides, out error);
            }
            catch (Exception exception) when (exception is JsonException || exception is InvalidDataException)
            {
                Debug.LogException(exception, entry.Source);
                error = "The value overrides structure is invalid: " + exception.Message;
                return false;
            }
        }

        private static bool TryValidateOverrideShape(GameAsset source, GameAssetOverrides overrides, out string error)
        {
            error = null;
            var serializer = JsonSerializer.Create(GameAssetJson.Settings);
            serializer.TypeNameHandling = TypeNameHandling.None;
            serializer.NullValueHandling = NullValueHandling.Include;
            var components = new JArray();
            var byAlias = new Dictionary<string, JObject>(StringComparer.Ordinal);
            foreach (var component in source.Components)
            {
                serializer.SerializationBinder.BindToName(component.GetType(), out _, out var alias);
                var value = JObject.FromObject(component, serializer);
                value[GameAssetDocumentComposer.TYPE_PROPERTY] = alias;
                byAlias.Add(alias, value);
                components.Add(value);
            }
            var assigned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var component in overrides.OverrideComponents ?? new List<JObject>())
            {
                var type = component?[GameAssetDocumentComposer.TYPE_PROPERTY];
                if (type?.Type != JTokenType.String || !byAlias.TryGetValue(type.Value<string>(), out var original))
                {
                    error = "A component override requires a known source '$type'.";
                    return false;
                }
                if (!assigned.Add(type.Value<string>()))
                {
                    error = "A component is overridden more than once.";
                    return false;
                }
                foreach (var property in component.Properties())
                {
                    if (property.Name != GameAssetDocumentComposer.TYPE_PROPERTY && original.Property(property.Name) == null)
                    {
                        error = "A component override contains an unknown field: " + property.Name;
                        return false;
                    }
                }
            }
            foreach (var path in (overrides.RemovedFields ?? new List<string>()).Concat(overrides.OverrideFields?.Keys ?? Enumerable.Empty<string>()))
            {
                var segments = path?.StartsWith("/", StringComparison.Ordinal) == true
                    ? path[1..].Split('/').Select(segment => segment.Replace("~1", "/").Replace("~0", "~")).ToArray()
                    : null;
                if (segments is { Length: >= 2 } && byAlias.TryGetValue(segments[0], out var original) && original.Property(segments[1]) == null)
                {
                    error = "A field override targets an unknown source field: " + path;
                    return false;
                }
            }
            var document = new JObject { [GameAssetDocumentComposer.COMPONENTS_PROPERTY] = components };
            var effective = GameAssetDocumentComposer.ApplyOverrides(source.Key, document, overrides);
            var effectiveTypes = effective[GameAssetDocumentComposer.COMPONENTS_PROPERTY] is JArray values
                ? values.Select(value => value is JObject component && component[GameAssetDocumentComposer.TYPE_PROPERTY]?.Type == JTokenType.String ? component[GameAssetDocumentComposer.TYPE_PROPERTY].Value<string>() : null).ToArray()
                : Array.Empty<string>();
            if (effectiveTypes.Length != byAlias.Count || !byAlias.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(effectiveTypes))
            {
                error = "The value overrides change the source GAC composition.";
                return false;
            }
            GameAssetDocumentComposer.CanonicalizeOverrides(overrides);
            return true;
        }

        private static bool TryParseGuid(string value, out Hash128 guid)
        {
            guid = default;
            if (value == null || value.Length != 32)
                return false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f'))
                    return false;
            }
            guid = Hash128.Parse(value);
            return guid.isValid;
        }

        private static string CalculateNativeContentHash(IReadOnlyList<PrefabGameAssetIndexEntry> entries, IReadOnlyList<PrefabGameAssetOverrideEntry> overrides)
        {
            using var writer = new CanonicalPatchBinaryWriter();
            writer.WriteString(MOD);
            writer.WriteInt32(entries.Count);
            foreach (var entry in entries.OrderBy(entry => entry.GUID, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(entry.ContentHash))
                    return null;
                writer.WriteString(entry.GUID);
                writer.WriteString(entry.ContentHash);
            }
            writer.WriteInt32(overrides?.Count ?? 0);
            foreach (var entry in (overrides ?? Array.Empty<PrefabGameAssetOverrideEntry>()).OrderBy(entry => entry.Id, StringComparer.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(entry.ContentHash))
                    return null;
                writer.WriteString(entry.Id);
                writer.WriteString(entry.ContentHash);
                writer.WriteString(entry.OverridesJson);
            }
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(writer.ToArray())).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void DestroyDefinition(GameAsset asset)
        {
            if (asset == null)
                return;
            if (Application.isPlaying)
                Object.Destroy(asset);
            else
                Object.DestroyImmediate(asset);
        }
    }

    public class PrefabPreparedGameAssetOverride
    {
        public readonly string Id;
        public readonly GameAssetReference Source;
        public readonly GameAssetOverrides Overrides;
        public readonly GameAsset Asset;

        public PrefabPreparedGameAssetOverride(string id, GameAssetReference source, GameAssetOverrides overrides, GameAsset asset)
        {
            Id = id;
            Source = source;
            Overrides = overrides;
            Asset = asset;
        }
    }
}
