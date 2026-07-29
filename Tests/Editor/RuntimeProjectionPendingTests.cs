using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeProjectionPendingTestComponent :
        GameRuntimeComponent<RuntimeProjectionPendingTestComponent>
    {
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
