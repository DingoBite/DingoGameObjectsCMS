using System;
using System.Threading;
using System.Threading.Tasks;
using DingoECSUtils.DotsPrefabLibAccess;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using Unity.Entities.Serialization;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    /// <summary>Resolves technical GA descriptors from a captured catalog before synchronous ECS projection.</summary>
    public class BakedEntityPrefabPreparation
    {
        private readonly GameAssetTemplateCache _templates;
        private readonly GameAssetLibraryLock _catalog;
        private readonly BakedEntityPrefabLoader _loader;

        public BakedEntityPrefabPreparation(GameAssetTemplateCache templates, GameAssetLibraryLock catalog, BakedEntityPrefabLoader loader)
        {
            if (catalog == null || !catalog.IsReadOnly)
                throw new ArgumentException("Baked prefab preparation requires an immutable GA catalog.", nameof(catalog));
            _templates = templates ?? throw new ArgumentNullException(nameof(templates));
            _catalog = catalog;
            _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        }

        public Task<BakedEntityPrefabLease> PrepareAsync(GameAssetReference template, CancellationToken cancellationToken = default)
        {
            EntityPrefabReference prefab = default;
            try
            {
                var definition = _templates.Materialize(GameAssetInstanceFactory.Create(template), _catalog);
                var component = definition.TakeRO<BakedEntityPrefab_GRC>();
                if (component == null)
                    Debug.LogError($"Technical GA '{template.RequestedKey}' has no baked entity prefab descriptor. Using an inert projection.");
                else
                    prefab = component.Prefab;
            }
            catch (Exception exception)
            {
                Debug.LogError($"Technical GA '{template.RequestedKey}' could not resolve its baked entity prefab: {exception.Message}. Using an inert projection.");
            }
            return _loader.PrepareAsync(prefab, cancellationToken);
        }
    }
}
