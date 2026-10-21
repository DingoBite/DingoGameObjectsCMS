using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;
using Unity.Entities;
using Unity.Entities.Serialization;
using UnityEngine;
using EntityHash128 = Unity.Entities.Hash128;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    [DisallowMultipleComponent, AddComponentMenu("Dingo CMS/Baked Entity Prefab Library")]
    public class BakedEntityPrefabLibraryAuthoring : MonoBehaviour
    {
        [SerializeField] private PrefabGameAssetIndex _index;

        public PrefabGameAssetIndex Index => _index;

        public void SetIndex(PrefabGameAssetIndex index) => _index = index;
    }

    [RuntimeDotsDerived]
    public struct BakedEntityPrefabLibraryEntry : IBufferElementData
    {
        public EntityHash128 DefinitionGuid;
        public EntityPrefabReference Prefab;
    }

    public static class BakedEntityPrefabLibrary
    {
        public static IReadOnlyList<BakedEntityPrefabLibraryEntry> GetReferences(PrefabGameAssetIndex index)
        {
            var references = new List<BakedEntityPrefabLibraryEntry>();
            if (index == null)
                return references;
            foreach (var entry in index.Entries)
            {
                if (!GameAssetAuthoringCollector.TryCollect(entry.Authoring, out var collection, out var error))
                {
                    Debug.LogWarning($"[Baked Entity Prefab] Native definition '{entry.GUID}': {error} Its baked template is unavailable.", index);
                    continue;
                }
                foreach (var component in collection.Components)
                {
                    if (component is not BakedEntityPrefab_GAC descriptor)
                        continue;
                    if (!descriptor.Prefab.IsReferenceValid)
                    {
                        Debug.LogWarning($"[Baked Entity Prefab] Native definition '{entry.GUID}' has an invalid baked prefab reference. Its baked template is unavailable.", index);
                        continue;
                    }
                    references.Add(new BakedEntityPrefabLibraryEntry { DefinitionGuid = new EntityHash128(entry.GUID), Prefab = descriptor.Prefab });
                }
            }
            return references.AsReadOnly();
        }
    }

    public class BakedEntityPrefabLibraryBaker : Baker<BakedEntityPrefabLibraryAuthoring>
    {
        public override void Bake(BakedEntityPrefabLibraryAuthoring authoring)
        {
            if (authoring.Index == null)
            {
                Debug.LogWarning("Baked Entity Prefab Library has no native GA index. No technical templates will be baked from this library.", authoring);
                return;
            }
            DependsOn(authoring.Index);
            var entity = GetEntity(TransformUsageFlags.None);
            var references = AddBuffer<BakedEntityPrefabLibraryEntry>(entity);
            foreach (var entry in authoring.Index.Entries)
            {
                DependsOn(entry.Authoring);
            }
            foreach (var reference in BakedEntityPrefabLibrary.GetReferences(authoring.Index))
            {
                references.Add(reference);
            }
        }
    }
}
