using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
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
        private readonly List<long> _snapshotKeys = new();
        private readonly HashSet<long> _snapshotLookup = new();
        private readonly List<long> _removedKeys = new();
        private readonly HashSet<long> _refreshIds = new();
        private int _entryVersion;

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

        public void Rebuild(RuntimeStore store) => Rebuild(store, CollectionViewSpawnOptions.Default);

        public void Rebuild(RuntimeStore store, CollectionViewSpawnOptions spawnOptions)
        {
            EnsurePool();
            ApplySnapshot(store, ResolveSpawnOptions(spawnOptions));
        }

        public void Clear() => Clear(CollectionViewSpawnOptions.ImmediateFill);

        public void Clear(CollectionViewSpawnOptions spawnOptions)
        {
            EnsurePool();
            ReleaseAll(null, ResolveSpawnOptions(spawnOptions));
        }

        public void ApplyStructureChanges(RuntimeStore store, NativeArray<RuntimeStructureDirty> changes) =>
            ApplyStructureChanges(store, changes, CollectionViewSpawnOptions.Default);

        public void ApplyStructureChanges(RuntimeStore store, NativeArray<RuntimeStructureDirty> changes, CollectionViewSpawnOptions spawnOptions)
        {
            EnsurePool();

            var options = ResolveSpawnOptions(spawnOptions);
            if (store == null)
            {
                ReleaseAll(null, options);
                return;
            }

            if (options.FullRebuild)
            {
                ApplySnapshot(store, options);
                return;
            }

            var orderDirty = false;
            for (var i = 0; i < changes.Length; i++)
            {
                if (ApplyStructureChange(store, changes[i], options))
                    orderDirty = true;
            }

            if (orderDirty)
                ApplyActiveOrder();
        }

        public void ApplyComponentStructureChanges(RuntimeStore store, NativeArray<ObjectStructDirty> changes)
        {
            EnsurePool();
            RefreshChangedEntries(store, changes);
        }

        public void ApplyComponentChanges(RuntimeStore store, NativeArray<ObjectComponentDirty> changes)
        {
            EnsurePool();
            RefreshChangedEntries(store, changes);
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

        private void EnsurePool()
        {
            _pool ??= Factory(_prefab, _parent);
        }

        private void ApplySnapshot(RuntimeStore store, CollectionViewSpawnOptions spawnOptions)
        {
            if (store == null)
            {
                ReleaseAll(null, spawnOptions);
                return;
            }

            var parents = store.Parents.V;
            if (parents == null || parents.Count == 0)
            {
                ReleaseAll(store, spawnOptions);
                return;
            }

            if (spawnOptions.FullRebuild)
                ReleaseAll(store, spawnOptions);

            _snapshotKeys.Clear();
            _snapshotLookup.Clear();

            foreach (var pair in parents)
            {
                if (!ShouldInclude(pair.Key, pair.Value))
                    continue;

                _snapshotKeys.Add(pair.Key);
                _snapshotLookup.Add(pair.Key);
            }

            if (_snapshotKeys.Count == 0)
            {
                ReleaseAll(store, spawnOptions);
                return;
            }

            SortKeys(_snapshotKeys);

            if (_activeEntries.Count > 0)
            {
                _removedKeys.Clear();
                foreach (var key in _activeEntries.Keys)
                {
                    if (!_snapshotLookup.Contains(key))
                        _removedKeys.Add(key);
                }

                for (var i = 0; i < _removedKeys.Count; i++)
                    ReleaseKey(store, _removedKeys[i], spawnOptions);
                _removedKeys.Clear();
            }

            _orderedKeys.Clear();
            for (var i = 0; i < _snapshotKeys.Count; i++)
            {
                var key = _snapshotKeys[i];
                _orderedKeys.Add(key);

                if (!parents.TryGetValue(key, out var value))
                    continue;

                var previousValue = _activeEntries.TryGetValue(key, out var activeEntry) ? activeEntry.Value : null;
                var operation = GameRuntimeObjectOperation.Snapshot(store, key, previousValue, value);
                UpsertEntry(key, operation, spawnOptions);
            }

            ApplyActiveOrder();
            _snapshotKeys.Clear();
            _snapshotLookup.Clear();
        }

        private bool ApplyStructureChange(RuntimeStore store, RuntimeStructureDirty dirty, CollectionViewSpawnOptions spawnOptions)
        {
            if (dirty.Id == RuntimeStore.STORE_ROOT_OBJECT_ID)
                return false;

            if (dirty.Kind == RuntimeStoreOpKind.Remove)
            {
                if (!_activeEntries.ContainsKey(dirty.Id))
                    return false;

                ReleaseKey(store, dirty.Id, spawnOptions);
                return true;
            }

            if (!TryTakeVisible(store, dirty.Id, out var value))
            {
                if (!_activeEntries.ContainsKey(dirty.Id))
                    return false;

                ReleaseKey(store, dirty.Id, spawnOptions);
                return true;
            }

            var previousValue = _activeEntries.TryGetValue(dirty.Id, out var activeEntry) ? activeEntry.Value : null;
            var operation = GameRuntimeObjectOperation.Structure(store, dirty, previousValue, value);
            UpsertEntry(dirty.Id, operation, spawnOptions);
            EnsureOrderedKey(dirty.Id);
            return true;
        }

        private void RefreshChangedEntries(RuntimeStore store, NativeArray<ObjectStructDirty> changes)
        {
            if (store == null || changes.Length == 0 || _activeEntries.Count == 0)
                return;

            _refreshIds.Clear();

            for (var i = 0; i < changes.Length; i++)
            {
                var dirty = changes[i];
                if (!_activeEntries.TryGetValue(dirty.Id, out var activeEntry))
                    continue;
                if (!_refreshIds.Add(dirty.Id))
                    continue;
                if (!TryTakeVisible(store, dirty.Id, out var value))
                    continue;

                var operation = GameRuntimeObjectOperation.ComponentStructure(store, dirty, activeEntry.Value, value);
                activeEntry.Value = value;
                ApplyOperation(activeEntry.Container, operation);
            }

            _refreshIds.Clear();
        }

        private void RefreshChangedEntries(RuntimeStore store, NativeArray<ObjectComponentDirty> changes)
        {
            if (store == null || changes.Length == 0 || _activeEntries.Count == 0)
                return;

            _refreshIds.Clear();

            for (var i = 0; i < changes.Length; i++)
            {
                var dirty = changes[i];
                if (!_activeEntries.TryGetValue(dirty.Id, out var activeEntry))
                    continue;
                if (!_refreshIds.Add(dirty.Id))
                    continue;
                if (!TryTakeVisible(store, dirty.Id, out var value))
                    continue;

                var operation = GameRuntimeObjectOperation.Component(store, dirty, activeEntry.Value, value);
                activeEntry.Value = value;
                ApplyOperation(activeEntry.Container, operation);
            }

            _refreshIds.Clear();
        }

        private bool TryTakeVisible(RuntimeStore store, long id, out GameRuntimeObject value)
        {
            value = null;
            if (store == null)
                return false;

            if (!store.Parents.V.TryGetValue(id, out value))
                return false;

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

            EnsureOrderedKey(key);
            _ = FinalizePullAsync(key, version, operation.Value, valueContainer, spawnOptions);
        }

        private void ReleaseAll(RuntimeStore store, CollectionViewSpawnOptions spawnOptions)
        {
            _orderedKeys.Clear();
            if (_activeEntries.Count == 0)
                return;

            _removedKeys.Clear();
            foreach (var key in _activeEntries.Keys)
                _removedKeys.Add(key);

            for (var i = 0; i < _removedKeys.Count; i++)
                ReleaseKey(store, _removedKeys[i], spawnOptions);

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

                ApplyActiveOrder();
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

        private void EnsureOrderedKey(long key)
        {
            var currentIndex = _orderedKeys.IndexOf(key);
            if (currentIndex >= 0)
                _orderedKeys.RemoveAt(currentIndex);

            var insertIndex = FindInsertIndex(key);
            _orderedKeys.Insert(insertIndex, key);
        }

        private void RemoveOrderedKey(long key)
        {
            var currentIndex = _orderedKeys.IndexOf(key);
            if (currentIndex >= 0)
                _orderedKeys.RemoveAt(currentIndex);
        }

        private int FindInsertIndex(long key)
        {
            var min = 0;
            var max = _orderedKeys.Count;

            while (min < max)
            {
                var mid = min + ((max - min) / 2);
                if (CompareKeys(_orderedKeys[mid], key) < 0)
                    min = mid + 1;
                else
                    max = mid;
            }

            return min;
        }

        private void ApplyActiveOrder()
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

        private CollectionViewSpawnOptions ResolveSpawnOptions(CollectionViewSpawnOptions spawnOptions)
        {
            var useDefaultOptions = spawnOptions.Equals(default(CollectionViewSpawnOptions));
            var sourceOptions = useDefaultOptions ? _defaultSpawnOptions : spawnOptions;
            return new CollectionViewSpawnOptions(immediate: sourceOptions.Immediate, fullRebuild: _fullRebuildOnChange || sourceOptions.FullRebuild);
        }

        private sealed class ActiveEntry
        {
            public int Version;
            public GameRuntimeObject Value;
            public GameRuntimeObjectOperationView Container;
        }
    }
}