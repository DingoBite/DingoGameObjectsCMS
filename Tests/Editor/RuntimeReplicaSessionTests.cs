using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplicaSessionTests
    {
        private World _world;
        private GameObject _ownedCoroutineParent;
        private readonly FixedString32Bytes _storeId = new("replica-session-tests");

        [SetUp]
        public void SetUp()
        {
            RuntimeStores.ResetState();
            if (CoroutineParent.GetNoCheck() == null)
            {
                _ownedCoroutineParent = new GameObject(nameof(RuntimeReplicaSessionTests));
                _ownedCoroutineParent.AddComponent<CoroutineParent>();
            }
            _world = new World(nameof(RuntimeReplicaSessionTests));
            RuntimeStores.SetupWorld(_world);
            _world.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeStores.ResetState();
            _world.Dispose();
            if (_ownedCoroutineParent != null)
                UnityEngine.Object.DestroyImmediate(_ownedCoroutineParent);
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

        [TestCase(false)]
        [TestCase(true)]
        public void BaselinePublication_ObserverFailureKeepsCommittedStoresAndCompletesLifecycle(bool grouped)
        {
            var ids = grouped ? new[] { _storeId, new FixedString32Bytes("replica-second") } : new[] { _storeId };
            var previous = new RuntimeStore[ids.Length];
            var stages = new RuntimeReplicaBaselineStage[ids.Length];
            var realms = new RuntimeReplicaStagingRealms();
            for (var i = 0; i < ids.Length; i++)
            {
                previous[i] = new RuntimeStore(ids[i], StoreRealm.Client, _world);
                RuntimeStores.PrepareReplicaStore(previous[i], 1);
                RuntimeStores.PublishPreparedReplicaStore(previous[i], StoreNetDir.None);
                stages[i] = PrepareBaseline(ids[i], 2, realms);
                stages[i].Build();
            }

            var changes = new List<RuntimeStoreLifecycleChange>();
            var injected = new InvalidOperationException("Expected replica publication observer failure.");
            var notifications = 0;
            Action<IReadOnlyDictionary<FixedString32Bytes, RuntimeStore>> failObserver = published =>
            {
                notifications++;
                for (var i = 0; i < ids.Length; i++)
                {
                    Assert.That(published[ids[i]], Is.SameAs(stages[i].Store));
                    Assert.That(RuntimeStores.GetRuntimeStore(ids[i], StoreRealm.Client), Is.SameAs(stages[i].Store));
                    Assert.That(RuntimeStores.GetNetDir(ids[i]), Is.EqualTo(StoreNetDir.S2C));
                }
                throw injected;
            };
            Action<RuntimeStoreLifecycleChange> observeLifecycle = change =>
            {
                changes.Add(change);
                for (var i = 0; i < ids.Length; i++)
                {
                    Assert.That(previous[i].Retired, Is.True);
                    Assert.That(stages[i].Store.Retired, Is.False);
                }
            };
            RuntimeStores.ClientStores.OnValueChange += failObserver;
            RuntimeStores.StoreLifecycleChanged += observeLifecycle;
            try
            {
                var applier = new RuntimeReplicaBaselineApplier();
                var failure = grouped
                    ? Assert.Throws<InvalidOperationException>(() => applier.PublishGroup(stages))
                    : Assert.Throws<InvalidOperationException>(() => applier.Publish(stages[0]));
                Assert.That(failure, Is.SameAs(injected));
                Assert.That(notifications, Is.EqualTo(1));
                Assert.That(changes, Has.Count.EqualTo(ids.Length));
                Assert.That(realms.PreparedStoreCount, Is.Zero);
                var committed = new RuntimeStore[stages.Length];
                for (var i = 0; i < stages.Length; i++)
                {
                    committed[i] = stages[i].Store;
                    Assert.That(stages[i].State, Is.EqualTo(RuntimeReplicaBaselineStageState.Published));
                    Assert.That(stages[i].HasPreparedRealm, Is.False);
                    Assert.That(previous[i].Retired, Is.True);
                    Assert.That(committed[i].Retired, Is.False);
                    Assert.That(committed[i].TryTakeRO(RuntimeStore.STORE_ROOT_OBJECT_ID, out _), Is.True);
                    Assert.That(changes[i].Kind, Is.EqualTo(RuntimeStoreLifecycleKind.Replaced));
                    Assert.That(changes[i].PreviousStore, Is.SameAs(previous[i]));
                    Assert.Throws<InvalidOperationException>(() => stages[i].Abort());
                }
                if (grouped)
                    Assert.Throws<InvalidOperationException>(() => applier.PublishGroup(stages));
                else
                    Assert.Throws<InvalidOperationException>(() => applier.Publish(stages[0]));
                RuntimeStores.PublishPreparedReplicaStores(committed);
                RuntimeStores.PublishPreparedReplicaStore(committed[0]);
                Assert.That(notifications, Is.EqualTo(1), "Repeated committed publication must not notify bindings again.");
                Assert.That(changes, Has.Count.EqualTo(ids.Length));
            }
            finally
            {
                RuntimeStores.ClientStores.OnValueChange -= failObserver;
                RuntimeStores.StoreLifecycleChanged -= observeLifecycle;
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void BaselinePublication_StaleReservationLeavesPreviousCohortAndAbortsOnlyStaging(bool grouped)
        {
            var ids = grouped ? new[] { _storeId, new FixedString32Bytes("replica-second") } : new[] { _storeId };
            var previous = new RuntimeStore[ids.Length];
            var stages = new RuntimeReplicaBaselineStage[ids.Length];
            var realms = new RuntimeReplicaStagingRealms();
            for (var i = 0; i < ids.Length; i++)
            {
                previous[i] = new RuntimeStore(ids[i], StoreRealm.Client, _world);
                RuntimeStores.PrepareReplicaStore(previous[i], 1);
                RuntimeStores.PublishPreparedReplicaStore(previous[i], StoreNetDir.None);
                stages[i] = PrepareBaseline(ids[i], 2, realms);
                stages[i].Build();
            }
            var superseding = PrepareBaseline(ids[ids.Length - 1], 3, new RuntimeReplicaStagingRealms());
            var notifications = 0;
            Action<IReadOnlyDictionary<FixedString32Bytes, RuntimeStore>> observe = _ => notifications++;
            RuntimeStores.ClientStores.OnValueChange += observe;
            try
            {
                var applier = new RuntimeReplicaBaselineApplier();
                if (grouped)
                    Assert.Throws<InvalidOperationException>(() => applier.PublishGroup(stages));
                else
                    Assert.Throws<InvalidOperationException>(() => applier.Publish(stages[0]));
                Assert.That(notifications, Is.Zero);
                for (var i = 0; i < ids.Length; i++)
                {
                    Assert.That(RuntimeStores.GetRuntimeStore(ids[i], StoreRealm.Client), Is.SameAs(previous[i]));
                    Assert.That(previous[i].Retired, Is.False);
                    Assert.That(stages[i].State, Is.EqualTo(grouped
                        ? RuntimeReplicaBaselineStageState.Built : RuntimeReplicaBaselineStageState.Failed));
                    stages[i].Abort();
                    Assert.That(stages[i].Store.Retired, Is.True);
                }
                Assert.That(realms.PreparedStoreCount, Is.Zero);
            }
            finally
            {
                RuntimeStores.ClientStores.OnValueChange -= observe;
                superseding.Abort();
            }
        }

        private RuntimeReplicaBaselineStage PrepareBaseline(FixedString32Bytes id, uint generation, RuntimeReplicaStagingRealms realms)
        {
            var codecs = new RuntimePatchCodecRegistry("replica-publication-tests");
            var assetLock = new GameAssetLibraryLock().Seal();
            var templates = new GameAssetTemplateCache(codecs, RuntimeTemplatePatchCodecContext.Instance);
            var policies = new RuntimeReplicationPolicyRegistry();
            policies.Seal(Array.Empty<uint>());
            var codec = new RuntimeStoreBaselineCodec(codecs);
            var factory = new RuntimeReplicaBaselineSpawnFactory(RuntimeSessionAssetCatalog.FromLock(assetLock), assetLock, templates, policies);
            var stager = new RuntimeReplicaBaselineStager(_world, codec, factory, realms);
            return stager.Prepare(codec.Encode(new RuntimeStoreBaselinePayload
            {
                Store = new NetStoreRef(id, generation),
                BaselineId = 1,
                RootPatch = new RuntimeObjectPatch(codecs.SchemaHash),
            }));
        }

        private RuntimeStore Prepare(uint generation)
        {
            var store = new RuntimeStore(_storeId, StoreRealm.Client, _world);
            RuntimeStores.PrepareReplicaStore(store, generation);
            return store;
        }
    }
}
