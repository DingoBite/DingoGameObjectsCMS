using System;
using System.Collections.Generic;
using System.Reflection;
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
        public const int RECONCILE_UPDATE_ORDER = RuntimeStore.UPDATE_ORDER - 1;

        [SerializeField] private GameObject _parent;
        [SerializeField] private GameRuntimeObjectOperationView _prefab;
        [SerializeField] private bool _fullRebuildOnChange;
        [SerializeField] private CollectionViewSpawnOptions _defaultSpawnOptions;
        [SerializeField] private SortTransformOrderOption _sortTransformOrder = SortTransformOrderOption.AsLast;

        private readonly Dictionary<GameRuntimeObjectOperationView, Pool<GameRuntimeObjectOperationView>> _poolsByPrefab = new();
        private readonly Dictionary<long, ActiveEntry> _activeEntries = new();
        private readonly List<long> _orderedKeys = new();
        private readonly List<long> _sourceKeys = new();
        private readonly List<long> _removedKeys = new();
        private RuntimeStore _store;
        private RuntimeObjectCollectionScope _scope = RuntimeObjectCollectionScope.Parents;
        private int _entryVersion;
        private int _scheduledOrderApplyVersion;
        private bool _orderApplyScheduled;
        private bool _orderDirty;
        private bool _fullRebuildPending;
        private bool _orderingPolicyInitialized;
        private bool _usesIncrementalParentOrdering;
        private bool _poolInitialized;

        public RuntimeStore Store => _store;
        public RuntimeObjectCollectionScope Scope => _scope;
        public bool IsBound => _store != null;
        public ulong TotalReconcileCount { get; private set; }
        public ulong TotalFullSortCount { get; private set; }
        protected virtual bool RequiresDefaultPrefab => true;

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

        public bool TryGetView(long key, out GameRuntimeObjectOperationView view)
        {
            view = null;
            if (!_activeEntries.TryGetValue(key, out var activeEntry) || activeEntry.Container == null)
                return false;

            view = activeEntry.Container;
            return true;
        }

        public bool TryGetView<TView>(long key, out TView view) where TView : GameRuntimeObjectOperationView
        {
            view = null;
            if (!TryGetView(key, out var activeView) || activeView is not TView typedView)
                return false;

            view = typedView;
            return true;
        }

        public void ConfigureRuntime(GameObject parent, GameRuntimeObjectOperationView prefab)
        {
            if (_poolInitialized || _store != null)
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
            Bind(store, scope, CollectionViewSpawnOptions.Default);
        }

        public void Bind(
            RuntimeStore store,
            RuntimeObjectCollectionScope scope,
            CollectionViewSpawnOptions spawnOptions)
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
            _store.DirtyPublishCompleted += ApplyDirtyPublishCompleted;
            ResetFromStore(ResolveSpawnOptions(spawnOptions));
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
            _store.DirtyPublishCompleted -= ApplyDirtyPublishCompleted;
            _store = null;
            _scope = RuntimeObjectCollectionScope.Parents;
            _orderDirty = false;
            _fullRebuildPending = false;
            ReleaseAll(previousStore, ResolveSpawnOptions(CollectionViewSpawnOptions.ImmediateFill));
        }

        public void Clear() => Clear(CollectionViewSpawnOptions.ImmediateFill);

        public void Clear(CollectionViewSpawnOptions spawnOptions)
        {
            EnsurePool();
            CancelScheduledActiveOrderApply();
            _orderDirty = false;
            _fullRebuildPending = false;
            ReleaseAll(_store, ResolveSpawnOptions(spawnOptions));
        }

        // Call when an external anchor changes without changing the runtime object.
        public void RefreshPlacement()
        {
            if (_store == null)
                return;

            var changed = false;
            foreach (var pair in _activeEntries)
            {
                var activeEntry = pair.Value;
                if (activeEntry.Container != null && PlaceEntry(pair.Key, activeEntry.Value, activeEntry.Container))
                    changed = true;
            }

            if (changed)
                ScheduleActiveOrderApply();
        }

        protected virtual Pool<GameRuntimeObjectOperationView> Factory(GameRuntimeObjectOperationView prefab, GameObject parent) =>
            new(prefab, parent, _sortTransformOrder);

        protected virtual bool ShouldInclude(long key, GameRuntimeObject value) =>
            value != null && key != RuntimeStore.STORE_ROOT_OBJECT_ID;

        // Prefab selection is fixed for each active entry until it is released or rebuilt.
        protected virtual GameRuntimeObjectOperationView ResolvePrefab(long key, GameRuntimeObject value) => _prefab;

        protected virtual Transform ResolveParent(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer) => _parent != null ? _parent.transform : null;

        protected virtual int CompareKeys(long left, long right) => left.CompareTo(right);

        protected virtual void SortKeys(List<long> keys) => keys.Sort(CompareKeys);

        protected virtual void ApplyOperation(GameRuntimeObjectOperationView valueContainer, GameRuntimeObjectOperation operation) =>
            valueContainer.UpdateValueWithoutNotify(operation);

        protected virtual UniTask OnAfterPullAsync(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer, CollectionViewSpawnOptions spawnOptions) =>
            valueContainer != null ? valueContainer.SpawnAsync(spawnOptions) : UniTask.CompletedTask;

        protected virtual UniTask OnBeforePushAsync(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer, CollectionViewSpawnOptions spawnOptions) =>
            valueContainer != null ? valueContainer.DespawnAsync(spawnOptions) : UniTask.CompletedTask;

        protected virtual void OnBeginRelease(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer)
        {
            var valueTransform = valueContainer.transform;
            var parent = valueTransform.parent;
            if (parent == null || _parent == null || parent != _parent.transform || !parent.gameObject.activeInHierarchy)
                return;
            if (valueTransform.GetSiblingIndex() == parent.childCount - 1)
                return;

            valueTransform.SetAsLastSibling();
        }

        private void OnDestroy()
        {
            Unbind();
        }

        private void EnsurePool()
        {
            EnsureOrderingPolicy();
            if (_poolInitialized)
                return;
            if (_parent == null)
                throw new InvalidOperationException($"{nameof(GameRuntimeObjectsDirtyCollection)} on '{name}' requires a parent.");
            if (_prefab == null && RequiresDefaultPrefab)
                throw new InvalidOperationException($"{nameof(GameRuntimeObjectsDirtyCollection)} on '{name}' requires a prefab.");

            if (_prefab != null)
            {
                var pool = Factory(_prefab, _parent);
                _poolsByPrefab.Add(_prefab, pool);
            }

            _poolInitialized = true;
        }

        private void ResetFromStore(CollectionViewSpawnOptions spawnOptions)
        {
            CancelScheduledActiveOrderApply();
            _orderDirty = false;
            _fullRebuildPending = false;
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

                if (UpsertEntry(key, GameRuntimeObjectOperation.Snapshot(_store, key, null, value), spawnOptions))
                    _orderedKeys.Add(key);
            }

            _sourceKeys.Clear();
            CancelScheduledActiveOrderApply();
            ApplyActiveOrder();
        }

        private void ApplyStructureChanges(NativeArray<RuntimeStructureDirty> changes)
        {
            if (_store == null || changes.Length == 0)
                return;

            var spawnOptions = ResolveSpawnOptions(CollectionViewSpawnOptions.Default);
            if (spawnOptions.FullRebuild)
            {
                _fullRebuildPending = true;
                _orderDirty = false;
                return;
            }

            for (var i = 0; i < changes.Length; i++)
            {
                if (ApplyStructureChange(changes[i], spawnOptions))
                    _orderDirty = true;
            }
        }

        private void ApplyComponentStructureChanges(NativeArray<ObjectStructDirty> changes)
        {
            if (_store == null || changes.Length == 0 || _fullRebuildPending)
                return;

            var spawnOptions = ResolveSpawnOptions(CollectionViewSpawnOptions.Default);
            for (var i = 0; i < changes.Length; i++)
            {
                var dirty = changes[i];
                var hasActiveEntry = _activeEntries.TryGetValue(dirty.Id, out var activeEntry);
                var isIncluded = TryTakeScopedValue(dirty.Id, out var value);

                if (hasActiveEntry && !isIncluded)
                {
                    ReleaseKey(_store, dirty.Id, spawnOptions);
                    _orderDirty = true;
                    continue;
                }
                if (!hasActiveEntry && isIncluded)
                {
                    if (UpsertEntry(dirty.Id, GameRuntimeObjectOperation.Snapshot(_store, dirty.Id, null, value), spawnOptions))
                    {
                        InsertOrderedKey(dirty.Id);
                        _orderDirty = true;
                    }
                    continue;
                }
                if (!hasActiveEntry)
                    continue;

                var operation = GameRuntimeObjectOperation.ComponentStructure(_store, dirty, activeEntry.Value, value);
                activeEntry.Value = value;
                if (PlaceEntry(dirty.Id, value, activeEntry.Container))
                    ScheduleActiveOrderApply();
                ApplyOperation(activeEntry.Container, operation);
            }
        }

        private void ApplyComponentChanges(NativeArray<ObjectComponentDirty> changes)
        {
            if (_store == null || changes.Length == 0 || _fullRebuildPending)
                return;

            var spawnOptions = ResolveSpawnOptions(CollectionViewSpawnOptions.Default);
            for (var i = 0; i < changes.Length; i++)
            {
                var dirty = changes[i];
                var hasActiveEntry = _activeEntries.TryGetValue(dirty.Id, out var activeEntry);
                var isIncluded = TryTakeScopedValue(dirty.Id, out var value);

                if (hasActiveEntry && !isIncluded)
                {
                    ReleaseKey(_store, dirty.Id, spawnOptions);
                    _orderDirty = true;
                    continue;
                }

                if (!hasActiveEntry && isIncluded)
                {
                    if (UpsertEntry(dirty.Id, GameRuntimeObjectOperation.Snapshot(_store, dirty.Id, null, value), spawnOptions))
                    {
                        InsertOrderedKey(dirty.Id);
                        _orderDirty = true;
                    }
                    continue;
                }

                if (!hasActiveEntry)
                    continue;

                var operation = GameRuntimeObjectOperation.Component(_store, dirty, activeEntry.Value, value);
                activeEntry.Value = value;
                if (PlaceEntry(dirty.Id, value, activeEntry.Container))
                    ScheduleActiveOrderApply();
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

            var wasActive = _activeEntries.TryGetValue(dirty.Id, out var activeEntry);
            var previousValue = wasActive ? activeEntry.Value : null;
            if (!UpsertEntry(dirty.Id, GameRuntimeObjectOperation.Structure(_store, dirty, previousValue, value), spawnOptions))
                return false;
            if (!wasActive)
            {
                InsertOrderedKey(dirty.Id);
                return true;
            }

            return _scope.Kind == RuntimeObjectCollectionScopeKind.DirectChildren;
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

                    TotalFullSortCount++;
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

        private bool UpsertEntry(long key, GameRuntimeObjectOperation operation, CollectionViewSpawnOptions spawnOptions)
        {
            if (_activeEntries.TryGetValue(key, out var activeEntry))
            {
                activeEntry.Value = operation.Value;
                if (PlaceEntry(key, operation.Value, activeEntry.Container))
                    ScheduleActiveOrderApply();
                ApplyOperation(activeEntry.Container, operation);
                return true;
            }

            var prefab = ResolvePrefab(key, operation.Value);
            if (prefab == null)
            {
                Debug.LogError($"{nameof(GameRuntimeObjectsDirtyCollection)} on '{name}' could not resolve a prefab for runtime object {key}.", this);
                return false;
            }

            if (!_poolsByPrefab.TryGetValue(prefab, out var pool))
            {
                pool = Factory(prefab, _parent);
                _poolsByPrefab.Add(prefab, pool);
            }

            var valueContainer = pool.PullElement();
            PlaceEntry(key, operation.Value, valueContainer);
            ApplyOperation(valueContainer, operation);

            var version = ++_entryVersion;
            var entry = new ActiveEntry
            {
                Version = version,
                Value = operation.Value,
                Container = valueContainer,
                Pool = pool,
            };
            _activeEntries[key] = entry;

            _ = FinalizePullAsync(key, entry, spawnOptions);
            return true;
        }

        private bool PlaceEntry(long key, GameRuntimeObject value, GameRuntimeObjectOperationView valueContainer)
        {
            var parent = ResolveParent(key, value, valueContainer);
            if (parent == null)
                parent = _parent.transform;
            if (valueContainer.transform.parent == parent)
                return false;

            valueContainer.transform.SetParent(parent, false);
            return true;
        }

        private void ApplyDirtyPublishCompleted(RuntimeStoreDirtyPublish publish)
        {
            if (_store == null || publish.StoreGeneration != _store.StoreGeneration)
                return;

            if (_fullRebuildPending)
            {
                ResetFromStore(ResolveSpawnOptions(CollectionViewSpawnOptions.Default));
                return;
            }

            if (!_orderDirty)
                return;

            _orderDirty = false;
            ReconcileActiveOrder();
        }

        private void ReconcileActiveOrder()
        {
            TotalReconcileCount++;
            CancelScheduledActiveOrderApply();

            switch (_scope.Kind)
            {
                case RuntimeObjectCollectionScopeKind.Parents:
                    if (!_usesIncrementalParentOrdering)
                    {
                        _sourceKeys.Clear();
                        foreach (var key in _activeEntries.Keys)
                            _sourceKeys.Add(key);

                        TotalFullSortCount++;
                        SortKeys(_sourceKeys);
                        _orderedKeys.Clear();
                        _orderedKeys.AddRange(_sourceKeys);
                        _sourceKeys.Clear();
                    }
                    break;

                case RuntimeObjectCollectionScopeKind.DirectChildren:
                    _orderedKeys.Clear();
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

        private void InsertOrderedKey(long key)
        {
            if (_scope.Kind != RuntimeObjectCollectionScopeKind.Parents)
                return;

            if (!_usesIncrementalParentOrdering)
            {
                _orderedKeys.Add(key);
                return;
            }

            var low = 0;
            var high = _orderedKeys.Count;
            while (low < high)
            {
                var middle = low + ((high - low) >> 1);
                if (CompareOrderedKeys(_orderedKeys[middle], key) <= 0)
                    low = middle + 1;
                else
                    high = middle;
            }

            _orderedKeys.Insert(low, key);
        }

        private int CompareOrderedKeys(long left, long right)
        {
            var comparison = CompareKeys(left, right);
            return comparison != 0 ? comparison : left.CompareTo(right);
        }

        private void EnsureOrderingPolicy()
        {
            if (_orderingPolicyInitialized)
                return;

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = GetType();
            _usesIncrementalParentOrdering =
                type.GetMethod(
                    nameof(CompareKeys),
                    flags,
                    binder: null,
                    types: new[] { typeof(long), typeof(long) },
                    modifiers: null)?.DeclaringType == typeof(GameRuntimeObjectsDirtyCollection)
                && type.GetMethod(
                    nameof(SortKeys),
                    flags,
                    binder: null,
                    types: new[] { typeof(List<long>) },
                    modifiers: null)?.DeclaringType == typeof(GameRuntimeObjectsDirtyCollection);
            _orderingPolicyInitialized = true;
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

            // Scene teardown may destroy pooled view objects before a store
            // presenter receives its final unbind callback. A destroyed Unity
            // container has already detached its component views and must not
            // receive a second synthetic Release operation.
            if (activeEntry.Container == null)
                return;

            _ = ReleaseEntryAsync(store, key, activeEntry, spawnOptions);
        }

        private async UniTask FinalizePullAsync(long key, ActiveEntry entry, CollectionViewSpawnOptions spawnOptions)
        {
            try
            {
                await OnAfterPullAsync(key, entry.Value, entry.Container, spawnOptions);

                if (this == null)
                    return;
                if (!_activeEntries.TryGetValue(key, out var activeEntry))
                    return;
                if (activeEntry.Version != entry.Version)
                    return;
                if (!ReferenceEquals(activeEntry.Container, entry.Container))
                    return;

                ScheduleActiveOrderApply();
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }
            finally
            {
                entry.PullCompleted.TrySetResult();
            }
        }

        private async UniTask ReleaseEntryAsync(RuntimeStore store, long key, ActiveEntry entry, CollectionViewSpawnOptions spawnOptions)
        {
            try
            {
                await entry.PullCompleted.Task;
                if (this == null || entry.Container == null)
                    return;
                ApplyOperation(entry.Container, GameRuntimeObjectOperation.Release(store, key, entry.Value));
                OnBeginRelease(key, entry.Value, entry.Container);
                await OnBeforePushAsync(key, entry.Value, entry.Container, spawnOptions);
            }
            catch (Exception e)
            {
                Debug.LogException(e, this);
            }

            if (this == null || entry.Container == null)
                return;

            if (_parent != null && entry.Container.transform.parent != _parent.transform)
                entry.Container.transform.SetParent(_parent.transform, false);
            entry.Pool.PushElement(entry.Container);
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

                if (activeEntry.Container.transform.parent != _parent.transform)
                    continue;

                if (activeEntry.Container.transform.GetSiblingIndex() != siblingIndex)
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
            CoroutineParent.AddSingleLateUpdate(() => ApplyScheduledActiveOrder(version), RECONCILE_UPDATE_ORDER);
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
            public Pool<GameRuntimeObjectOperationView> Pool;
            public readonly UniTaskCompletionSource PullCompleted = new();
        }
    }
}
