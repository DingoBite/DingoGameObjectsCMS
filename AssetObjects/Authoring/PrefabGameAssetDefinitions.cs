using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
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
        private bool _disposed;

        public bool IsDisposed => _disposed;

        private PrefabGameAssetDefinitions() {}

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
                }
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
            resolved = default;
            return !_disposed && reference.Authoring != null && _references.TryGetValue(reference.Authoring, out resolved);
        }

        public bool TryGetAsset(PrefabGameAssetReference reference, out GameAsset asset)
        {
            asset = null;
            return !_disposed && reference.Authoring != null && _assets.TryGetValue(reference.Authoring, out asset);
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
}
