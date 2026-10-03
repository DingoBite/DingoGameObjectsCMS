using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoUnityExtensions.MonoBehaviours.Singletons;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public class PrefabGameAssetIndex : SingletonScriptableObject<PrefabGameAssetIndex>
    {
        [SerializeField] private List<PrefabGameAssetIndexEntry> _entries = new();

        public IReadOnlyList<PrefabGameAssetIndexEntry> Entries => _entries;

        public bool ReplaceEntries(IEnumerable<PrefabGameAssetIndexEntry> entries)
        {
            var next = entries != null ? new List<PrefabGameAssetIndexEntry>(entries) : new List<PrefabGameAssetIndexEntry>();
            next.Sort((left, right) => string.Compare(left?.GUID, right?.GUID, StringComparison.Ordinal));
            if (_entries != null && _entries.Count == next.Count)
            {
                var equal = true;
                for (var index = 0; index < next.Count; index++)
                {
                    if (_entries[index]?.GUID != next[index]?.GUID || _entries[index]?.Authoring != next[index]?.Authoring)
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
    public class PrefabGameAssetIndexEntry
    {
        [SerializeField] private string _guid;
        [SerializeField] private GameAssetAuthoring _authoring;

        public string GUID => _guid;
        public GameAssetAuthoring Authoring => _authoring;

        public PrefabGameAssetIndexEntry(string guid, GameAssetAuthoring authoring)
        {
            _guid = guid?.Trim().ToLowerInvariant();
            _authoring = authoring;
        }
    }
}
