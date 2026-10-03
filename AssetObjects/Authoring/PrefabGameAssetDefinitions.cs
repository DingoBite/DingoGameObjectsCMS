using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
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
        private readonly Dictionary<GameAssetAuthoring, GameAssetReference> _references = new();
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
                    {
                        error = $"Prefab '{entry.Authoring.name}' ({entry.GUID}): {collectionError}";
                        return false;
                    }

                    var key = new GameAssetKey(MOD, TYPE, entry.GUID, VERSION);
                    var asset = CloneDefinition(entry.Authoring, collection.Components, key, guid);
                    prepared._assets.Add(entry.Authoring, asset);
                    prepared._references.Add(entry.Authoring, new GameAssetReference(key));
                    prepared._sourcesByGuid.Add(entry.GUID, entry.Authoring);
                }
                var overrideIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in index.Overrides ?? Array.Empty<PrefabGameAssetOverrideEntry>())
                {
                    if (entry == null || string.IsNullOrWhiteSpace(entry.Id) || !overrideIds.Add(entry.Id) || entry.Source == null || !prepared._assets.TryGetValue(entry.Source, out var source) || entry.Components == null)
                    {
                        error = "A prefab GameAsset override requires a unique instance id, an indexed source prefab and effective components.";
                        return false;
                    }
                    var types = new HashSet<Type>(source.Components.Select(component => component.GetType()));
                    var effectiveTypes = new HashSet<Type>();
                    foreach (var component in entry.Components)
                    {
                        if (component == null || !effectiveTypes.Add(component.GetType()))
                        {
                            error = $"Prefab instance override '{entry.Id}' contains a null or repeated GAC type.";
                            return false;
                        }
                    }
                    if (!types.SetEquals(effectiveTypes))
                    {
                        error = $"Prefab instance override '{entry.Id}' changes the source GAC composition.";
                        return false;
                    }
                    var overrides = JsonConvert.DeserializeObject<GameAssetOverrides>(entry.OverridesJson, GameAssetJson.DataSettings);
                    if (overrides?.HasAny != true)
                    {
                        error = $"Prefab instance override '{entry.Id}' has no effective value overrides.";
                        return false;
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
            _references.Clear();
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
