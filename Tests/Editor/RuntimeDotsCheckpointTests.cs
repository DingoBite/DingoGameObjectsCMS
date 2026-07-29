using System;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
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

        public uint SectionId => SECTION_ID;
        public uint CurrentVersion => 1u;

        public void Capture(RuntimeReplayCheckpointWriter writer)
        {
            CaptureCalls++;
            writer.WriteInt32(Value);
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

    public class RuntimeDotsCheckpointTestExporter :
        IRuntimeDotsCheckpointExporter
    {
        private readonly RuntimeDotsCheckpointTestParticipant _participant;

        public uint ExporterId { get; }
        public int ExportCalls { get; private set; }
        public bool FailExport { get; set; }
        public RuntimeCommandJournal JournalToMutate { get; set; }

        public RuntimeDotsCheckpointTestExporter(
            uint exporterId,
            RuntimeDotsCheckpointTestParticipant participant)
        {
            ExporterId = exporterId;
            _participant = participant;
        }

        public void Export(in RuntimeDotsCheckpointContext context)
        {
            ExportCalls++;
            if (FailExport)
            {
                throw new InvalidOperationException(
                    "Intentional DOTS checkpoint export failure.");
            }

            _participant.Value = checked((int)context.CompletedTick);
            JournalToMutate?.AppendOutcomeBatch(
                context.CompletedTick,
                new RuntimeEncodedCommand(
                    typeId: 1u,
                    codecVersion: 1,
                    payload: new byte[] { 1 }),
                RuntimeCommandJournalScope.Session);
        }
    }

    public class RuntimeDotsCheckpointStoreMutationExporter :
        IRuntimeDotsCheckpointExporter
    {
        public uint ExporterId { get; }
        public FixedString32Bytes StoreId { get; }
        public bool Enabled { get; set; }
        public RuntimeStore MutatedStore { get; private set; }

        public RuntimeDotsCheckpointStoreMutationExporter(
            uint exporterId,
            FixedString32Bytes storeId)
        {
            ExporterId = exporterId;
            StoreId = storeId;
        }

        public void Export(in RuntimeDotsCheckpointContext context)
        {
            if (!Enabled)
            {
                return;
            }

            MutatedStore = context.TakeStore(StoreId);
            MutatedStore.Create();
            MutatedStore.FlushToQuiescence();
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
        public void CaptureAtCompletedTick_ExportsOnlyAtMatchingExternalBarrier()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant();
            var exporter = new RuntimeDotsCheckpointTestExporter(
                exporterId: 7u,
                participant);
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                exporter,
                registry,
                journal,
                bus);

            Assert.Throws<InvalidOperationException>(
                () => coordinator.CaptureAtCompletedTick(12));
            Assert.That(exporter.ExportCalls, Is.Zero);
            Assert.That(participant.CaptureCalls, Is.Zero);

            bus.Drain(applyBeforeTick: 11);
            Assert.Throws<InvalidOperationException>(
                () => coordinator.CaptureAtCompletedTick(12));
            Assert.That(exporter.ExportCalls, Is.Zero);
            Assert.That(participant.CaptureCalls, Is.Zero);

            bus.Drain(applyBeforeTick: 12);
            var checkpoint =
                coordinator.CaptureAtCompletedTick(completedTick: 12);

            Assert.That(exporter.ExportCalls, Is.EqualTo(1));
            Assert.That(participant.CaptureCalls, Is.EqualTo(1));
            Assert.That(participant.Value, Is.EqualTo(12));
            Assert.That(checkpoint.CompletedTick, Is.EqualTo(12));
            Assert.That(checkpoint.Cursor, Is.EqualTo(journal.Cursor));
            Assert.That(coordinator.HasCheckpoint, Is.True);
            Assert.That(
                coordinator.CurrentBoundary.CheckpointHash,
                Is.EqualTo(RuntimeReplayHash.ToHex(checkpoint.OverallHash)));
        }

        [Test]
        public void FailedCapture_KeepsLastValidCheckpointAndBoundary()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant();
            var exporter = new RuntimeDotsCheckpointTestExporter(
                exporterId: 1u,
                participant);
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                exporter,
                registry,
                journal,
                bus);

            bus.Drain(applyBeforeTick: 20);
            var validCheckpoint =
                coordinator.CaptureAtCompletedTick(completedTick: 20);
            var validBoundary = coordinator.CurrentBoundary;

            exporter.FailExport = true;
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
            Assert.That(participant.CaptureCalls, Is.EqualTo(1));
        }

        [Test]
        public void SecondExporterFailure_DiscardsMutatedStoreStageAndKeepsPreviousRecovery()
        {
            var storeId = new FixedString32Bytes("map");
            RuntimeStores.GetOrAddRuntimeStore(storeId);
            var participant = new RuntimeDotsCheckpointTestParticipant();
            var mutationExporter =
                new RuntimeDotsCheckpointStoreMutationExporter(
                    exporterId: 1u,
                    storeId);
            var failingExporter = new RuntimeDotsCheckpointTestExporter(
                exporterId: 2u,
                participant);
            var registry = CreateRegistry(
                participant,
                CreateStoreParticipant("map"));
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var group = new RuntimeDotsCheckpointGroup(
                groupId: "test.transactional-store",
                storeScope: new RuntimeReplayStoreScope("map"),
                journalScope:
                new RuntimeCommandJournalScope("map"),
                retentionPolicy:
                new RuntimeCommandJournalRetentionPolicy(
                    maxEntries: 128,
                    maxPayloadBytes: 1024 * 1024,
                    maxAgeSeconds: 60d),
                exporters: new IRuntimeDotsCheckpointExporter[]
                {
                    mutationExporter,
                    failingExporter,
                });
            var coordinator = new RuntimeDotsCheckpointCoordinator(
                group,
                bus,
                journal,
                registry,
                _world,
                StoreRealm.Server);

            bus.Drain(applyBeforeTick: 10);
            var validCheckpoint =
                coordinator.CaptureAtCompletedTick(completedTick: 10);
            var validBoundary = coordinator.CurrentBoundary;
            var activeBeforeFailure =
                RuntimeStores.GetRuntimeStore(
                    storeId,
                    StoreRealm.Server);
            var epochBeforeFailure = activeBeforeFailure.Epoch;
            var generationBeforeFailure =
                activeBeforeFailure.StoreGeneration;
            var revisionBeforeFailure =
                activeBeforeFailure.StoreRevision;
            Assert.That(activeBeforeFailure.Entries.Count, Is.Zero);
            Assert.That(
                coordinator.TryTakeRecoveryBoundary(out _),
                Is.True);

            mutationExporter.Enabled = true;
            failingExporter.FailExport = true;
            bus.Drain(applyBeforeTick: 11);

            Assert.Throws<InvalidOperationException>(
                () => coordinator.CaptureAtCompletedTick(11));

            var activeAfterFailure =
                RuntimeStores.GetRuntimeStore(
                    storeId,
                    StoreRealm.Server);
            Assert.That(
                activeAfterFailure,
                Is.SameAs(activeBeforeFailure));
            Assert.That(activeAfterFailure.Retired, Is.False);
            Assert.That(
                activeAfterFailure.Epoch,
                Is.EqualTo(epochBeforeFailure));
            Assert.That(
                activeAfterFailure.StoreGeneration,
                Is.EqualTo(generationBeforeFailure));
            Assert.That(
                activeAfterFailure.StoreRevision,
                Is.EqualTo(revisionBeforeFailure));
            Assert.That(activeAfterFailure.Entries.Count, Is.Zero);
            Assert.That(
                mutationExporter.MutatedStore,
                Is.Not.SameAs(activeBeforeFailure));
            Assert.That(mutationExporter.MutatedStore.Retired, Is.True);
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
            Assert.That(
                coordinator.TryTakeRecoveryBoundary(
                    out var retainedBoundary),
                Is.True);
            Assert.That(
                retainedBoundary.CheckpointHash,
                Is.EqualTo(validBoundary.CheckpointHash));
        }

        [Test]
        public void JournalMutationDuringCapture_DoesNotPublishMixedBoundary()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant();
            var exporter = new RuntimeDotsCheckpointTestExporter(
                exporterId: 1u,
                participant);
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                exporter,
                registry,
                journal,
                bus);

            bus.Drain(applyBeforeTick: 30);
            var validCheckpoint =
                coordinator.CaptureAtCompletedTick(completedTick: 30);
            var validBoundary = coordinator.CurrentBoundary;

            bus.Drain(applyBeforeTick: 31);
            exporter.JournalToMutate = journal;
            Assert.Throws<InvalidOperationException>(
                () => coordinator.CaptureAtCompletedTick(31));

            Assert.That(
                coordinator.CurrentCheckpoint,
                Is.SameAs(validCheckpoint));
            Assert.That(
                coordinator.CurrentBoundary.CompletedTick,
                Is.EqualTo(validBoundary.CompletedTick));
            Assert.That(journal.Cursor, Is.GreaterThan(validBoundary.JournalCursor));
        }

        [Test]
        public void StoreSetJournalScope_MustExactlyMatchCheckpointStores()
        {
            var retention = new RuntimeCommandJournalRetentionPolicy(
                maxEntries: 10,
                maxPayloadBytes: 1024,
                maxAgeSeconds: 60d);

            Assert.Throws<ArgumentException>(
                () => new RuntimeDotsCheckpointGroup(
                    groupId: "test.scope",
                    storeScope: new RuntimeReplayStoreScope("map"),
                    journalScope:
                    new RuntimeCommandJournalScope("map", "units"),
                    retentionPolicy: retention));
        }

        [Test]
        public void StoreRevisionDrift_DisablesRetainedRecoveryBoundary()
        {
            var store = RuntimeStores.GetOrAddRuntimeStore(
                new Unity.Collections.FixedString32Bytes("map"));
            var participant = new RuntimeDotsCheckpointTestParticipant();
            var exporter = new RuntimeDotsCheckpointTestExporter(
                exporterId: 1u,
                participant);
            var registry = CreateRegistry(
                participant,
                CreateStoreParticipant("map"));
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var group = new RuntimeDotsCheckpointGroup(
                groupId: "test.store-drift",
                storeScope: new RuntimeReplayStoreScope("map"),
                journalScope:
                new RuntimeCommandJournalScope("map"),
                retentionPolicy:
                new RuntimeCommandJournalRetentionPolicy(
                    maxEntries: 32,
                    maxPayloadBytes: 1024,
                    maxAgeSeconds: 60d),
                exporters: new[] { exporter });
            var coordinator = new RuntimeDotsCheckpointCoordinator(
                group,
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

            store = RuntimeStores.GetRuntimeStore(
                new FixedString32Bytes("map"),
                StoreRealm.Server);
            store.Create();

            Assert.That(
                coordinator.TryTakeRecoveryBoundary(out _),
                Is.False);
            Assert.That(
                coordinator.ProvideRecoveryBoundary(),
                Is.Null);
        }

        [Test]
        public void SessionWideBoundary_DetectsRuntimeStoreMembershipDrift()
        {
            var participant = new RuntimeDotsCheckpointTestParticipant();
            var exporter = new RuntimeDotsCheckpointTestExporter(
                exporterId: 1u,
                participant);
            var registry = CreateRegistry(participant);
            var journal = new RuntimeCommandJournal();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                journal: journal);
            var coordinator = CreateCoordinator(
                exporter,
                registry,
                journal,
                bus);

            bus.Drain(applyBeforeTick: 50);
            coordinator.CaptureAtCompletedTick(50);
            Assert.That(
                coordinator.TryTakeRecoveryBoundary(out _),
                Is.True);

            RuntimeStores.GetOrAddRuntimeStore(
                new Unity.Collections.FixedString32Bytes("late-store"));

            Assert.That(
                coordinator.TryTakeRecoveryBoundary(out _),
                Is.False);
        }

        private RuntimeDotsCheckpointCoordinator CreateCoordinator(
            IRuntimeDotsCheckpointExporter exporter,
            RuntimeReplayCheckpointRegistry registry,
            RuntimeCommandJournal journal,
            RuntimeCommandsBus bus)
        {
            var group = new RuntimeDotsCheckpointGroup(
                groupId: "test.hybrid",
                storeScope: null,
                journalScope: RuntimeCommandJournalScope.Session,
                retentionPolicy:
                new RuntimeCommandJournalRetentionPolicy(
                    maxEntries: 128,
                    maxPayloadBytes: 1024 * 1024,
                    maxAgeSeconds: 60d),
                exporters: new[] { exporter });
            return new RuntimeDotsCheckpointCoordinator(
                group,
                bus,
                journal,
                registry,
                _world,
                StoreRealm.Server);
        }

        private RuntimeStoresReplayCheckpointParticipant
            CreateStoreParticipant(params string[] storeIds)
        {
            var assetLock = new GameAssetLibraryLock().Seal();
            var patchCodecs = new RuntimePatchCodecRegistry(
                "dots-checkpoint-transaction-tests");
            var templates = new GameAssetTemplateCache(
                patchCodecs,
                RuntimeTemplatePatchCodecContext.Instance);
            return new RuntimeStoresReplayCheckpointParticipant(
                _world,
                StoreRealm.Server,
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
