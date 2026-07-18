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

        private readonly Dictionary<uint, GameRuntimeComponentMonoBehaviour> _createdComponentViewsById = new();
        private readonly HashSet<uint> _activeComponentIds = new();
        private readonly List<uint> _componentIdsBuffer = new();

        public bool IsDestroying { get; private set; }

        protected override void OnAwake()
        {
            if (_componentPalettePrefab == null)
                throw new InvalidOperationException($"{nameof(GameRuntimeObjectByComponentsView)} on '{name}' requires an explicit {nameof(GameRuntimeComponentPalettePrefab)} reference.");

            base.OnAwake();
        }

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
            foreach (var componentTypeId in _activeComponentIds)
            {
                _componentIdsBuffer.Add(componentTypeId);
            }

            var components = gro.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component == null)
                {
                    continue;
                }

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
            if (_activeComponentIds.Contains(compTypeId))
            {
                UpdateComponent(gro, compTypeId);
                return;
            }

            AddComponent(gro, compTypeId);
        }

        private void AddComponent(GameRuntimeObject gro, uint compTypeId)
        {
            if (!_componentPalettePrefab.TryGetComponentViewType(compTypeId, out var componentViewType))
                return;

            if (!_createdComponentViewsById.TryGetValue(compTypeId, out var componentView) || componentView == null)
            {
                componentView = gameObject.AddComponent(componentViewType) as GameRuntimeComponentMonoBehaviour;
                if (componentView == null)
                    throw new InvalidOperationException($"Component palette '{_componentPalettePrefab.name}' maps type id {compTypeId} to '{componentViewType.FullName}', which is not a {nameof(GameRuntimeComponentMonoBehaviour)}.");

                _createdComponentViewsById.Add(compTypeId, componentView);
            }

            componentView.enabled = true;
            componentView.Attach(this);
            _activeComponentIds.Add(compTypeId);
            UpdateComponentValue(componentView, gro, compTypeId);
        }

        private void UpdateComponent(GameRuntimeObject gro, uint compTypeId)
        {
            if (!_activeComponentIds.Contains(compTypeId) || !_createdComponentViewsById.TryGetValue(compTypeId, out var componentView) || componentView == null)
            {
                AddComponent(gro, compTypeId);
                return;
            }

            UpdateComponentValue(componentView, gro, compTypeId);
        }

        private void RemoveComponent(uint compTypeId)
        {
            if (!_activeComponentIds.Remove(compTypeId))
                return;
            if (!_createdComponentViewsById.TryGetValue(compTypeId, out var componentView) || componentView == null)
                throw new InvalidOperationException($"Active component view type id {compTypeId} has no created view on '{name}'.");

            componentView.Detach();
            componentView.enabled = false;
        }

        private void ClearComponents()
        {
            if (_activeComponentIds.Count == 0)
                return;

            _componentIdsBuffer.Clear();
            foreach (var componentTypeId in _activeComponentIds)
            {
                _componentIdsBuffer.Add(componentTypeId);
            }

            for (var i = 0; i < _componentIdsBuffer.Count; i++)
            {
                RemoveComponent(_componentIdsBuffer[i]);
            }

            _componentIdsBuffer.Clear();
        }

        protected virtual void OnDestroy()
        {
            IsDestroying = true;
            ClearComponents();
        }

        private static void UpdateComponentValue(GameRuntimeComponentMonoBehaviour componentView, GameRuntimeObject gro, uint compTypeId)
        {
            var component = gro != null ? gro.GetById(compTypeId) : null;
            componentView.UpdateValueWithoutNotify(component);
        }
    }
}
