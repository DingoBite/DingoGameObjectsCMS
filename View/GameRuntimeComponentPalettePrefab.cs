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

        public bool TryGetComponentViewType(uint componentTypeId, out Type componentViewType)
        {
            EnsureCache();
            return _componentViewTypesById.TryGetValue(componentTypeId, out componentViewType);
        }

        private void EnsureCache()
        {
            if (_cacheBuilt)
                return;
            if (!RuntimeComponentTypeRegistry.IsInitialized)
                throw new InvalidOperationException($"{nameof(GameRuntimeComponentPalettePrefab)} '{name}' cannot build its mapping before {nameof(RuntimeComponentTypeRegistry)} is initialized.");

            _componentViewTypesById.Clear();
            var componentViews = GetComponents<GameRuntimeComponentMonoBehaviour>();
            for (var i = 0; i < componentViews.Length; i++)
            {
                var componentView = componentViews[i];
                if (componentView == null)
                    continue;

                var componentType = componentView.ComponentType;
                if (!RuntimeComponentTypeRegistry.TryGetId(componentType, out var componentTypeId))
                    throw new InvalidOperationException($"Component palette '{name}' contains '{componentView.GetType().FullName}' for unregistered runtime component '{componentType.FullName}'.");
                if (_componentViewTypesById.TryGetValue(componentTypeId, out var existingViewType))
                    throw new InvalidOperationException($"Component palette '{name}' maps runtime component '{componentType.FullName}' to both '{existingViewType.FullName}' and '{componentView.GetType().FullName}'.");

                _componentViewTypesById.Add(componentTypeId, componentView.GetType());
            }

            _cacheBuilt = true;
        }
    }
}
