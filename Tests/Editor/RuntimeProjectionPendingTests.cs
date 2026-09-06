using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using NUnit.Framework;
using Unity.Entities;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeProjectionPendingTestComponent :
        GameRuntimeComponent<RuntimeProjectionPendingTestComponent>
    {
    }

    public class RuntimeProjectionReferenceTestComponent : GameRuntimeComponent
    {
        public RuntimeInstance Target;
        public RuntimeInstance Outside;
        public RuntimeStore Resolved;
        public bool OutsideRejected;
        public bool FailProjection;

        public override void SetupForEntity(
            RuntimeStore store,
            EntityCommandBuffer ecb,
            GameRuntimeObject runtimeObject,
            Entity entity)
        {
            Assert.That(store.TryResolveProjectionStore(Target, out Resolved), Is.True);
            OutsideRejected = !store.TryResolveProjectionStore(Outside, out _);
            if (FailProjection)
                throw new InvalidOperationException("Expected projection failure.");
        }
    }

    public class RuntimeProjectionPendingTests
    {
        private World _world;
        private EntityManager _entityManager;
        private RuntimeStore _store;
        private GameObject _ownedCoroutineParent;

        [SetUp]
        public void SetUp()
        {
            EnsureCoroutineParent();
            RuntimeStores.ResetState();
            _world = new World(nameof(RuntimeProjectionPendingTests));
            _entityManager = _world.EntityManager;
            RuntimeStores.SetupWorld(_world);
            _store = RuntimeStores.GetOrAddRuntimeStore(
                "projection-pending-tests");
        }

        [TearDown]
        public void TearDown()
        {
            if (_world != null && _world.IsCreated)
                _world.Dispose();
            RuntimeStores.ResetState();
            if (_ownedCoroutineParent != null)
                Object.DestroyImmediate(_ownedCoroutineParent);
        }

        [Test]
        public void CreateEntity_RemainsPendingUntilCompleteProjectionPlayback()
        {
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(
                uint.MaxValue,
                new RuntimeProjectionPendingTestComponent());

            var entity = runtimeObject.CreateEntity();

            Assert.That(
                _entityManager.HasComponent<RuntimeProjectionPending>(entity),
                Is.True);
            Assert.That(
                _entityManager.HasComponent<RuntimeProjectionPendingTestComponent>(
                    entity),
                Is.False);

            PlaybackEditingCommands();

            Assert.That(
                _entityManager.HasComponent<RuntimeProjectionPending>(entity),
                Is.False);
            Assert.That(
                _entityManager.HasComponent<RuntimeProjectionPendingTestComponent>(
                    entity),
                Is.True);
        }

        [Test]
        public void CreateEntity_ProjectsSessionAssetIdentityOnGroRoot()
        {
            var runtimeObject = _store.Create();
            var expected = new RuntimeGameAssetIdentity
            {
                AssetIndex = new GameAssetIndex(17u),
                IdentityIndex = new GameAssetIdentityIndex(5u),
            };
            runtimeObject.SetRuntimeGameAssetIdentity(in expected);

            var entity = runtimeObject.CreateEntity();

            Assert.That(
                _entityManager.HasComponent<RuntimeGameAssetIdentity>(entity),
                Is.True);
            Assert.That(
                _entityManager.GetComponentData<RuntimeGameAssetIdentity>(
                    entity),
                Is.EqualTo(expected));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void StagedProjectionResolvesOnlyItsCohortAndAlwaysReleasesScope(bool failProjection)
        {
            var published = RuntimeStores.GetOrAddRuntimeStore("projection-target");
            var outside = RuntimeStores.GetOrAddRuntimeStore("projection-outside");
            var stagedTarget = RuntimeStores.PrepareRestoreStore(published.Id, published.Realm);
            var stagedRoot = RuntimeStores.PrepareRestoreStore(_store.Id, _store.Realm);
            try
            {
                var component = new RuntimeProjectionReferenceTestComponent
                {
                    Target = stagedTarget.Create().RuntimeInstance,
                    Outside = outside.Create().RuntimeInstance,
                    FailProjection = failProjection,
                };
                var root = stagedRoot.Create();
                root.AddOrReplaceById(uint.MaxValue - 1u, component);
                var cohort = new Dictionary<FixedString32Bytes, RuntimeStore>
                {
                    { stagedRoot.Id, stagedRoot },
                    { stagedTarget.Id, stagedTarget },
                };

                if (failProjection)
                {
                    Assert.Throws<InvalidOperationException>(
                        () => stagedRoot.CreateEntitySubtree(root.InstanceId, cohort));
                }
                else
                {
                    stagedRoot.CreateEntitySubtree(root.InstanceId, cohort);
                }

                Assert.That(component.Resolved, Is.SameAs(stagedTarget));
                Assert.That(component.OutsideRejected, Is.True);
                Assert.That(RuntimeStores.GetRuntimeStore(published.Id, published.Realm),
                    Is.SameAs(published));
                Assert.That(stagedRoot.TryResolveProjectionStore(component.Target, out _), Is.False);
                Assert.That(stagedRoot.TryResolveProjectionStore(component.Outside, out var restored), Is.True);
                Assert.That(restored, Is.SameAs(outside));
            }
            finally
            {
                stagedRoot.Retire();
                stagedTarget.Retire();
            }
        }

        private void PlaybackEditingCommands()
        {
            _world.GetOrCreateSystemManaged<
                EndSimulationEntityCommandBufferSystem>().Update();
        }

        private void EnsureCoroutineParent()
        {
            if (CoroutineParent.GetNoCheck() != null)
                return;

            _ownedCoroutineParent = new GameObject(
                $"{nameof(RuntimeProjectionPendingTests)} CoroutineParent");
            _ownedCoroutineParent.AddComponent<CoroutineParent>();
        }
    }
}
