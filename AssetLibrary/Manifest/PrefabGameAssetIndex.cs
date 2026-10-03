using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.AssetObjects;
using DingoUnityExtensions.MonoBehaviours.Singletons;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public class PrefabGameAssetIndex : SingletonScriptableObject<PrefabGameAssetIndex>
    {
        [SerializeField] private List<PrefabGameAssetIndexEntry> _entries = new();
        [SerializeField] private List<PrefabGameAssetOverrideEntry> _overrides = new();

        public IReadOnlyList<PrefabGameAssetIndexEntry> Entries => _entries;
        public IReadOnlyList<PrefabGameAssetOverrideEntry> Overrides => _overrides;

        public bool ReplaceOverrides(IEnumerable<PrefabGameAssetOverrideEntry> overrides)
        {
            var next = overrides != null ? new List<PrefabGameAssetOverrideEntry>(overrides) : new List<PrefabGameAssetOverrideEntry>();
            next.Sort((left, right) => string.Compare(left?.Id, right?.Id, StringComparison.Ordinal));
            if (_overrides != null && _overrides.Count == next.Count)
            {
                var equal = true;
                for (var index = 0; index < next.Count; index++)
                {
                    var current = _overrides[index];
                    var replacement = next[index];
                    if (current?.Id != replacement?.Id || current?.Source != replacement?.Source || current?.ContentHash != replacement?.ContentHash || current?.OverridesJson != replacement?.OverridesJson)
                    {
                        equal = false;
                        break;
                    }
                }
                if (equal)
                    return false;
            }
            _overrides = next;
            return true;
        }

        public bool ReplaceEntries(IEnumerable<PrefabGameAssetIndexEntry> entries)
        {
            var next = entries != null ? new List<PrefabGameAssetIndexEntry>(entries) : new List<PrefabGameAssetIndexEntry>();
            next.Sort((left, right) => string.Compare(left?.GUID, right?.GUID, StringComparison.Ordinal));
            if (_entries != null && _entries.Count == next.Count)
            {
                var equal = true;
                for (var index = 0; index < next.Count; index++)
                {
                    if (_entries[index]?.GUID != next[index]?.GUID || _entries[index]?.Authoring != next[index]?.Authoring || _entries[index]?.ContentHash != next[index]?.ContentHash)
                    {
                        equal = false;
                        break;
                    }
                }
                if (equal)
                    return false;
            }
            _entries = next;
            return true;
        }
    }

    [Serializable, Preserve]
    public class PrefabGameAssetOverrideEntry
    {
        [SerializeField] private string _id;
        [SerializeField] private GameAssetAuthoring _source;
        [SerializeReference] private List<GameAssetComponent> _components = new();
        [SerializeField] private string _overridesJson;
        [SerializeField] private string _contentHash;

        public string Id => _id;
        public GameAssetAuthoring Source => _source;
        public IReadOnlyList<GameAssetComponent> Components => _components;
        public string OverridesJson => _overridesJson;
        public string ContentHash => _contentHash;

        public PrefabGameAssetOverrideEntry(string id, GameAssetAuthoring source, IEnumerable<GameAssetComponent> components, string overridesJson, string contentHash)
        {
            _id = id;
            _source = source;
            _components = components != null ? new List<GameAssetComponent>(components) : new List<GameAssetComponent>();
            _overridesJson = overridesJson;
            _contentHash = contentHash;
        }
    }

    [Serializable, Preserve]
    public class PrefabGameAssetIndexEntry
    {
        [SerializeField] private string _guid;
        [SerializeField] private GameAssetAuthoring _authoring;
        [SerializeField] private string _contentHash;

        public string GUID => _guid;
        public GameAssetAuthoring Authoring => _authoring;
        public string ContentHash => _contentHash;

        public PrefabGameAssetIndexEntry(string guid, GameAssetAuthoring authoring, string contentHash = null)
        {
            _guid = guid?.Trim().ToLowerInvariant();
            _authoring = authoring;
            _contentHash = contentHash;
        }
    }
}
