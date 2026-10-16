using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoGameObjectsCMS.View;
using DingoUnityExtensions;
using DingoUnityExtensions.UnityViewProviders.Pools;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class InclusionTestView : GameRuntimeObjectOperationView
    {
        public int SpawnCount { get; private set; }
        public int DespawnCount { get; private set; }
        public int OperationCount { get; private set; }
        public object PreparedResource { get; private set; }
        protected override void OnGROCreated(GameRuntimeObject value, GameRuntimeObjectOperation operation) => PreparedResource = new object();
        protected override void OnGROOperation(GameRuntimeObject previous, GameRuntimeObject value, GameRuntimeObjectOperation operation) => OperationCount++;
        public override UniTask SpawnAsync(CollectionViewSpawnOptions options)
        {
            SpawnCount++;
            return UniTask.CompletedTask;
        }
        public override UniTask DespawnAsync(CollectionViewSpawnOptions options)
        {
            DespawnCount++;
            return base.DespawnAsync(options);
        }
    }

    public class InclusionTestCollection : GameRuntimeObjectsDirtyCollection
    {
        public readonly HashSet<long> Excluded = new();
        public Action<long> Releasing;
        public Action<long> Querying;
        protected override bool ShouldInclude(long key, GameRuntimeObject value)
        {
            var included = base.ShouldInclude(key, value) && !Excluded.Contains(key);
            Querying?.Invoke(key);
            return included;
        }
        protected override UniTask OnBeforePushAsync(long key, GameRuntimeObject value, GameRuntimeObjectOperationView view, CollectionViewSpawnOptions options)
        {
            Releasing?.Invoke(key);
            return base.OnBeforePushAsync(key, value, view, options);
        }
    }

    public class RuntimeCollectionInclusionTests
    {
        private GameObject _root, _scheduler;
        private World _world;
        private RuntimeStore _store;
        private InclusionTestCollection _collection;

        [SetUp]
        public void SetUp()
        {
            if (CoroutineParent.GetNoCheck() == null)
            {
                _scheduler = new GameObject("Inclusion test scheduler");
                _scheduler.AddComponent<CoroutineParent>();
            }
            RuntimeStores.ResetState();
            _world = new World(nameof(RuntimeCollectionInclusionTests));
            RuntimeStores.SetupWorld(_world);
            _world.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
            _store = RuntimeStores.GetOrAddRuntimeStore("collection-inclusion-tests");
            _root = new GameObject(nameof(RuntimeCollectionInclusionTests));
            _collection = _root.AddComponent<InclusionTestCollection>();
            var prototype = new GameObject("Inclusion prototype");
            prototype.transform.SetParent(_root.transform);
            prototype.SetActive(false);
            _collection.ConfigureRuntime(_root, prototype.AddComponent<InclusionTestView>());
        }

        [TearDown]
        public void TearDown()
        {
            _collection?.Unbind();
            Object.DestroyImmediate(_root);
            RuntimeStores.ResetState();
            if (_world?.IsCreated == true)
                _world.Dispose();
            if (_scheduler != null)
                Object.DestroyImmediate(_scheduler);
        }

        [Test]
        public void RefreshInclusionReleasesOnlySelectedRowsAndPreservesUnrelatedPreparationAndEffects()
        {
            var removed = _store.Create();
            var retained = _store.Create();
            _store.FlushToQuiescence();
            _collection.Bind(_store);
            Assert.That(_collection.TryGetView<InclusionTestView>(retained.InstanceId, out var view), Is.True);
            var preparation = view.PreparedResource;
            var operations = view.OperationCount;
            var spawns = view.SpawnCount;
            _collection.Excluded.Add(removed.InstanceId);

            _collection.RefreshInclusion(new[] { removed.InstanceId, retained.InstanceId });
            _collection.RefreshInclusion(new[] { removed.InstanceId, retained.InstanceId });

            Assert.That(_collection.TryGetView(removed.InstanceId, out _), Is.False);
            Assert.That(_collection.TryGetView(retained.InstanceId, out var current), Is.True);
            Assert.That(current, Is.SameAs(view));
            Assert.That(view.PreparedResource, Is.SameAs(preparation));
            Assert.That(view.OperationCount, Is.EqualTo(operations));
            Assert.That(view.SpawnCount, Is.EqualTo(spawns));
            Assert.That(_store.TryTakeRO(removed.InstanceId, out _), Is.True, "Presentation filtering does not mutate the Store.");
        }

        [Test]
        public void RefreshInclusionFreezesTheSelectedKeysBeforeReleaseCallbacksMutateThem()
        {
            var first = _store.Create();
            var second = _store.Create();
            _store.FlushToQuiescence();
            _collection.Bind(_store);
            var selected = new List<long> { first.InstanceId, second.InstanceId };
            _collection.Excluded.UnionWith(selected);
            _collection.Releasing = _ => selected.Clear();

            _collection.RefreshInclusion(selected);

            Assert.That(_collection.TryGetView(first.InstanceId, out _), Is.False);
            Assert.That(_collection.TryGetView(second.InstanceId, out _), Is.False);
        }

        [Test]
        public void ReleaseCallbackRebindingTheSameStoreCannotReleaseItsReplacementRows()
        {
            var first = _store.Create();
            var second = _store.Create();
            _store.FlushToQuiescence();
            _collection.Bind(_store);
            _collection.Excluded.UnionWith(new[] { first.InstanceId, second.InstanceId });
            InclusionTestView replacement = null;
            var replacementSpawns = 0;
            _collection.Releasing = _ =>
            {
                _collection.Releasing = null;
                _collection.Unbind();
                _collection.Excluded.Clear();
                _collection.Bind(_store);
                Assert.That(_collection.TryGetView(second.InstanceId, out replacement), Is.True);
                replacementSpawns = replacement.SpawnCount;
            };

            _collection.RefreshInclusion(new[] { first.InstanceId, second.InstanceId });

            Assert.That(_collection.TryGetView(second.InstanceId, out var current), Is.True);
            Assert.That(current, Is.SameAs(replacement));
            Assert.That(replacementSpawns, Is.GreaterThan(0));
            Assert.That(replacement.SpawnCount, Is.EqualTo(replacementSpawns));
        }

        [Test]
        public void MembershipPredicateRebindingTheSameStoreCannotReleaseTheReplacementRow()
        {
            var item = _store.Create();
            _store.FlushToQuiescence();
            _collection.Bind(_store);
            Assert.That(_collection.TryGetView<InclusionTestView>(item.InstanceId, out var original), Is.True);
            var originalSpawns = original.SpawnCount;
            _collection.Excluded.Add(item.InstanceId);
            InclusionTestView replacement = null;
            var replacementSpawns = 0;
            var replacementDespawns = 0;
            var replacementOperations = 0;
            object replacementPreparation = null;
            _collection.Querying = _ =>
            {
                _collection.Querying = null;
                _collection.Unbind();
                _collection.Excluded.Clear();
                _collection.Bind(_store);
                Assert.That(_collection.TryGetView(item.InstanceId, out replacement), Is.True);
                Assert.That(replacement, Is.SameAs(original), "The synchronous pool release reuses the original container for this binding.");
                Assert.That(replacement.SpawnCount, Is.EqualTo(originalSpawns + 1), "Rebinding spawns a new row on a container whose effect counters survive pooling.");
                replacementSpawns = replacement.SpawnCount;
                replacementDespawns = replacement.DespawnCount;
                replacementOperations = replacement.OperationCount;
                replacementPreparation = replacement.PreparedResource;
            };

            _collection.RefreshInclusion(new[] { item.InstanceId });

            Assert.That(_collection.TryGetView(item.InstanceId, out var current), Is.True);
            Assert.That(current, Is.SameAs(replacement));
            Assert.That(replacementSpawns, Is.GreaterThan(0));
            Assert.That(replacement.SpawnCount, Is.EqualTo(replacementSpawns));
            Assert.That(replacement.DespawnCount, Is.EqualTo(replacementDespawns));
            Assert.That(replacement.OperationCount, Is.EqualTo(replacementOperations));
            Assert.That(replacement.PreparedResource, Is.SameAs(replacementPreparation));
        }

        [Test]
        public void RebindingDuringKeyEnumerationDoesNotRedirectTheOldRefreshToTheNewBinding()
        {
            var item = _store.Create();
            _store.FlushToQuiescence();
            _collection.Bind(_store);
            InclusionTestView replacement = null;
            IEnumerable<long> Selected()
            {
                _collection.Unbind();
                _collection.Bind(_store);
                Assert.That(_collection.TryGetView(item.InstanceId, out replacement), Is.True);
                _collection.Excluded.Add(item.InstanceId);
                yield return item.InstanceId;
            }

            _collection.RefreshInclusion(Selected());

            Assert.That(_collection.TryGetView(item.InstanceId, out var current), Is.True);
            Assert.That(current, Is.SameAs(replacement));
        }
    }
}
