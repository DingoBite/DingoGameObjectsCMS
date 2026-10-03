using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeDotsCheckpointTestParticipant :
        IRuntimeReplayCheckpointParticipant
    {
        public const uint SECTION_ID = 0xD0750001u;

        public int Value;
        public int CaptureCalls;
        public bool FailCapture;
        public RuntimeCommandJournal JournalToMutate;

        public uint SectionId => SECTION_ID;
        public uint CurrentVersion => 1u;

        public void Capture(RuntimeReplayCheckpointWriter writer)
        {
            CaptureCalls++;
            if (FailCapture)
            {
                throw new InvalidOperationException(
                    "Intentional DOTS checkpoint capture failure.");
            }

            writer.WriteInt32(Value);
            JournalToMutate?.AppendOutcomeBatch(
                Value,
                new RuntimeEncodedCommand(
                    typeId: 1u,
                    codecVersion: 1,
                    payload: new byte[] { 1 }),
                RuntimeCommandJournalScope.Session);
        }

        public void Restore(RuntimeReplayCheckpointReader reader)
        {
            Value = reader.ReadInt32();
        }

        public void AppendFingerprint(RuntimeReplayCheckpointWriter writer)
        {
            writer.WriteString("dots.checkpoint.test");
        }
    }

    public class RuntimeDotsCheckpointTests
    {
        private World _world;
        private GameObject _ownedCoroutineParent;

        [SetUp]
        public void SetUp()
        {
            EnsureCoroutineParent();
            RuntimeStores.ResetState();
            _world = new World("RuntimeDotsCheckpointTests");
            RuntimeStores.SetupWorld(_world);
            _world.GetOrCreateSystemManaged<
                EndSimulationEntityCommandBufferSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_world != null && _world.IsCreated)
            {
                _world.Dispose();
            }
            RuntimeStores.ResetState();
            if (_ownedCoroutineParent != null)
            {
                UnityEngine.Object.DestroyImmediate(
                    _ownedCoroutineParent);
            }
        }

        [Test]
        public void CaptureAtCompletedTick_CapturesOnlyAtMatchingBarrier()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant
            {
                Value = 12,
            };
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                registry,
                journal,
                bus);

            Assert.Throws<InvalidOperationException>(
                () => coordinator.CaptureAtCompletedTick(12));
            Assert.That(participant.CaptureCalls, Is.Zero);

            bus.Drain(applyBeforeTick: 11);
            Assert.Throws<InvalidOperationException>(
                () => coordinator.CaptureAtCompletedTick(12));
            Assert.That(participant.CaptureCalls, Is.Zero);

            bus.Drain(applyBeforeTick: 12);
            var checkpoint =
                coordinator.CaptureAtCompletedTick(completedTick: 12);

            Assert.That(participant.CaptureCalls, Is.EqualTo(1));
            Assert.That(checkpoint.CompletedTick, Is.EqualTo(12));
            Assert.That(checkpoint.Cursor, Is.EqualTo(journal.Cursor));
            Assert.That(coordinator.HasCheckpoint, Is.True);
            Assert.That(
                coordinator.CurrentBoundary.CheckpointHash,
                Is.EqualTo(
                    RuntimeReplayHash.ToHex(
                        checkpoint.OverallHash)));
        }

        [Test]
        public void FailedCapture_KeepsLastValidCheckpointAndBoundary()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant
            {
                Value = 20,
            };
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                registry,
                journal,
                bus);

            bus.Drain(applyBeforeTick: 20);
            var validCheckpoint =
                coordinator.CaptureAtCompletedTick(completedTick: 20);
            var validBoundary = coordinator.CurrentBoundary;

            participant.FailCapture = true;
            bus.Drain(applyBeforeTick: 21);
            Assert.Throws<InvalidOperationException>(
                () => coordinator.CaptureAtCompletedTick(21));

            Assert.That(
                coordinator.CurrentCheckpoint,
                Is.SameAs(validCheckpoint));
            Assert.That(
                coordinator.CurrentBoundary.CompletedTick,
                Is.EqualTo(validBoundary.CompletedTick));
            Assert.That(
                coordinator.CurrentBoundary.JournalCursor,
                Is.EqualTo(validBoundary.JournalCursor));
            Assert.That(
                coordinator.CurrentBoundary.CheckpointHash,
                Is.EqualTo(validBoundary.CheckpointHash));
            Assert.That(participant.CaptureCalls, Is.EqualTo(2));
        }

        [Test]
        public void Capture_DoesNotCloneOrPublishCheckpointScopedStore()
        {
            var storeId = new FixedString32Bytes("map");
            var activeBefore =
                RuntimeStores.GetOrAddRuntimeStore(storeId);
            var participant = new RuntimeDotsCheckpointTestParticipant
            {
                Value = 10,
            };
            var registry = CreateRegistry(
                participant,
                CreateStoreParticipant("map"));
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var group = CreateGroup(
                "test.read-only-store",
                new RuntimeReplayStoreScope("map"),
                new RuntimeCommandJournalScope("map"));
            var coordinator = new RuntimeDotsCheckpointCoordinator(
                group,
                bus,
                journal,
                registry,
                _world,
                StoreRealm.Server);

            var epoch = activeBefore.Epoch;
            var generation = activeBefore.StoreGeneration;
            var revision = activeBefore.StoreRevision;
            bus.Drain(applyBeforeTick: 10);
            coordinator.CaptureAtCompletedTick(10);

            var activeAfter = RuntimeStores.GetRuntimeStore(
                storeId,
                StoreRealm.Server);
            Assert.That(activeAfter, Is.SameAs(activeBefore));
            Assert.That(activeAfter.Epoch, Is.EqualTo(epoch));
            Assert.That(
                activeAfter.StoreGeneration,
                Is.EqualTo(generation));
            Assert.That(activeAfter.StoreRevision, Is.EqualTo(revision));
            Assert.That(activeAfter.Retired, Is.False);
        }

        [Test]
        public void PrepareRestoreStage_DoesNotPublishUntilExplicitCommit()
        {
            var storeId = new FixedString32Bytes("map");
            var active = RuntimeStores.GetOrAddRuntimeStore(storeId);
            var participant = CreateStoreParticipant("map");
            var registry = CreateRegistry(participant);
            var checkpoint = registry.Capture(12L, 0UL);
            var section = checkpoint.Sections[0];

            RuntimeStoresReplayCheckpointStage stage;
            using (var reader = new RuntimeReplayCheckpointReader(
                       section.Pages,
                       section.PayloadLength,
                       RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES))
            {
                stage = participant.PrepareRestoreStage(reader);
            }

            using (stage)
            {
                var staged = stage.TakeStore(storeId);
                Assert.That(staged, Is.Not.SameAs(active));
                Assert.That(
                    RuntimeStores.GetRuntimeStore(
                        storeId,
                        StoreRealm.Server),
                    Is.SameAs(active));

                stage.Publish();

                Assert.That(stage.IsPublished, Is.True);
                Assert.That(
                    RuntimeStores.GetRuntimeStore(
                        storeId,
                        StoreRealm.Server),
                    Is.SameAs(staged));
            }
        }

        [TestCase(StoreRealm.Server)]
        [TestCase(StoreRealm.Client)]
        public void PrepareRestoreStage_ObserverFailureKeepsPublishedCohortAlive(StoreRealm realm)
        {
            var mapId = new FixedString32Bytes("map");
            var unitsId = new FixedString32Bytes("units");
            var previousMap = RuntimeStores.GetOrAddRuntimeStore(mapId, realm: realm);
            var previousUnits = RuntimeStores.GetOrAddRuntimeStore(unitsId, realm: realm);
            var otherRealm = realm == StoreRealm.Server ? StoreRealm.Client : StoreRealm.Server;
            var otherMap = RuntimeStores.GetOrAddRuntimeStore(mapId, realm: otherRealm);
            var otherUnits = RuntimeStores.GetOrAddRuntimeStore(unitsId, realm: otherRealm);
            var participant = CreateStoreParticipant(realm, "map", "units");
            var checkpoint = CreateRegistry(participant).Capture(12L, 7UL);
            var section = checkpoint.Sections[0];
            RuntimeStoresReplayCheckpointStage stage;
            using (var reader = new RuntimeReplayCheckpointReader(section.Pages, section.PayloadLength,
                       RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES))
            {
                stage = participant.PrepareRestoreStage(reader);
            }
            var restoredMap = stage.TakeStore(mapId);
            var restoredUnits = stage.TakeStore(unitsId);
            var binding = realm == StoreRealm.Server ? RuntimeStores.ServerStores : RuntimeStores.ClientStores;
            var injected = new InvalidOperationException("Intentional checkpoint publication observer failure.");
            var callbacks = 0;
            var observerSawCompleteSwap = false;
            var lifecycleChanges = new List<RuntimeStoreLifecycleChange>();
            var restoreCompletions = new List<RuntimeStoresRestoreCompleted>();
            var lifecycleSawPublishedCohort = true;
            var completionSawPublishedCohort = true;
            bool PublishedCohortIsValid() => previousMap.Retired && previousUnits.Retired
                && !restoredMap.Retired && !restoredUnits.Retired
                && ReferenceEquals(RuntimeStores.GetRuntimeStore(mapId, realm), restoredMap)
                && ReferenceEquals(RuntimeStores.GetRuntimeStore(unitsId, realm), restoredUnits);
            Action<RuntimeStoreLifecycleChange> observeLifecycle = change =>
            {
                lifecycleChanges.Add(change);
                lifecycleSawPublishedCohort &= PublishedCohortIsValid();
            };
            Action<RuntimeStoresRestoreCompleted> observeCompletion = completed =>
            {
                restoreCompletions.Add(completed);
                completionSawPublishedCohort &= PublishedCohortIsValid();
            };
            Action<IReadOnlyDictionary<FixedString32Bytes, RuntimeStore>> failPublication = stores =>
            {
                callbacks++;
                observerSawCompleteSwap = ReferenceEquals(stores[mapId], restoredMap)
                    && ReferenceEquals(stores[unitsId], restoredUnits);
                throw injected;
            };
            binding.OnValueChange += failPublication;
            RuntimeStores.StoreLifecycleChanged += observeLifecycle;
            RuntimeStores.RestoreCompleted += observeCompletion;
            try
            {
                Assert.That(RuntimeStores.GetRuntimeStore(mapId, realm), Is.SameAs(previousMap));
                Assert.That(RuntimeStores.GetRuntimeStore(unitsId, realm), Is.SameAs(previousUnits));
                var failure = Assert.Throws<InvalidOperationException>(() => stage.Publish());
                Assert.That(failure, Is.SameAs(injected));
                Assert.That(callbacks, Is.EqualTo(1));
                Assert.That(observerSawCompleteSwap, Is.True);
                Assert.That(lifecycleChanges.Count, Is.EqualTo(2));
                Assert.That(lifecycleSawPublishedCohort, Is.True,
                    "Lifecycle callbacks must observe retired previous stores and the complete live replacement cohort.");
                CollectionAssert.AreEquivalent(new[] { mapId, unitsId },
                    new[] { lifecycleChanges[0].StoreId, lifecycleChanges[1].StoreId });
                foreach (var change in lifecycleChanges)
                {
                    Assert.That(change.Kind, Is.EqualTo(RuntimeStoreLifecycleKind.Replaced));
                    Assert.That(change.Realm, Is.EqualTo(realm));
                    Assert.That(change.Store, Is.SameAs(change.StoreId.Equals(mapId) ? restoredMap : restoredUnits));
                    Assert.That(change.PreviousStore, Is.SameAs(change.StoreId.Equals(mapId) ? previousMap : previousUnits));
                    Assert.That(change.StoreGeneration, Is.EqualTo(change.Store.StoreGeneration));
                }
                Assert.That(restoreCompletions.Count, Is.EqualTo(1));
                Assert.That(completionSawPublishedCohort, Is.True,
                    "Restore completion must observe the published registry after the bind observer failure.");
                Assert.That(restoreCompletions[0].Realm, Is.EqualTo(realm));
                CollectionAssert.AreEquivalent(new[] { restoredMap, restoredUnits }, restoreCompletions[0].Stores);
                Assert.That(stage.IsPublished, Is.True,
                    "The stage must report registry publication even when a later observer throws.");
                Assert.That(previousMap.Retired, Is.True);
                Assert.That(previousUnits.Retired, Is.True);
                Assert.That(RuntimeStores.GetRuntimeStore(mapId, realm), Is.SameAs(restoredMap));
                Assert.That(RuntimeStores.GetRuntimeStore(unitsId, realm), Is.SameAs(restoredUnits));
                Assert.That(restoredMap.Retired, Is.False);
                Assert.That(restoredUnits.Retired, Is.False);
                Assert.That(restoredMap.Epoch, Is.EqualTo(previousMap.Epoch + 1u));
                Assert.That(restoredUnits.StoreGeneration, Is.EqualTo(previousUnits.StoreGeneration + 1u));
                Assert.Throws<InvalidOperationException>(() => stage.Publish());
                Assert.That(callbacks, Is.EqualTo(1), "A repeated publication attempt must not notify observers again.");
                Assert.That(lifecycleChanges.Count, Is.EqualTo(2));
                Assert.That(restoreCompletions.Count, Is.EqualTo(1));
            }
            finally
            {
                binding.OnValueChange -= failPublication;
                RuntimeStores.StoreLifecycleChanged -= observeLifecycle;
                RuntimeStores.RestoreCompleted -= observeCompletion;
                stage.Dispose();
            }
            Assert.That(stage.IsDisposed, Is.True);
            Assert.That(stage.IsPublished, Is.True);
            Assert.That(RuntimeStores.GetRuntimeStore(mapId, realm), Is.SameAs(restoredMap));
            Assert.That(RuntimeStores.GetRuntimeStore(unitsId, realm), Is.SameAs(restoredUnits));
            Assert.That(restoredMap.Retired, Is.False, "Disposal cannot abort an already published stage.");
            Assert.That(restoredUnits.Retired, Is.False);
            Assert.That(RuntimeStores.GetRuntimeStore(mapId, otherRealm), Is.SameAs(otherMap));
            Assert.That(RuntimeStores.GetRuntimeStore(unitsId, otherRealm), Is.SameAs(otherUnits));
            Assert.That(otherMap.Retired, Is.False);
            Assert.That(otherUnits.Retired, Is.False);
        }

        [Test]
        public void JournalMutationDuringCapture_DoesNotPublishMixedBoundary()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant
            {
                Value = 30,
            };
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                registry,
                journal,
                bus);

            bus.Drain(applyBeforeTick: 30);
            var validCheckpoint =
                coordinator.CaptureAtCompletedTick(completedTick: 30);
            var validBoundary = coordinator.CurrentBoundary;

            bus.Drain(applyBeforeTick: 31);
            participant.Value = 31;
            participant.JournalToMutate = journal;
            Assert.Throws<InvalidOperationException>(
                () => coordinator.CaptureAtCompletedTick(31));

            Assert.That(
                coordinator.CurrentCheckpoint,
                Is.SameAs(validCheckpoint));
            Assert.That(
                coordinator.CurrentBoundary.CompletedTick,
                Is.EqualTo(validBoundary.CompletedTick));
            Assert.That(
                journal.Cursor,
                Is.GreaterThan(validBoundary.JournalCursor));
        }

        [Test]
        public void RecoveryProvider_ReturnsBoundaryAndActualEnvelope()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant
            {
                Value = 42,
            };
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                registry,
                journal,
                bus);

            bus.Drain(applyBeforeTick: 42);
            var envelope = coordinator.CaptureAtCompletedTick(42);
            var recovery = coordinator.ProvideRecoveryCheckpoint();

            Assert.That(recovery, Is.Not.Null);
            Assert.That(recovery.Envelope, Is.SameAs(envelope));
            Assert.That(
                recovery.Boundary.CheckpointHash,
                Is.EqualTo(
                    RuntimeReplayHash.ToHex(
                        envelope.OverallHash)));
        }

        [Test]
        public void StoreSetJournalScope_MustExactlyMatchCheckpointStores()
        {
            Assert.Throws<ArgumentException>(
                () => new RuntimeDotsCheckpointGroup(
                    groupId: "test.scope",
                    storeScope: new RuntimeReplayStoreScope("map"),
                    journalScope:
                    new RuntimeCommandJournalScope("map", "units"),
                    retentionPolicy: RetentionPolicy()));
        }

        [Test]
        public void StoreRevisionDrift_DisablesRetainedRecoveryBoundary()
        {
            var storeId = new FixedString32Bytes("map");
            var store = RuntimeStores.GetOrAddRuntimeStore(storeId);
            var participant = new RuntimeDotsCheckpointTestParticipant();
            var registry = CreateRegistry(
                participant,
                CreateStoreParticipant("map"));
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = new RuntimeDotsCheckpointCoordinator(
                CreateGroup(
                    "test.store-drift",
                    new RuntimeReplayStoreScope("map"),
                    new RuntimeCommandJournalScope("map")),
                bus,
                journal,
                registry,
                _world,
                StoreRealm.Server);

            bus.Drain(applyBeforeTick: 40);
            coordinator.CaptureAtCompletedTick(40);
            Assert.That(
                coordinator.TryTakeRecoveryBoundary(out _),
                Is.True);

            store.Create();
            Assert.That(
                coordinator.TryTakeRecoveryBoundary(out _),
                Is.False);
            Assert.That(
                coordinator.ProvideRecoveryCheckpoint(),
                Is.Null);
        }

        [Test]
        public void SessionWideBoundary_DetectsRuntimeStoreMembershipDrift()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant();
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                registry,
                journal,
                bus);

            bus.Drain(applyBeforeTick: 50);
            coordinator.CaptureAtCompletedTick(50);
            Assert.That(
                coordinator.TryTakeRecoveryBoundary(out _),
                Is.True);

            RuntimeStores.GetOrAddRuntimeStore(
                new FixedString32Bytes("late-store"));
            Assert.That(
                coordinator.TryTakeRecoveryBoundary(out _),
                Is.False);
        }

        private RuntimeDotsCheckpointCoordinator CreateCoordinator(
            RuntimeReplayCheckpointRegistry registry,
            RuntimeCommandJournal journal,
            RuntimeCommandsBus bus)
        {
            return new RuntimeDotsCheckpointCoordinator(
                CreateGroup(
                    "test.hybrid",
                    null,
                    RuntimeCommandJournalScope.Session),
                bus,
                journal,
                registry,
                _world,
                StoreRealm.Server);
        }

        private static RuntimeDotsCheckpointGroup CreateGroup(
            string groupId,
            RuntimeReplayStoreScope storeScope,
            RuntimeCommandJournalScope journalScope)
        {
            return new RuntimeDotsCheckpointGroup(
                groupId,
                storeScope,
                journalScope,
                RetentionPolicy());
        }

        private static RuntimeCommandJournalRetentionPolicy
            RetentionPolicy()
        {
            return new RuntimeCommandJournalRetentionPolicy(
                maxEntries: 128,
                maxPayloadBytes: 1024 * 1024,
                maxAgeSeconds: 60d);
        }

        private RuntimeStoresReplayCheckpointParticipant
            CreateStoreParticipant(params string[] storeIds)
            => CreateStoreParticipant(StoreRealm.Server, storeIds);

        private RuntimeStoresReplayCheckpointParticipant
            CreateStoreParticipant(StoreRealm realm, params string[] storeIds)
        {
            var assetLock = new GameAssetLibraryLock().Seal();
            var patchCodecs = new RuntimePatchCodecRegistry(
                "dots-checkpoint-read-only-tests");
            var templates = new GameAssetTemplateCache(
                patchCodecs,
                RuntimeTemplatePatchCodecContext.Instance);
            return new RuntimeStoresReplayCheckpointParticipant(
                _world,
                realm,
                assetLock,
                templates,
                new RuntimeReplayStoreScope(storeIds));
        }

        private static RuntimeReplayCheckpointRegistry CreateRegistry(
            params IRuntimeReplayCheckpointParticipant[] participants)
        {
            var registry = new RuntimeReplayCheckpointRegistry();
            for (var i = 0; i < participants.Length; i++)
            {
                registry.RegisterParticipant(participants[i]);
            }
            registry.Seal();
            return registry;
        }

        private void EnsureCoroutineParent()
        {
            if (CoroutineParent.GetNoCheck() != null)
            {
                return;
            }

            _ownedCoroutineParent = new GameObject(
                $"{nameof(RuntimeDotsCheckpointTests)} CoroutineParent");
            _ownedCoroutineParent.AddComponent<CoroutineParent>();
        }
    }
}
