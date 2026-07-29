using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Mirror.V2;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
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
    public class RuntimeCommandJournalNetworkTestCommand : GameRuntimeComponent, ICommandLogic
    {
        public static int ExecutionCount;
        public static bool ThrowOnExecute;

        public int Value;

        public void Execute(GameRuntimeCommand command)
        {
            if (ThrowOnExecute)
            {
                throw new InvalidOperationException(
                    "Expected journal apply failure.");
            }

            ExecutionCount++;
            Value++;
        }
    }

    public class RuntimeCommandJournalNetworkTests
    {
        private const uint COMMAND_TYPE_ID = 4301;
        private const string CHECKPOINT_HASH =
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string NEXT_CHECKPOINT_HASH =
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        private const string THIRD_CHECKPOINT_HASH =
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";

        private World _world;
        private GameObject _ownedCoroutineParent;

        [SetUp]
        public void SetUp()
        {
            RuntimeCommandJournalNetworkTestCommand.ExecutionCount = 0;
            RuntimeCommandJournalNetworkTestCommand.ThrowOnExecute = false;
            RuntimeStores.ResetState();
            RuntimeExecutionContext.ResetState();
            EnsureCoroutineParent();
            _world = new World(nameof(RuntimeCommandJournalNetworkTests));
            RuntimeStores.SetupWorld(_world);
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeStores.ResetState();
            RuntimeExecutionContext.ResetState();
            if (_world != null && _world.IsCreated)
            {
                _world.Dispose();
            }
            if (_ownedCoroutineParent != null)
            {
                UnityEngine.Object.DestroyImmediate(
                    _ownedCoroutineParent);
            }
        }

        [Test]
        public void ProtocolV3_RejectsV2Descriptor()
        {
            var expected = CreateDescriptor(RuntimeProtocolV2.VERSION);
            var actual = CreateDescriptor(2);

            Assert.That(RuntimeProtocolV2.VERSION, Is.EqualTo(3));
            Assert.That(
                RuntimeSessionCompatibility.Validate(expected, actual),
                Is.EqualTo(RuntimeProtocolRejectCode.ProtocolVersionMismatch));
        }

        [Test]
        public void BaselineChunks_CarryOneCheckpointBoundaryAndRejectMixedTransfer()
        {
            var store = new NetStoreRef(
                new FixedString32Bytes("map"),
                1);
            var boundary = new RuntimeCheckpointBoundary(
                "world",
                10,
                7,
                CHECKPOINT_HASH);
            var chunks = RuntimeBaselineChunker.Split(
                1,
                store,
                1,
                1,
                0,
                new byte[RuntimeProtocolV2.BASELINE_CHUNK_BYTES + 1],
                boundary);
            var assembler = new RuntimeBaselineChunkAssembler();

            Assert.That(chunks.Count, Is.EqualTo(2));
            Assert.That(chunks[0].CheckpointGroupId, Is.EqualTo("world"));
            Assert.That(chunks[0].CompletedTick, Is.EqualTo(10));
            Assert.That(chunks[0].JournalCursor, Is.EqualTo(7));
            Assert.That(chunks[0].CheckpointHash, Is.EqualTo(CHECKPOINT_HASH));
            Assert.That(
                assembler.Accept(chunks[0], 0d, out _),
                Is.EqualTo(RuntimeBaselineChunkResult.Accepted));

            var conflicting = chunks[1];
            conflicting.CheckpointHash =
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            Assert.That(
                assembler.Accept(conflicting, 0d, out _),
                Is.EqualTo(RuntimeBaselineChunkResult.ConflictingTransfer));
        }

        [Test]
        public void Catchup_AppliesOrderedEntriesAndAdvancesOnlyToBatchHighWater()
        {
            var bus = CreateBus();
            var boundary = new RuntimeCheckpointBoundary(
                "world",
                4,
                0,
                CHECKPOINT_HASH);
            var receiver = new RuntimeCommandJournalCatchupReceiver(
                1,
                boundary,
                new RuntimeCommandJournalScope("map"),
                bus);
            var entries = new[]
            {
                Entry(5, 1, "map", 10),
                Entry(5, 3, "map", 20),
            };
            var batch = new RuntimeCommandJournalBatch(
                1,
                "world",
                CHECKPOINT_HASH,
                0,
                4,
                entries,
                completesCatchup: true);

            var result = receiver.Receive(batch);

            Assert.That(result.Status, Is.EqualTo(RuntimeCommandJournalReceiveStatus.Applied));
            Assert.That(result.AppliedEntryCount, Is.EqualTo(2));
            Assert.That(receiver.Cursor, Is.EqualTo(4));
            Assert.That(receiver.LastApplyBeforeTick, Is.EqualTo(5));
            Assert.That(receiver.NeedsResync, Is.False);
        }

        [Test]
        public void Catchup_GapCheckpointScopeAndTickFailuresRequireResync()
        {
            var boundary = new RuntimeCheckpointBoundary(
                "world",
                5,
                0,
                CHECKPOINT_HASH);

            AssertReceiveFailure(
                boundary,
                new RuntimeCommandJournalBatch(
                    1,
                    "world",
                    CHECKPOINT_HASH,
                    2,
                    3,
                    new[] { Entry(6, 3, "map", 1) }),
                RuntimeCommandJournalReceiveStatus.CursorGap);
            AssertReceiveFailure(
                boundary,
                new RuntimeCommandJournalBatch(
                    1,
                    "world",
                    "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                    0,
                    1,
                    new[] { Entry(6, 1, "map", 1) }),
                RuntimeCommandJournalReceiveStatus.CheckpointMismatch);
            AssertReceiveFailure(
                boundary,
                new RuntimeCommandJournalBatch(
                    1,
                    "world",
                    CHECKPOINT_HASH,
                    0,
                    1,
                    new[] { Entry(6, 1, new[] { "map", "units" }, 1) }),
                RuntimeCommandJournalReceiveStatus.ScopeMismatch);
            AssertReceiveFailure(
                boundary,
                new RuntimeCommandJournalBatch(
                    1,
                    "world",
                    CHECKPOINT_HASH,
                    0,
                    1,
                    new[] { Entry(4, 1, "map", 1) }),
                RuntimeCommandJournalReceiveStatus.InvalidBatch);
            AssertReceiveFailure(
                boundary,
                new RuntimeCommandJournalBatch(
                    1,
                    "world",
                    CHECKPOINT_HASH,
                    0,
                    1,
                    new[]
                    {
                        new RuntimeCommandJournalEntry(
                            6,
                            1,
                            new RuntimeEncodedCommand(
                                999,
                                1,
                                new byte[] { 1 }),
                            new RuntimeCommandJournalScope("map")),
                    }),
                RuntimeCommandJournalReceiveStatus.UnknownCodec);
        }

        [Test]
        public void HardOverflow_ClosesRecoveryReadButLiveEntryStillAdvancesCaughtUpReceiver()
        {
            var journal = new RuntimeCommandJournal(() => 0d);
            journal.ConfigureWindow(
                "world",
                new RuntimeCommandJournalScope("map"),
                new RuntimeCommandJournalRetentionPolicy(
                    maxEntries: 1,
                    maxPayloadBytes: 1_000,
                    maxAgeSeconds: 100d,
                    checkpointWarningRatio: 0.5d));
            RuntimeCommandJournalEntry live = default;
            journal.EntryRecorded += entry => live = entry;

            journal.AppendInput(
                1,
                Encode(1),
                new RuntimeCommandJournalScope("map"));
            journal.AppendInput(
                1,
                Encode(2),
                new RuntimeCommandJournalScope("map"));
            Assert.That(
                journal.ReadAfter("world", 0).Status,
                Is.EqualTo(RuntimeCommandJournalReadStatus.RecoveryUnavailable));

            journal.AppendInput(
                1,
                Encode(3),
                new RuntimeCommandJournalScope("map"));
            Assert.That(live.Sequence, Is.EqualTo(3));

            var boundary = new RuntimeCheckpointBoundary(
                "world",
                0,
                0,
                CHECKPOINT_HASH);
            var receiver = new RuntimeCommandJournalCatchupReceiver(
                1,
                boundary,
                new RuntimeCommandJournalScope("map"),
                CreateBus());
            receiver.Receive(new RuntimeCommandJournalBatch(
                1,
                "world",
                CHECKPOINT_HASH,
                0,
                2,
                new[]
                {
                    Entry(1, 1, "map", 1),
                    Entry(1, 2, "map", 2),
                }));

            var result = receiver.Receive(new RuntimeCommandJournalBatch(
                1,
                "world",
                CHECKPOINT_HASH,
                2,
                3,
                new[] { live }));

            Assert.That(result.Status, Is.EqualTo(RuntimeCommandJournalReceiveStatus.Applied));
            Assert.That(receiver.Cursor, Is.EqualTo(3));
        }

        [Test]
        public void ClientCoordinator_PublishesTwoStoreBaselineAtomicallyAndBecomesReadyOnlyAfterFinalJournalBatch()
        {
            var stores = new[]
            {
                new NetStoreRef(new FixedString32Bytes("map"), 1),
                new NetStoreRef(new FixedString32Bytes("units"), 1),
            };
            var fixture = CreateProtocolFixture(stores);
            var bus = CreateBus();
            var playbackCount = 0;
            var readyChanges = new List<bool>();
            var journalResyncs =
                new List<RtCommandJournalResyncData>();
            using var coordinator =
                new RuntimeProtocolV2ClientCoordinator(
                    fixture.CreateClientContext(
                        bus,
                        new RuntimeCommandJournalScope(
                            "map",
                            "units"),
                        _ => playbackCount++),
                    new RuntimeProtocolV2ClientOutput(
                        (_, _) => { },
                        _ => { },
                        (_, _) => { },
                        _ => { },
                        _ => { },
                        _ => { },
                        journalResyncs.Add),
                    clientNonce: 17);
            coordinator.ReplicaReadyChanged +=
                readyChanges.Add;

            var session = BeginClientSession(
                coordinator,
                fixture,
                sessionId: 31);
            var boundary = new RuntimeCheckpointBoundary(
                "world",
                5,
                0,
                CHECKPOINT_HASH);

            var firstResult = SendBaseline(
                coordinator,
                fixture,
                session.SessionId,
                stores[0],
                baselineId: 1,
                deliverySequence: 1,
                boundary);

            Assert.That(
                firstResult.Kind,
                Is.EqualTo(
                    RuntimeClientReceiveResultKind.Buffered));
            Assert.That(
                RuntimeStores.TryGetRuntimeStore(
                    stores[0].StoreId,
                    stores[0].StoreGeneration,
                    StoreRealm.Client,
                    out _),
                Is.False,
                "No prefix of a grouped baseline may become visible.");

            var secondResult = SendBaseline(
                coordinator,
                fixture,
                session.SessionId,
                stores[1],
                baselineId: 1,
                deliverySequence: 1,
                boundary);

            Assert.That(
                secondResult.Kind,
                Is.EqualTo(
                    RuntimeClientReceiveResultKind.Buffered));
            Assert.That(
                RuntimeStores.TryGetRuntimeStore(
                    stores[0].StoreId,
                    stores[0].StoreGeneration,
                    StoreRealm.Client,
                    out _),
                Is.True);
            Assert.That(
                RuntimeStores.TryGetRuntimeStore(
                    stores[1].StoreId,
                    stores[1].StoreGeneration,
                    StoreRealm.Client,
                    out _),
                Is.True);
            Assert.That(coordinator.IsReplicaReady, Is.False);
            Assert.That(playbackCount, Is.Zero);

            var firstBatchResult =
                coordinator.ReceiveJournalBatch(
                    new RuntimeCommandJournalBatch(
                        session.SessionId,
                        boundary.GroupId,
                        boundary.CheckpointHash,
                        0,
                        1,
                        new[]
                        {
                            Entry(6, 1, "map", 1),
                        },
                        completesCatchup: false));

            Assert.That(
                firstBatchResult.Status,
                Is.EqualTo(
                    RuntimeCommandJournalReceiveStatus.Applied));
            Assert.That(playbackCount, Is.EqualTo(1));
            Assert.That(
                RuntimeCommandJournalNetworkTestCommand
                    .ExecutionCount,
                Is.EqualTo(1));
            Assert.That(coordinator.IsReplicaReady, Is.False);
            Assert.That(readyChanges, Is.Empty);

            var finalBatchResult =
                coordinator.ReceiveJournalBatch(
                    new RuntimeCommandJournalBatch(
                        session.SessionId,
                        boundary.GroupId,
                        boundary.CheckpointHash,
                        1,
                        2,
                        new[]
                        {
                            Entry(7, 2, "units", 2),
                        },
                        completesCatchup: true));

            Assert.That(
                finalBatchResult.Status,
                Is.EqualTo(
                    RuntimeCommandJournalReceiveStatus.Applied));
            Assert.That(playbackCount, Is.EqualTo(2));
            Assert.That(
                RuntimeCommandJournalNetworkTestCommand
                    .ExecutionCount,
                Is.EqualTo(2));
            Assert.That(coordinator.IsReplicaReady, Is.True);
            Assert.That(readyChanges, Is.EqualTo(new[] { true }));
            Assert.That(journalResyncs, Is.Empty);
        }

        [Test]
        public void ClientCoordinator_MixedCheckpointBaselineRequestsWholeGroupResync()
        {
            var stores = new[]
            {
                new NetStoreRef(
                    new FixedString32Bytes("map"),
                    1),
                new NetStoreRef(
                    new FixedString32Bytes("units"),
                    1),
            };
            var fixture = CreateProtocolFixture(stores);
            var storeResyncs = new List<RtStoreResyncData>();
            using var coordinator =
                new RuntimeProtocolV2ClientCoordinator(
                    fixture.CreateClientContext(
                        CreateBus(),
                        new RuntimeCommandJournalScope(
                            "map",
                            "units"),
                        _ => { }),
                    new RuntimeProtocolV2ClientOutput(
                        (_, _) => { },
                        _ => { },
                        (_, _) => { },
                        _ => { },
                        storeResyncs.Add,
                        _ => { },
                        _ => { }),
                    clientNonce: 19);
            var session = BeginClientSession(
                coordinator,
                fixture,
                sessionId: 32);
            var firstBoundary =
                new RuntimeCheckpointBoundary(
                    "world",
                    5,
                    0,
                    CHECKPOINT_HASH);
            var conflictingBoundary =
                new RuntimeCheckpointBoundary(
                    "world",
                    5,
                    0,
                    NEXT_CHECKPOINT_HASH);

            Assert.That(
                SendBaseline(
                    coordinator,
                    fixture,
                    session.SessionId,
                    stores[0],
                    baselineId: 1,
                    deliverySequence: 1,
                    firstBoundary).Kind,
                Is.EqualTo(
                    RuntimeClientReceiveResultKind.Buffered));
            var conflict = SendBaseline(
                coordinator,
                fixture,
                session.SessionId,
                stores[1],
                baselineId: 1,
                deliverySequence: 1,
                conflictingBoundary);

            Assert.That(
                conflict.Kind,
                Is.EqualTo(
                    RuntimeClientReceiveResultKind.ResyncRequested));
            Assert.That(storeResyncs, Has.Count.EqualTo(2));
            Assert.That(
                storeResyncs.Select(value => value.Store),
                Is.EquivalentTo(stores));
            Assert.That(
                storeResyncs.All(value => value.BaselineId == 0),
                Is.True);
            Assert.That(coordinator.IsReplicaReady, Is.False);
        }

        [Test]
        public void ClientCoordinator_EmptyTerminalMarkerCompletesPlaybackAndReadiness()
        {
            var store = new NetStoreRef(
                new FixedString32Bytes("map"),
                1);
            var fixture = CreateProtocolFixture(store);
            var bus = CreateBus();
            var playbackCount = 0;
            using var coordinator =
                new RuntimeProtocolV2ClientCoordinator(
                    fixture.CreateClientContext(
                        bus,
                        new RuntimeCommandJournalScope("map"),
                        _ => playbackCount++),
                    new RuntimeProtocolV2ClientOutput(
                        (_, _) => { },
                        _ => { },
                        (_, _) => { },
                        _ => { },
                        _ => { },
                        _ => { },
                        _ => { }),
                    clientNonce: 18);
            var session = BeginClientSession(
                coordinator,
                fixture,
                sessionId: 32);
            var boundary = new RuntimeCheckpointBoundary(
                "world",
                5,
                0,
                CHECKPOINT_HASH);

            SendBaseline(
                coordinator,
                fixture,
                session.SessionId,
                store,
                baselineId: 1,
                deliverySequence: 1,
                boundary);
            Assert.That(coordinator.IsReplicaReady, Is.False);

            var result = coordinator.ReceiveJournalBatch(
                new RuntimeCommandJournalBatch(
                    session.SessionId,
                    boundary.GroupId,
                    boundary.CheckpointHash,
                    boundary.JournalCursor,
                    boundary.JournalCursor,
                    Array.Empty<RuntimeCommandJournalEntry>(),
                    completesCatchup: true));

            Assert.That(
                result.Status,
                Is.EqualTo(
                    RuntimeCommandJournalReceiveStatus.Applied));
            Assert.That(playbackCount, Is.EqualTo(1));
            Assert.That(coordinator.IsReplicaReady, Is.True);
        }

        [Test]
        public void ClientCoordinator_ApplyFailureRequestsForcedCheckpointBaseline()
        {
            var store = new NetStoreRef(
                new FixedString32Bytes("map"),
                1);
            var fixture = CreateProtocolFixture(store);
            var bus = CreateBus();
            var journalResyncs =
                new List<RtCommandJournalResyncData>();
            using var coordinator =
                new RuntimeProtocolV2ClientCoordinator(
                    fixture.CreateClientContext(
                        bus,
                        new RuntimeCommandJournalScope("map"),
                        _ => { }),
                    new RuntimeProtocolV2ClientOutput(
                        (_, _) => { },
                        _ => { },
                        (_, _) => { },
                        _ => { },
                        _ => { },
                        _ => { },
                        journalResyncs.Add),
                    clientNonce: 19);
            var session = BeginClientSession(
                coordinator,
                fixture,
                sessionId: 33);
            var boundary = new RuntimeCheckpointBoundary(
                "world",
                5,
                0,
                CHECKPOINT_HASH);
            SendBaseline(
                coordinator,
                fixture,
                session.SessionId,
                store,
                baselineId: 1,
                deliverySequence: 1,
                boundary);
            RuntimeCommandJournalNetworkTestCommand
                .ThrowOnExecute = true;

            var result = coordinator.ReceiveJournalBatch(
                new RuntimeCommandJournalBatch(
                    session.SessionId,
                    boundary.GroupId,
                    boundary.CheckpointHash,
                    0,
                    1,
                    new[]
                    {
                        Entry(6, 1, "map", 1),
                    },
                    completesCatchup: true));

            Assert.That(
                result.Status,
                Is.EqualTo(
                    RuntimeCommandJournalReceiveStatus.ApplyFailed));
            Assert.That(coordinator.IsReplicaReady, Is.False);
            Assert.That(journalResyncs, Has.Count.EqualTo(1));
            Assert.That(
                journalResyncs[0].ForceCheckpointBaseline,
                Is.True);
            Assert.That(
                journalResyncs[0].ExpectedCursor,
                Is.EqualTo(0));
            Assert.That(
                journalResyncs[0].CheckpointHash,
                Is.EqualTo(CHECKPOINT_HASH));
        }

        [Test]
        public void ServerCoordinator_DeliversLiveEntriesAfterHardOverflowAndCanRebaselineWholeCheckpointGroup()
        {
            var map = RegisterServerStore("map");
            var units = RegisterServerStore("units");
            var stores = new[] { map, units };
            var fixture = CreateProtocolFixture(stores);
            var journal = new RuntimeCommandJournal(() => 0d);
            var scope = new RuntimeCommandJournalScope(
                "map",
                "units");
            journal.ConfigureWindow(
                "world",
                scope,
                new RuntimeCommandJournalRetentionPolicy(
                    maxEntries: 1,
                    maxPayloadBytes: 1_000,
                    maxAgeSeconds: 100d,
                    checkpointWarningRatio: 0.5d));
            var bus = CreateBus(journal);
            RuntimeCheckpointBoundary? providedBoundary =
                new RuntimeCheckpointBoundary(
                    "world",
                    0,
                    0,
                    CHECKPOINT_HASH);
            var manifests =
                new List<RuntimeSessionManifestSnapshot>();
            var baselines = new List<RuntimeBaselineChunk>();
            var batches =
                new List<RuntimeCommandJournalBatch>();
            var rejects =
                new List<RuntimeProtocolRejectCode>();
            using var coordinator =
                new RuntimeProtocolV2ServerCoordinator(
                    fixture.CreateServerContext(
                        bus,
                        scope,
                        () => providedBoundary),
                    new RuntimeProtocolV2ServerOutput(
                        (_, manifest) =>
                            manifests.Add(manifest),
                        (_, code, _) => rejects.Add(code),
                        (_, chunk) => baselines.Add(chunk),
                        (_, _) => { },
                        (_, _) => { },
                        (in RuntimeReliableDeltaTransportEnvelope _) =>
                            true,
                        journalBatch: (_, batch) =>
                            batches.Add(batch)),
                    firstSessionId: 71);
            const int connectionId = 9;
            coordinator.AddConnection(connectionId);

            Assert.That(
                coordinator.ReceiveHello(
                    connectionId,
                    fixture.Manifest.Descriptor,
                    clientNonce: 81).Accepted,
                Is.True);
            var session = manifests.Single();
            Assert.That(
                coordinator.ReceiveReady(
                    connectionId,
                    session.SessionId).Accepted,
                Is.True);
            Assert.That(
                batches,
                Has.Count.EqualTo(1));
            Assert.That(batches[0].CompletesCatchup, Is.True);
            Assert.That(batches[0].Entries, Is.Empty);

            journal.AppendInput(
                1,
                Encode(1),
                new RuntimeCommandJournalScope("map"));
            journal.AppendInput(
                1,
                Encode(2),
                new RuntimeCommandJournalScope("units"));

            Assert.That(
                journal.ReadAfter("world", 0).Status,
                Is.EqualTo(
                    RuntimeCommandJournalReadStatus
                        .RecoveryUnavailable));
            Assert.That(batches, Has.Count.EqualTo(3));
            Assert.That(
                batches[1].Entries.Single().Sequence,
                Is.EqualTo(1));
            Assert.That(
                batches[2].Entries.Single().Sequence,
                Is.EqualTo(2));
            Assert.That(
                batches[2].ScannedThroughCursor,
                Is.EqualTo(2));
            Assert.That(rejects, Is.Empty);

            var oldBoundary = providedBoundary.Value;
            journal.CommitCheckpoint(
                "world",
                journal.Cursor);
            providedBoundary = new RuntimeCheckpointBoundary(
                "world",
                1,
                journal.Cursor,
                NEXT_CHECKPOINT_HASH);
            var baselineCount = baselines.Count;
            var recovery = coordinator.ReceiveJournalResync(
                connectionId,
                new RtCommandJournalResyncData(
                    session.SessionId,
                    oldBoundary.GroupId,
                    oldBoundary.CheckpointHash,
                    journal.Cursor,
                    forceCheckpointBaseline: true));

            Assert.That(recovery.Accepted, Is.True);
            var replacementChunks = baselines
                .Skip(baselineCount)
                .ToArray();
            Assert.That(
                replacementChunks
                    .Select(value => value.Store)
                    .Distinct()
                    .Count(),
                Is.EqualTo(2));
            Assert.That(
                replacementChunks.All(
                    value =>
                        value.CheckpointHash
                        == NEXT_CHECKPOINT_HASH),
                Is.True);
            Assert.That(
                batches[batches.Count - 1]
                    .CompletesCatchup,
                Is.True);
            Assert.That(
                batches[batches.Count - 1]
                    .CheckpointHash,
                Is.EqualTo(NEXT_CHECKPOINT_HASH));
            Assert.That(rejects, Is.Empty);

            journal.AppendInput(
                2,
                Encode(3),
                new RuntimeCommandJournalScope("map"));
            var secondBoundary =
                providedBoundary.Value;
            journal.CommitCheckpoint(
                "world",
                journal.Cursor);
            providedBoundary = new RuntimeCheckpointBoundary(
                "world",
                2,
                journal.Cursor,
                THIRD_CHECKPOINT_HASH);
            baselineCount = baselines.Count;

            var staleGapRecovery =
                coordinator.ReceiveJournalResync(
                    connectionId,
                    new RtCommandJournalResyncData(
                        session.SessionId,
                        secondBoundary.GroupId,
                        secondBoundary.CheckpointHash,
                        secondBoundary.JournalCursor,
                        forceCheckpointBaseline: false));

            Assert.That(staleGapRecovery.Accepted, Is.True);
            replacementChunks = baselines
                .Skip(baselineCount)
                .ToArray();
            Assert.That(
                replacementChunks
                    .Select(value => value.Store)
                    .Distinct()
                    .Count(),
                Is.EqualTo(2));
            Assert.That(
                replacementChunks.All(
                    value =>
                        value.CheckpointHash
                        == THIRD_CHECKPOINT_HASH),
                Is.True);
            Assert.That(
                batches[batches.Count - 1]
                    .CompletesCatchup,
                Is.True);
            Assert.That(
                batches[batches.Count - 1]
                    .CheckpointHash,
                Is.EqualTo(THIRD_CHECKPOINT_HASH));
            Assert.That(rejects, Is.Empty);
        }

        [Test]
        public void StoreSetJournalScope_MayBeManifestSubsetAndOnlyScopedBaselinesCarryBoundary()
        {
            var stores = new[]
            {
                RegisterServerStore("map"),
                RegisterServerStore("units"),
                RegisterServerStore("profile"),
            };
            var fixture = CreateProtocolFixture(stores);
            var journal = new RuntimeCommandJournal(
                () => 0d);
            var scope = new RuntimeCommandJournalScope(
                "map",
                "units");
            journal.ConfigureWindow(
                "world",
                scope,
                new RuntimeCommandJournalRetentionPolicy(
                    maxEntries: 10,
                    maxPayloadBytes: 1_000,
                    maxAgeSeconds: 100d));
            var boundary = new RuntimeCheckpointBoundary(
                "world",
                0,
                0,
                CHECKPOINT_HASH);
            var manifests =
                new List<RuntimeSessionManifestSnapshot>();
            var chunks = new List<RuntimeBaselineChunk>();
            using var coordinator =
                new RuntimeProtocolV2ServerCoordinator(
                    fixture.CreateServerContext(
                        CreateBus(journal),
                        scope,
                        () => boundary),
                    new RuntimeProtocolV2ServerOutput(
                        (_, manifest) =>
                            manifests.Add(manifest),
                        (_, _, _) => { },
                        (_, chunk) => chunks.Add(chunk),
                        (_, _) => { },
                        (_, _) => { },
                        (in RuntimeReliableDeltaTransportEnvelope _) =>
                            true,
                        journalBatch: (_, _) => { }),
                    firstSessionId: 91);
            coordinator.AddConnection(1);

            Assert.That(
                coordinator.ReceiveHello(
                    1,
                    fixture.Manifest.Descriptor,
                    clientNonce: 1).Accepted,
                Is.True);
            Assert.That(
                coordinator.ReceiveReady(
                    1,
                    manifests.Single().SessionId).Accepted,
                Is.True);

            Assert.That(
                chunks.Where(
                        value =>
                            value.Store.StoreId.Equals(
                                new FixedString32Bytes("map"))
                            || value.Store.StoreId.Equals(
                                new FixedString32Bytes("units")))
                    .All(
                        value =>
                            value.CheckpointHash
                            == CHECKPOINT_HASH),
                Is.True);
            Assert.That(
                chunks.Where(
                        value =>
                            value.Store.StoreId.Equals(
                                new FixedString32Bytes("profile")))
                    .All(
                        value =>
                            string.IsNullOrEmpty(
                                value.CheckpointHash)),
                Is.True);
        }

        [Test]
        public void ClientCoordinator_UnscopedManifestStorePublishesWithCheckpointGroupButNeedsNoBoundary()
        {
            var stores = new[]
            {
                new NetStoreRef(
                    new FixedString32Bytes("map"),
                    1),
                new NetStoreRef(
                    new FixedString32Bytes("profile"),
                    1),
            };
            var fixture = CreateProtocolFixture(stores);
            using var coordinator =
                new RuntimeProtocolV2ClientCoordinator(
                    fixture.CreateClientContext(
                        CreateBus(),
                        new RuntimeCommandJournalScope("map"),
                        _ => { }),
                    new RuntimeProtocolV2ClientOutput(
                        (_, _) => { },
                        _ => { },
                        (_, _) => { },
                        _ => { },
                        _ => { },
                        _ => { },
                        _ => { }),
                    clientNonce: 92);
            var session = BeginClientSession(
                coordinator,
                fixture,
                sessionId: 93);
            var boundary = new RuntimeCheckpointBoundary(
                "world",
                0,
                0,
                CHECKPOINT_HASH);

            SendBaseline(
                coordinator,
                fixture,
                session.SessionId,
                stores[0],
                baselineId: 1,
                deliverySequence: 1,
                boundary);
            var publication = SendBaseline(
                coordinator,
                fixture,
                session.SessionId,
                stores[1],
                baselineId: 1,
                deliverySequence: 1,
                boundary: null);

            Assert.That(
                publication.Kind,
                Is.EqualTo(
                    RuntimeClientReceiveResultKind.Buffered));
            Assert.That(
                RuntimeStores.TryGetRuntimeStore(
                    stores[0].StoreId,
                    stores[0].StoreGeneration,
                    StoreRealm.Client,
                    out _),
                Is.True);
            Assert.That(
                RuntimeStores.TryGetRuntimeStore(
                    stores[1].StoreId,
                    stores[1].StoreGeneration,
                    StoreRealm.Client,
                    out _),
                Is.True);
            Assert.That(coordinator.IsReplicaReady, Is.False);

            var completion =
                coordinator.ReceiveJournalBatch(
                    new RuntimeCommandJournalBatch(
                        session.SessionId,
                        boundary.GroupId,
                        boundary.CheckpointHash,
                        boundary.JournalCursor,
                        boundary.JournalCursor,
                        Array.Empty<
                            RuntimeCommandJournalEntry>(),
                        completesCatchup: true));

            Assert.That(completion.Succeeded, Is.True);
            Assert.That(coordinator.IsReplicaReady, Is.True);
        }

        [Test]
        public void ReliableProjection_IgnoresObjectInterestForScopedStoreButKeepsItForOrdinaryStore()
        {
            var stores = new[]
            {
                new NetStoreRef(
                    new FixedString32Bytes("map"),
                    1),
                new NetStoreRef(
                    new FixedString32Bytes("profile"),
                    1),
            };
            var fixture = CreateProtocolFixture(stores);
            var context = fixture.CreateServerContext(
                CreateBus(),
                new RuntimeCommandJournalScope("map"),
                () => new RuntimeCheckpointBoundary(
                    "world",
                    0,
                    0,
                    CHECKPOINT_HASH),
                (_, _, _) => false);
            var scopedStore = new RuntimeStore(
                stores[0].StoreId,
                StoreRealm.Server,
                _world);
            RuntimeStores.SetRuntimeStore(scopedStore);
            scopedStore.Create();
            scopedStore.Create();
            var ordinaryStore = new RuntimeStore(
                stores[1].StoreId,
                StoreRealm.Server,
                _world);
            RuntimeStores.SetRuntimeStore(ordinaryStore);
            ordinaryStore.Create();
            ordinaryStore.Create();

            var scopedProjection =
                RuntimeProjectedStoreSnapshotBuilder.Build(
                    scopedStore,
                    connectionId: 1,
                    context
                        .IsObjectVisibleForReliableProjection);
            var ordinaryProjection =
                RuntimeProjectedStoreSnapshotBuilder.Build(
                    ordinaryStore,
                    connectionId: 1,
                    context
                        .IsObjectVisibleForReliableProjection);

            Assert.That(scopedProjection.Count, Is.EqualTo(2));
            Assert.That(ordinaryProjection.Count, Is.Zero);
        }

        [Test]
        public void ClientJournalContext_RequiresExplicitPlaybackCompletionHook()
        {
            var store = new NetStoreRef(
                new FixedString32Bytes("map"),
                1);
            var fixture = CreateProtocolFixture(store);

            Assert.Throws<InvalidOperationException>(
                () => fixture.CreateClientContext(
                    CreateBus(),
                    new RuntimeCommandJournalScope("map"),
                    completeJournalCatchup: null));
        }

        [Test]
        public void ServerJournalContext_RequiresCheckpointBoundaryProvider()
        {
            var store = RegisterServerStore("map");
            var fixture = CreateProtocolFixture(store);

            Assert.Throws<InvalidOperationException>(
                () => fixture.CreateServerContext(
                    CreateBus(),
                    new RuntimeCommandJournalScope("map"),
                    checkpointBoundaryProvider: null));
        }

        [Test]
        public void SessionWideJournalScope_RejectsManifestThatOmitsActiveAuthoritativeStore()
        {
            var map = RegisterServerStore("map");
            RegisterServerStore("units");
            var fixture = CreateProtocolFixture(map);
            var journal = new RuntimeCommandJournal(
                () => 0d);
            journal.ConfigureWindow(
                "world",
                RuntimeCommandJournalScope.Session,
                new RuntimeCommandJournalRetentionPolicy(
                    maxEntries: 10,
                    maxPayloadBytes: 1_000,
                    maxAgeSeconds: 100d));

            Assert.Throws<InvalidOperationException>(
                () => fixture.CreateServerContext(
                    CreateBus(journal),
                    RuntimeCommandJournalScope.Session,
                    () => new RuntimeCheckpointBoundary(
                        "world",
                        0,
                        0,
                        CHECKPOINT_HASH)));
        }

        private NetStoreRef RegisterServerStore(
            string storeId)
        {
            var store = new RuntimeStore(
                new FixedString32Bytes(storeId),
                StoreRealm.Server,
                _world);
            RuntimeStores.SetRuntimeStore(
                store,
                StoreNetDir.S2C);
            return new NetStoreRef(
                store.Id,
                store.StoreGeneration);
        }

        private void EnsureCoroutineParent()
        {
            if (CoroutineParent.GetNoCheck() != null)
            {
                return;
            }

            _ownedCoroutineParent = new GameObject(
                $"{nameof(RuntimeCommandJournalNetworkTests)} CoroutineParent");
            _ownedCoroutineParent
                .AddComponent<CoroutineParent>();
        }

        private static RuntimeSessionManifestSnapshot
            BeginClientSession(
                RuntimeProtocolV2ClientCoordinator coordinator,
                ProtocolFixture fixture,
                ulong sessionId)
        {
            coordinator.BeginHandshake();
            var session =
                fixture.Manifest.CreateSnapshot(sessionId);
            var result = coordinator.ReceiveManifest(
                session.SessionId,
                session.Descriptor,
                session.CopyAssets(),
                session.CopyStores());
            Assert.That(result.Accepted, Is.True);
            return session;
        }

        private static RuntimeClientReceiveResult
            SendBaseline(
                RuntimeProtocolV2ClientCoordinator coordinator,
                ProtocolFixture fixture,
                ulong sessionId,
                in NetStoreRef store,
                ulong baselineId,
                ulong deliverySequence,
                RuntimeCheckpointBoundary? boundary)
        {
            var payload =
                new RuntimeStoreBaselineCodec(
                        fixture.PatchCodecs)
                    .Encode(
                        new RuntimeStoreBaselinePayload
                        {
                            Store = store,
                            BaselineId = baselineId,
                            StoreRevision = 0,
                        });
            var chunks = RuntimeBaselineChunker.Split(
                sessionId,
                store,
                baselineId,
                deliverySequence,
                0,
                payload,
                boundary);
            RuntimeClientReceiveResult result = default;
            for (var i = 0; i < chunks.Count; i++)
            {
                result = coordinator.ReceiveBaselineChunk(
                    chunks[i],
                    i * 0.01d);
            }

            return result;
        }

        private ProtocolFixture
            CreateProtocolFixture(
                params NetStoreRef[] stores)
        {
            return new ProtocolFixture(_world, stores);
        }

        private static void AssertReceiveFailure(
            in RuntimeCheckpointBoundary boundary,
            RuntimeCommandJournalBatch batch,
            RuntimeCommandJournalReceiveStatus expected)
        {
            var receiver = new RuntimeCommandJournalCatchupReceiver(
                1,
                boundary,
                new RuntimeCommandJournalScope("map"),
                CreateBus());

            var result = receiver.Receive(batch);

            Assert.That(result.Status, Is.EqualTo(expected));
            Assert.That(receiver.NeedsResync, Is.True);
        }

        private static RuntimeCommandJournalEntry Entry(
            long tick,
            ulong sequence,
            string storeId,
            int value)
        {
            return Entry(tick, sequence, new[] { storeId }, value);
        }

        private static RuntimeCommandJournalEntry Entry(
            long tick,
            ulong sequence,
            string[] storeIds,
            int value)
        {
            return new RuntimeCommandJournalEntry(
                tick,
                sequence,
                Encode(value),
                new RuntimeCommandJournalScope(storeIds));
        }

        private static RuntimeEncodedCommand Encode(int value)
        {
            return new RuntimeEncodedCommand(
                COMMAND_TYPE_ID,
                1,
                BitConverter.GetBytes(value));
        }

        private static RuntimeCommandsBus CreateBus(
            RuntimeCommandJournal journal = null)
        {
            var registry = new RuntimeReplayCommandRegistry();
            registry.Register<RuntimeCommandJournalNetworkTestCommand>(
                COMMAND_TYPE_ID,
                "tests.network-journal.command",
                1,
                (command, _) =>
                    BitConverter.GetBytes(
                        command.Get<RuntimeCommandJournalNetworkTestCommand>().Value),
                (payload, _) =>
                {
                    var command = new GameRuntimeCommand();
                    command.AddOrReplace(
                        new RuntimeCommandJournalNetworkTestCommand
                        {
                            Value = BitConverter.ToInt32(payload, 0),
                        });
                    return command;
                });
            registry.Seal();
            return new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                registry,
                journal: journal);
        }

        private static RuntimeSessionDescriptor CreateDescriptor(
            ushort version)
        {
            return new RuntimeSessionDescriptor
            {
                ProtocolVersion = version,
                BuildId = "build",
                RuntimeSchemaHash = "schema",
                AssetCatalogHash = "assets",
                ResourceCatalogHash = "resources",
                StateStreamCatalogHash = "streams",
            };
        }

        private class ProtocolFixture
        {
            private readonly World _world;

            public readonly GameAssetLibraryLock AssetLock;
            public readonly RuntimeSessionAssetCatalog AssetCatalog;
            public readonly RuntimePatchCodecRegistry PatchCodecs;
            public readonly GameAssetTemplateCache Templates;
            public readonly RuntimeReplicationPolicyRegistry Policies;
            public readonly RuntimeStateStreamProfileRegistry Streams;
            public readonly RuntimeSessionManifestTemplate Manifest;

            public ProtocolFixture(
                World world,
                IReadOnlyList<NetStoreRef> stores)
            {
                _world = world != null && world.IsCreated
                    ? world
                    : throw new ArgumentException(
                        "A protocol fixture requires a created World.",
                        nameof(world));
                if (stores == null || stores.Count == 0)
                {
                    throw new ArgumentException(
                        "A protocol fixture requires at least one store.",
                        nameof(stores));
                }

                AssetLock =
                    CreateDummyAssetLock().Seal();
                AssetCatalog =
                    RuntimeSessionAssetCatalog.FromLock(
                        AssetLock);
                PatchCodecs =
                    new RuntimePatchCodecRegistry(
                        "journal-coordinator-tests");
                Templates = new GameAssetTemplateCache(
                    PatchCodecs,
                    RuntimeTemplatePatchCodecContext.Instance);
                Policies =
                    new RuntimeReplicationPolicyRegistry();
                Policies.Seal(Array.Empty<uint>());
                Streams =
                    RuntimeStateStreamProfileRegistry
                        .CreateEmptySealed();
                var storeCatalog =
                    new RuntimeStoreCatalogEntry[
                        stores.Count];
                for (var i = 0; i < stores.Count; i++)
                {
                    storeCatalog[i] =
                        new RuntimeStoreCatalogEntry
                        {
                            StoreId = stores[i].StoreId,
                            StoreGeneration =
                                stores[i].StoreGeneration,
                        };
                }

                var descriptor =
                    new RuntimeSessionDescriptor
                    {
                        ProtocolVersion =
                            RuntimeProtocolV2.VERSION,
                        BuildId =
                            "journal-coordinator-tests",
                        RuntimeSchemaHash =
                            PatchCodecs.SchemaHash,
                        AssetCatalogHash =
                            RuntimeSessionCatalogHasher
                                .CalculateAssets(
                                    AssetCatalog
                                        .ManifestEntries),
                        ResourceCatalogHash = "resources",
                        StateStreamCatalogHash =
                            Streams.CatalogHash,
                    };
                Manifest =
                    new RuntimeSessionManifestTemplate(
                        descriptor,
                        AssetCatalog.ManifestEntries,
                        storeCatalog);
            }

            public RuntimeProtocolV2Context
                CreateClientContext(
                    RuntimeCommandsBus bus,
                    RuntimeCommandJournalScope scope,
                    RuntimeJournalCatchupCompletion
                        completeJournalCatchup)
            {
                return new RuntimeProtocolV2Context(
                    RuntimeSessionClientExpectation
                        .FromServerTemplate(Manifest),
                    AssetCatalog,
                    AssetLock,
                    Templates,
                    PatchCodecs,
                    Policies,
                    _world,
                    Streams,
                    commandsBus: bus,
                    journalSubscriptionScope: scope,
                    completeJournalCatchup:
                        completeJournalCatchup);
            }

            public RuntimeProtocolV2Context
                CreateServerContext(
                    RuntimeCommandsBus bus,
                    RuntimeCommandJournalScope scope,
                    RuntimeCheckpointBoundaryProvider
                        checkpointBoundaryProvider,
                    RuntimeObjectVisibility
                        isObjectVisible = null)
            {
                return new RuntimeProtocolV2Context(
                    Manifest,
                    AssetCatalog,
                    AssetLock,
                    Templates,
                    PatchCodecs,
                    Policies,
                    _world,
                    Streams,
                    commandsBus: bus,
                    isObjectVisible: isObjectVisible,
                    checkpointBoundaryProvider:
                        checkpointBoundaryProvider,
                    journalSubscriptionScope: scope);
            }

            private static GameAssetLibraryLock
                CreateDummyAssetLock()
            {
                var result =
                    new GameAssetLibraryLock();
                var key = new GameAssetKey(
                    "tests",
                    "network",
                    "dummy",
                    "1.0.0");
                result.Set(
                    key,
                    new GameAssetLibraryLockEntry(
                        key,
                        UnityEngine.Hash128.Parse(
                            "11111111111111111111111111111111"),
                        "dummy-content"));
                return result;
            }
        }
    }
}
