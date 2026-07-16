using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using UnityEngine;

namespace DingoGameObjectsCMS.View
{
    public class GameRuntimeComponentPalettePrefab : MonoBehaviour
    {
        private readonly Dictionary<uint, Type> _componentViewTypesById = new();
        private bool _cacheBuilt;
        private ulong _registryInitializationVersion;

        public bool TryGetComponentViewType(uint componentTypeId, out Type componentViewType)
        {
            EnsureCache();
            if (_componentViewTypesById.TryGetValue(componentTypeId, out componentViewType))
            {
                return true;
            }

            // Fast Script Reload and prefab reimport can preserve the cache flags while
            // replacing the palette components. A requested view that exists on the
            // current prefab is authoritative evidence that the retained cache is stale.
            if (!ContainsComponentView(componentTypeId))
            {
                return false;
            }

            RebuildCache();
            return _componentViewTypesById.TryGetValue(componentTypeId, out componentViewType);
        }

        private void EnsureCache()
        {
            if (!RuntimeComponentTypeRegistry.IsInitialized)
                throw new InvalidOperationException($"{nameof(GameRuntimeComponentPalettePrefab)} '{name}' cannot build its mapping before {nameof(RuntimeComponentTypeRegistry)} is initialized.");
            if (_cacheBuilt && _registryInitializationVersion == RuntimeComponentTypeRegistry.InitializationVersion)
            {
                return;
            }

            RebuildCache();
        }

        private bool ContainsComponentView(uint componentTypeId)
        {
            var componentViews = GetComponents<GameRuntimeComponentMonoBehaviour>();
            for (var i = 0; i < componentViews.Length; i++)
            {
                var componentView = componentViews[i];
                if (componentView != null
                    && RuntimeComponentTypeRegistry.TryGetId(componentView.ComponentType, out var mappedTypeId)
                    && mappedTypeId == componentTypeId)
                {
                    return true;
                }
            }

            return false;
        }

        private void RebuildCache()
        {
            var rebuilt = new Dictionary<uint, Type>();
            var componentViews = GetComponents<GameRuntimeComponentMonoBehaviour>();
            for (var i = 0; i < componentViews.Length; i++)
            {
                var componentView = componentViews[i];
                if (componentView == null)
                {
                    continue;
                }

                var componentType = componentView.ComponentType;
                if (!RuntimeComponentTypeRegistry.TryGetId(componentType, out var componentTypeId))
                {
                    throw new InvalidOperationException($"Component palette '{name}' contains '{componentView.GetType().FullName}' for unregistered runtime component '{componentType.FullName}'.");
                }
                if (rebuilt.TryGetValue(componentTypeId, out var existingViewType))
                {
                    throw new InvalidOperationException($"Component palette '{name}' maps runtime component '{componentType.FullName}' to both '{existingViewType.FullName}' and '{componentView.GetType().FullName}'.");
                }

                rebuilt.Add(componentTypeId, componentView.GetType());
            }

            _cacheBuilt = false;
            _componentViewTypesById.Clear();
            foreach (var pair in rebuilt)
            {
                _componentViewTypesById.Add(pair.Key, pair.Value);
            }

            _registryInitializationVersion = RuntimeComponentTypeRegistry.InitializationVersion;
            _cacheBuilt = true;
        }
    }
}
