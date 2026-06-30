using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using UnityEngine;

namespace DingoGameObjectsCMS.View
{
    public abstract class GameRuntimeObjectByComponentsView : GameRuntimeObjectOperationView
    {
        [SerializeField] private GameRuntimeComponentPalettePrefab _componentPalettePrefab;
        [SerializeField] private bool _useFirstPaletteComponentOnMissingType;

        private readonly Dictionary<uint, GameRuntimeComponentMonoBehaviour> _componentViewsById = new();
        private readonly List<uint> _componentIdsBuffer = new();

        protected override void OnGRODestroy(GameRuntimeObject value, GameRuntimeObjectOperation operation)
        {
            ClearComponents();
            base.OnGRODestroy(value, operation);
        }

        protected override void OnGROSnapshot(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation)
        {
            SyncComponents(value);
            base.OnGROSnapshot(previousValue, value, operation);
        }

        protected override void OnGROStructure(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation)
        {
            SyncComponents(value);
            base.OnGROStructure(previousValue, value, operation);
        }

        protected override void OnGROComponentStructure(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation)
        {
            var dirty = operation.ComponentStructureDirty.Dirty;
            switch (dirty.Kind)
            {
                case CompStructOpKind.Add:
                    AddOrUpdateComponent(value, dirty.CompTypeId);
                    break;
                case CompStructOpKind.Remove:
                    RemoveComponent(dirty.CompTypeId);
                    break;
                default: throw new ArgumentOutOfRangeException();
            }

            base.OnGROComponentStructure(previousValue, value, operation);
        }

        protected override void OnGROComponent(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation)
        {
            UpdateComponent(value, operation.ComponentDirty.Dirty.CompTypeId);
            base.OnGROComponent(previousValue, value, operation);
        }

        protected override void OnGRORelease(GameRuntimeObject previousValue, GameRuntimeObjectOperation operation)
        {
            ClearComponents();
            base.OnGRORelease(previousValue, operation);
        }

        private void SyncComponents(GameRuntimeObject gro)
        {
            if (gro == null)
            {
                ClearComponents();
                return;
            }

            _componentIdsBuffer.Clear();
            foreach (var pair in _componentViewsById)
            {
                _componentIdsBuffer.Add(pair.Key);
            }

            var components = gro.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;

                var type = component.GetType();
                if (!RuntimeComponentTypeRegistry.TryGetId(type, out var compTypeId))
                {
                    Debug.LogError($"GameRuntimeObject {gro.InstanceId} contains runtime component '{type.FullName}' that is not present in RuntimeComponentTypeRegistry.", this);
                    continue;
                }

                _componentIdsBuffer.Remove(compTypeId);
                AddOrUpdateComponent(gro, compTypeId);
            }

            for (var i = 0; i < _componentIdsBuffer.Count; i++)
            {
                RemoveComponent(_componentIdsBuffer[i]);
            }

            _componentIdsBuffer.Clear();
        }

        private void AddOrUpdateComponent(GameRuntimeObject gro, uint compTypeId)
        {
            if (_componentViewsById.ContainsKey(compTypeId))
            {
                UpdateComponent(gro, compTypeId);
                return;
            }

            AddComponent(gro, compTypeId);
        }

        private void AddComponent(GameRuntimeObject gro, uint compTypeId)
        {
            if (!RuntimeComponentTypeRegistry.TryGetType(compTypeId, out var componentType) || componentType == null)
            {
                Debug.LogError($"Cannot create runtime component view for unknown runtime component type id {compTypeId}.", this);
                return;
            }

            var componentViewType = ResolveComponentViewType(componentType);
            if (componentViewType == null)
                return;

            var componentView = gameObject.AddComponent(componentViewType) as GameRuntimeComponentMonoBehaviour;
            if (componentView == null)
            {
                Debug.LogError($"Component view type '{componentViewType.FullName}' is not a {nameof(GameRuntimeComponentMonoBehaviour)}.", this);
                return;
            }

            componentView.Attach(this);
            _componentViewsById[compTypeId] = componentView;
            UpdateComponentValue(componentView, gro, compTypeId);
        }

        private void UpdateComponent(GameRuntimeObject gro, uint compTypeId)
        {
            if (!_componentViewsById.TryGetValue(compTypeId, out var componentView) || componentView == null)
            {
                AddComponent(gro, compTypeId);
                return;
            }

            UpdateComponentValue(componentView, gro, compTypeId);
        }

        private void RemoveComponent(uint compTypeId)
        {
            if (!_componentViewsById.Remove(compTypeId, out var componentView) || componentView == null)
                return;

            componentView.Detach();
            Destroy(componentView);
        }

        private void ClearComponents()
        {
            if (_componentViewsById.Count == 0)
                return;

            _componentIdsBuffer.Clear();
            foreach (var pair in _componentViewsById)
            {
                _componentIdsBuffer.Add(pair.Key);
            }

            for (var i = 0; i < _componentIdsBuffer.Count; i++)
            {
                RemoveComponent(_componentIdsBuffer[i]);
            }

            _componentIdsBuffer.Clear();
        }

        private Type ResolveComponentViewType(Type componentType)
        {
            if (_componentPalettePrefab != null)
                return _componentPalettePrefab.GetComponentFor(componentType, _useFirstPaletteComponentOnMissingType, warnOnMissingType: true)?.GetType();

            return GameRuntimeComponentMonoBehaviourExtensions.TryGetComponentViewType(componentType, out var componentViewType) ? componentViewType : null;
        }

        private static void UpdateComponentValue(GameRuntimeComponentMonoBehaviour componentView, GameRuntimeObject gro, uint compTypeId)
        {
            var component = gro != null ? gro.GetById(compTypeId) : null;
            componentView.UpdateValueWithoutNotify(component);
        }
    }
}
