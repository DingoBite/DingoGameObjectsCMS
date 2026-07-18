using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoUnityExtensions;
using DingoUnityExtensions.Pools.Core;
using DingoUnityExtensions.UnityViewProviders.Pools;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.View
{
    public class GameRuntimeObjectsDirtyCollection : MonoBehaviour
    {
        [SerializeField] private GameObject _parent;
        [SerializeField] private GameRuntimeObjectOperationView _prefab;
        [SerializeField] private bool _fullRebuildOnChange;
        [SerializeField] private CollectionViewSpawnOptions _defaultSpawnOptions;
        [SerializeField] private SortTransformOrderOption _sortTransformOrder = SortTransformOrderOption.AsLast;

        private Pool<GameRuntimeObjectOperationView> _pool;
        private readonly Dictionary<long, ActiveEntry> _activeEntries = new();
        private readonly List<long> _orderedKeys = new();
        private readonly List<long> _sourceKeys = new();
        private readonly List<long> _removedKeys = new();
        private RuntimeStore _store;
        private RuntimeObjectCollectionScope _scope = RuntimeObjectCollectionScope.Parents;
        private int _entryVersion;
        private int _scheduledOrderApplyVersion;
        private bool _orderApplyScheduled;

        public RuntimeStore Store => _store;
        public RuntimeObjectCollectionScope Scope => _scope;
        public bool IsBound => _store != null;

        public IEnumerable<GameRuntimeObjectOperationView> GetOrderedViews()
        {
            for (var i = 0; i < _orderedKeys.Count; i++)
            {
                var key = _orderedKeys[i];
                if (_activeEntries.TryGetValue(key, out var activeEntry) && activeEntry.Container != null)
                    yield return activeEntry.Container;
            }
        }

        public IEnumerable<TView> GetOrderedViews<TView>() where TView : GameRuntimeObjectOperationView
        {
            for (var i = 0; i < _orderedKeys.Count; i++)
            {
                var key = _orderedKeys[i];
                if (!_activeEntries.TryGetValue(key, out var activeEntry))
                    continue;
                if (activeEntry.Container is TView typedView)
                    yield return typedView;
            }
        }

        public IEnumerable<TInterface> GetOrderedViewInterfaces<TInterface>()
        {
            for (var i = 0; i < _orderedKeys.Count; i++)
            {
                var key = _orderedKeys[i];
                if (!_activeEntries.TryGetValue(key, out var activeEntry))
                    continue;
                if (activeEntry.Container is TInterface typedView)
                    yield return typedView;
            }
        }

        public void ConfigureRuntime(GameObject parent, GameRuntimeObjectOperationView prefab)
        {
            if (_pool != null || _store != null)
                throw new InvalidOperationException($"{nameof(GameRuntimeObjectsDirtyCollection)} on '{name}' cannot be reconfigured after initialization.");

            _parent = parent != null
                ? parent
                : throw new ArgumentNullException(nameof(parent));
            _prefab = prefab != null
                ? prefab
                : throw new ArgumentNullException(nameof(prefab));
        }

        public void Bind(RuntimeStore store) => Bind(store, RuntimeObjectCollectionScope.Parents);

        public void Bind(RuntimeStore store, RuntimeObjectCollectionScope scope)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));

            scope.Validate();
            EnsurePool();

            if (ReferenceEquals(_store, store) && _scope.SameAs(scope))
                return;

            Unbind();
            _store = store;
            _scope = scope;
            _store.StructureChanges += ApplyStructureChanges;
            _store.ComponentStructureChanges += ApplyComponentStructureChanges;
            _store.ComponentChanges += ApplyComponentChanges;
            ResetFromStore(ResolveSpawnOptions(CollectionViewSpawnOptions.Default));
        }

        public void RefreshFromStore() => RefreshFromStore(CollectionViewSpawnOptions.Default);

        public void RefreshFromStore(CollectionViewSpawnOptions spawnOptions)
        {
            EnsurePool();
            ResetFromStore(ResolveSpawnOptions(spawnOptions));
        }

        public void Unbind()
        {
            CancelScheduledActiveOrderApply();
            if (_store == null)
                return;

            var previousStore = _store;
            _store.StructureChanges -= ApplyStructureChanges;
            _store.ComponentStructureChanges -= ApplyComponentStructureChanges;
            _store.ComponentChanges -= ApplyComponentChanges;
            _store = null;
            _scope = RuntimeObjectCollectionScope.Parents;
            ReleaseAll(previousStore, ResolveSpawnOptions(CollectionViewSpawnOptions.ImmediateFill));
        }

        public void Clear() => Clear(CollectionViewSpawnOptions.ImmediateFill);

        public void Clear(CollectionViewSpawnOptions spawnOptions)
        {
            EnsurePool();
            CancelScheduledActiveOrderApply();
            ReleaseAll(_store, ResolveSpawnOptions(spawnOptions));
        }

        protected virtual Pool<GameRuntimeObjectOperationView> Factory(GameRuntimeObjectOperationView prefab, GameObject parent) =>
            new(prefab, parent, _sortTransformOrder);

        protected virtual bool ShouldInclude(long key, GameRuntimeObject value) =>
            value != null && key != RuntimeStore.STORE_ROOT_OBJECT_ID;

        protected virtual int CompareKeys(long left, long right) => left.CompareTo(right);

        protected virtual void SortKeys(List<long> keys) => keys.Sort(CompareKeys);

        protected virtual void ApplyOperation(GameRuntimeObjectOperationView valueContainer, GameRuntimeObjectOperation operation) =>
            valueContainer.UpdateValueWithoutNotify(operation);

        protected virtual UniTask OnAfterPullAsync(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer, CollectionViewSpawnOptions spawnOptions) =>
            valueContainer != null ? valueContainer.SpawnAsync(spawnOptions) : UniTask.CompletedTask;

        protected virtual UniTask OnBeforePushAsync(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer, CollectionViewSpawnOptions spawnOptions) =>
            valueContainer != null ? valueContainer.DespawnAsync(spawnOptions) : UniTask.CompletedTask;

        protected virtual void OnBeginRelease(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer) =>
            valueContainer.transform.SetAsLastSibling();

        private void OnDestroy()
        {
            Unbind();
        }

        private void EnsurePool()
        {
            if (_pool != null)
                return;
            if (_parent == null)
                throw new InvalidOperationException($"{nameof(GameRuntimeObjectsDirtyCollection)} on '{name}' requires a parent.");
            if (_prefab == null)
                throw new InvalidOperationException($"{nameof(GameRuntimeObjectsDirtyCollection)} on '{name}' requires a prefab.");

            _pool = Factory(_prefab, _parent);
        }

        private void ResetFromStore(CollectionViewSpawnOptions spawnOptions)
        {
            ReleaseAll(_store, spawnOptions);
            if (_store == null)
                return;

            _sourceKeys.Clear();
            CollectSourceKeys(_sourceKeys);

            for (var i = 0; i < _sourceKeys.Count; i++)
            {
                var key = _sourceKeys[i];
                if (!TryTakeScopedValue(key, out var value))
                    continue;

                UpsertEntry(key, GameRuntimeObjectOperation.Snapshot(_store, key, null, value), spawnOptions);
            }

            _sourceKeys.Clear();
            SyncActiveOrder();
        }

        private void ApplyStructureChanges(NativeArray<RuntimeStructureDirty> changes)
        {
            if (_store == null || changes.Length == 0)
                return;

            var spawnOptions = ResolveSpawnOptions(CollectionViewSpawnOptions.Default);
            if (spawnOptions.FullRebuild)
            {
                ResetFromStore(spawnOptions);
                return;
            }

            var orderDirty = false;
            for (var i = 0; i < changes.Length; i++)
            {
                if (ApplyStructureChange(changes[i], spawnOptions))
                    orderDirty = true;
            }

            if (orderDirty)
                SyncActiveOrder();
        }

        private void ApplyComponentStructureChanges(NativeArray<ObjectStructDirty> changes)
        {
            if (_store == null || changes.Length == 0)
                return;

            var spawnOptions = ResolveSpawnOptions(CollectionViewSpawnOptions.Default);
            var orderDirty = false;
            for (var i = 0; i < changes.Length; i++)
            {
                var dirty = changes[i];
                var hasActiveEntry = _activeEntries.TryGetValue(dirty.Id, out var activeEntry);
                var isIncluded = TryTakeScopedValue(dirty.Id, out var value);

                if (hasActiveEntry && !isIncluded)
                {
                    ReleaseKey(_store, dirty.Id, spawnOptions);
                    orderDirty = true;
                    continue;
                }
                if (!hasActiveEntry && isIncluded)
                {
                    UpsertEntry(
                        dirty.Id,
                        GameRuntimeObjectOperation.ComponentStructure(_store, dirty, null, value),
                        spawnOptions);
                    orderDirty = true;
                    continue;
                }
                if (!hasActiveEntry)
                    continue;

                var operation = GameRuntimeObjectOperation.ComponentStructure(_store, dirty, activeEntry.Value, value);
                activeEntry.Value = value;
                ApplyOperation(activeEntry.Container, operation);
            }

            if (orderDirty)
                SyncActiveOrder();
        }

        private void ApplyComponentChanges(NativeArray<ObjectComponentDirty> changes)
        {
            if (_store == null || changes.Length == 0 || _activeEntries.Count == 0)
                return;

            for (var i = 0; i < changes.Length; i++)
            {
                var dirty = changes[i];
                if (!_activeEntries.TryGetValue(dirty.Id, out var activeEntry))
                    continue;
                if (!TryTakeScopedValue(dirty.Id, out var value))
                    continue;

                var operation = GameRuntimeObjectOperation.Component(_store, dirty, activeEntry.Value, value);
                activeEntry.Value = value;
                ApplyOperation(activeEntry.Container, operation);
            }
        }

        private bool ApplyStructureChange(RuntimeStructureDirty dirty, CollectionViewSpawnOptions spawnOptions)
        {
            if (dirty.Id == RuntimeStore.STORE_ROOT_OBJECT_ID)
                return false;

            if (dirty.Kind == RuntimeStoreOpKind.Remove)
            {
                if (!_activeEntries.ContainsKey(dirty.Id))
                    return false;

                ReleaseKey(_store, dirty.Id, spawnOptions);
                return true;
            }

            if (!TryTakeScopedValue(dirty.Id, out var value))
            {
                if (!_activeEntries.ContainsKey(dirty.Id))
                    return false;

                ReleaseKey(_store, dirty.Id, spawnOptions);
                return true;
            }

            var previousValue = _activeEntries.TryGetValue(dirty.Id, out var activeEntry) ? activeEntry.Value : null;
            UpsertEntry(dirty.Id, GameRuntimeObjectOperation.Structure(_store, dirty, previousValue, value), spawnOptions);
            return true;
        }

        private void CollectSourceKeys(List<long> target)
        {
            switch (_scope.Kind)
            {
                case RuntimeObjectCollectionScopeKind.Parents:
                    foreach (var pair in _store.Parents.V)
                    {
                        if (ShouldInclude(pair.Key, pair.Value))
                            target.Add(pair.Key);
                    }

                    SortKeys(target);
                    break;

                case RuntimeObjectCollectionScopeKind.DirectChildren:
                    if (!_store.TryTakeChildren(_scope.ParentId, out var children))
                        return;

                    for (var i = 0; i < children.Count; i++)
                    {
                        var childId = children[i];
                        if (_store.TryTakeRO(childId, out var value) && ShouldInclude(childId, value))
                            target.Add(childId);
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(_scope), _scope.Kind, null);
            }
        }

        private bool TryTakeScopedValue(long id, out GameRuntimeObject value)
        {
            value = null;
            if (_store == null)
                return false;

            switch (_scope.Kind)
            {
                case RuntimeObjectCollectionScopeKind.Parents:
                    if (!_store.Parents.V.TryGetValue(id, out value))
                        return false;
                    break;

                case RuntimeObjectCollectionScopeKind.DirectChildren:
                    if (!_store.TryTakeParentRO(id, out var parent) || parent == null || parent.InstanceId != _scope.ParentId)
                        return false;
                    if (!_store.TryTakeRO(id, out value))
                        return false;
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(_scope), _scope.Kind, null);
            }

            return ShouldInclude(id, value);
        }

        private void UpsertEntry(long key, GameRuntimeObjectOperation operation, CollectionViewSpawnOptions spawnOptions)
        {
            if (_activeEntries.TryGetValue(key, out var activeEntry))
            {
                activeEntry.Value = operation.Value;
                ApplyOperation(activeEntry.Container, operation);
                return;
            }

            var valueContainer = _pool.PullElement();
            ApplyOperation(valueContainer, operation);

            var version = ++_entryVersion;
            _activeEntries[key] = new ActiveEntry
            {
                Version = version,
                Value = operation.Value,
                Container = valueContainer,
            };

            _ = FinalizePullAsync(key, version, operation.Value, valueContainer, spawnOptions);
        }

        private void SyncActiveOrder()
        {
            CancelScheduledActiveOrderApply();
            _orderedKeys.Clear();

            switch (_scope.Kind)
            {
                case RuntimeObjectCollectionScopeKind.Parents:
                    _sourceKeys.Clear();
                    foreach (var key in _activeEntries.Keys)
                    {
                        _sourceKeys.Add(key);
                    }

                    SortKeys(_sourceKeys);
                    _orderedKeys.AddRange(_sourceKeys);
                    _sourceKeys.Clear();
                    break;

                case RuntimeObjectCollectionScopeKind.DirectChildren:
                    if (_store != null && _store.TryTakeChildren(_scope.ParentId, out var children))
                    {
                        for (var i = 0; i < children.Count; i++)
                        {
                            var childId = children[i];
                            if (_activeEntries.ContainsKey(childId))
                                _orderedKeys.Add(childId);
                        }
                    }

                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(_scope), _scope.Kind, null);
            }

            ApplyActiveOrder();
        }

        private void ReleaseAll(RuntimeStore store, CollectionViewSpawnOptions spawnOptions)
        {
            _orderedKeys.Clear();
            if (_activeEntries.Count == 0)
                return;

            _removedKeys.Clear();
            foreach (var key in _activeEntries.Keys)
            {
                _removedKeys.Add(key);
            }

            for (var i = 0; i < _removedKeys.Count; i++)
            {
                ReleaseKey(store, _removedKeys[i], spawnOptions);
            }

            _removedKeys.Clear();
        }

        private void ReleaseKey(RuntimeStore store, long key, CollectionViewSpawnOptions spawnOptions)
        {
            RemoveOrderedKey(key);

            if (!_activeEntries.Remove(key, out var activeEntry))
                return;

            ApplyOperation(activeEntry.Container, GameRuntimeObjectOperation.Release(store, key, activeEntry.Value));
            BeginRelease(key, activeEntry.Value, activeEntry.Container, spawnOptions);
        }

        private void BeginRelease(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer, CollectionViewSpawnOptions spawnOptions)
        {
            if (valueContainer == null)
                return;

            OnBeginRelease(key, value, valueContainer);
            _ = ReleaseEntryAsync(key, value, valueContainer, spawnOptions);
        }

        private async UniTask FinalizePullAsync(long key, int version, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer, CollectionViewSpawnOptions spawnOptions)
        {
            try
            {
                await OnAfterPullAsync(key, value, valueContainer, spawnOptions);

                if (this == null)
                    return;
                if (!_activeEntries.TryGetValue(key, out var activeEntry))
                    return;
                if (activeEntry.Version != version)
                    return;
                if (!ReferenceEquals(activeEntry.Container, valueContainer))
                    return;

                ScheduleActiveOrderApply();
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
        }

        private async UniTask ReleaseEntryAsync(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer, CollectionViewSpawnOptions spawnOptions)
        {
            try
            {
                await OnBeforePushAsync(key, value, valueContainer, spawnOptions);
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            if (this == null || valueContainer == null)
                return;

            _pool.PushElement(valueContainer);
        }

        private void RemoveOrderedKey(long key)
        {
            var currentIndex = _orderedKeys.IndexOf(key);
            if (currentIndex >= 0)
                _orderedKeys.RemoveAt(currentIndex);
        }

        protected virtual void ApplyActiveOrder()
        {
            var siblingIndex = 0;
            for (var i = 0; i < _orderedKeys.Count; i++)
            {
                var key = _orderedKeys[i];
                if (!_activeEntries.TryGetValue(key, out var activeEntry) || activeEntry.Container == null)
                    continue;

                activeEntry.Container.transform.SetSiblingIndex(siblingIndex);
                siblingIndex++;
            }
        }

        private void ScheduleActiveOrderApply()
        {
            if (_orderApplyScheduled)
                return;
            if (CoroutineParent.GetNoCheck() == null)
            {
                ApplyActiveOrder();
                return;
            }

            _orderApplyScheduled = true;
            var version = ++_scheduledOrderApplyVersion;
            CoroutineParent.AddSingleLateUpdate(() => ApplyScheduledActiveOrder(version));
        }

        private void ApplyScheduledActiveOrder(int version)
        {
            if (this == null || !_orderApplyScheduled || version != _scheduledOrderApplyVersion)
                return;

            _orderApplyScheduled = false;
            ApplyActiveOrder();
        }

        private void CancelScheduledActiveOrderApply()
        {
            if (!_orderApplyScheduled)
                return;

            _orderApplyScheduled = false;
            _scheduledOrderApplyVersion++;
        }

        private CollectionViewSpawnOptions ResolveSpawnOptions(CollectionViewSpawnOptions spawnOptions)
        {
            var useDefaultOptions = spawnOptions.Equals(default(CollectionViewSpawnOptions));
            var sourceOptions = useDefaultOptions ? _defaultSpawnOptions : spawnOptions;
            return new CollectionViewSpawnOptions(immediate: sourceOptions.Immediate, fullRebuild: _fullRebuildOnChange || sourceOptions.FullRebuild);
        }

        private class ActiveEntry
        {
            public int Version;
            public GameRuntimeObject Value;
            public GameRuntimeObjectOperationView Container;
        }
    }
}
