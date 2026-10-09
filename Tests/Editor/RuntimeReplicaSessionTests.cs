using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplicaSessionTests
    {
        private World _world;
        private readonly FixedString32Bytes _storeId = new("replica-session-tests");

        [SetUp]
        public void SetUp()
        {
            RuntimeStores.ResetState();
            _world = new World(nameof(RuntimeReplicaSessionTests));
            RuntimeStores.SetupWorld(_world);
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeStores.ResetState();
            _world.Dispose();
        }

        [Test]
        public void NewSession_PreservesLocalEpochAndAcceptsLowerRemoteGeneration()
        {
            var previous = Prepare(4);
            RuntimeStores.PublishPreparedReplicaStore(previous, StoreNetDir.None);
            var previousHandle = new RuntimeInstance { StoreId = _storeId, Id = 1, Epoch = previous.Epoch };
            RuntimeStores.RemoveRuntimeStore(_storeId, StoreRealm.Client);
            RuntimeStores.BeginReplicaSession(new[] { _storeId });
            var current = Prepare(1);
            RuntimeStores.PublishPreparedReplicaStore(current, StoreNetDir.None);
            Assert.That(current.StoreGeneration, Is.EqualTo(1));
            Assert.That(current.Epoch, Is.GreaterThan(previous.Epoch));
            Assert.That(current.IsRuntimeInstanceActive(previousHandle), Is.False);

            var older = new RuntimeStore(_storeId, StoreRealm.Client, _world);
            RuntimeStores.PrepareReplicaStore(older, 3);
            var stale = new RuntimeStore(_storeId, StoreRealm.Client, _world);
            Assert.Throws<InvalidOperationException>(() => RuntimeStores.PrepareReplicaStore(stale, 2),
                "Within a session, lower generations must still be rejected.");
            RuntimeStores.SetRuntimeStore(older);
            RuntimeStores.SetRuntimeStore(stale);
        }

        [Test]
        public void NewSession_CannotResetAnActiveStoreOrPartialGroupReservations()
        {
            var previous = Prepare(4);
            RuntimeStores.PublishPreparedReplicaStore(previous, StoreNetDir.None);
            RuntimeStores.RemoveRuntimeStore(_storeId, StoreRealm.Client);
            var activeId = new FixedString32Bytes("still-active");
            var active = RuntimeStores.GetOrAddRuntimeStore(activeId, realm: StoreRealm.Client);
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeStores.BeginReplicaSession(new[] { _storeId, activeId }));
            Assert.That(RuntimeStores.GetRuntimeStore(activeId, StoreRealm.Client), Is.SameAs(active));
            Assert.That(active.Retired, Is.False);
            var stale = new RuntimeStore(_storeId, StoreRealm.Client, _world);
            Assert.Throws<InvalidOperationException>(() => RuntimeStores.PrepareReplicaStore(stale, 1),
                "Validation of the full group must finish before any reservation is cleared.");
            RuntimeStores.SetRuntimeStore(stale);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreparedStage_FromPreviousSessionCannotPublishWithReusedGeneration(bool grouped)
        {
            var previous = Prepare(1);
            RuntimeStores.BeginReplicaSession(new[] { _storeId });
            var current = Prepare(1);
            RuntimeStores.PublishPreparedReplicaStore(current, StoreNetDir.None);
            Assert.That(current.Epoch, Is.GreaterThan(previous.Epoch));
            if (grouped)
            {
                Assert.Throws<InvalidOperationException>(() =>
                    RuntimeStores.PublishPreparedReplicaStores(new[] { previous }, StoreNetDir.None));
            }
            else
            {
                Assert.Throws<InvalidOperationException>(() =>
                    RuntimeStores.PublishPreparedReplicaStore(previous, StoreNetDir.None));
            }
            Assert.That(RuntimeStores.GetRuntimeStore(_storeId, StoreRealm.Client), Is.SameAs(current));
            Assert.That(current.Retired, Is.False);
            RuntimeStores.SetRuntimeStore(previous);
        }

        private RuntimeStore Prepare(uint generation)
        {
            var store = new RuntimeStore(_storeId, StoreRealm.Client, _world);
            RuntimeStores.PrepareReplicaStore(store, generation);
            return store;
        }
    }
}
