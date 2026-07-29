using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public interface IRuntimeDotsCheckpointExporter
    {
        uint ExporterId { get; }

        void Export(in RuntimeDotsCheckpointContext context);
    }

    public class RuntimeDotsCheckpointGroup
    {
        private readonly IRuntimeDotsCheckpointExporter[] _exporters;

        public string GroupId { get; }
        public RuntimeReplayStoreScope StoreScope { get; }
        public RuntimeCommandJournalScope JournalScope { get; }
        public RuntimeCommandJournalRetentionPolicy RetentionPolicy { get; }
        public int ExporterCount => _exporters.Length;

        public RuntimeDotsCheckpointGroup(
            string groupId,
            RuntimeReplayStoreScope storeScope,
            RuntimeCommandJournalScope journalScope,
            in RuntimeCommandJournalRetentionPolicy retentionPolicy,
            IEnumerable<IRuntimeDotsCheckpointExporter> exporters = null)
        {
            RuntimeReplayId.Validate(groupId, nameof(groupId));
            if (journalScope == null)
            {
                throw new ArgumentNullException(nameof(journalScope));
            }
            retentionPolicy.Validate();

            ValidateJournalCoverage(storeScope, journalScope);

            GroupId = groupId;
            StoreScope = storeScope;
            JournalScope = journalScope;
            RetentionPolicy = retentionPolicy;
            _exporters = OrderAndValidateExporters(exporters);
        }

        public IRuntimeDotsCheckpointExporter TakeExporter(int index)
        {
            return _exporters[index];
        }

        private static void ValidateJournalCoverage(
            RuntimeReplayStoreScope storeScope,
            RuntimeCommandJournalScope journalScope)
        {
            if (journalScope.IsSessionWide)
            {
                if (storeScope != null)
                {
                    throw new ArgumentException(
                        "A session-wide journal scope uses a null RuntimeStore scope to snapshot every store in the realm.",
                        nameof(journalScope));
                }

                return;
            }
            if (storeScope == null)
            {
                throw new ArgumentException(
                    "A store-set journal scope requires a matching checkpoint RuntimeStore scope.",
                    nameof(journalScope));
            }
            if (storeScope.Count != journalScope.StoreCount)
            {
                throw new ArgumentException(
                    "A store-set journal scope must exactly match the checkpoint RuntimeStore scope.",
                    nameof(journalScope));
            }

            for (var i = 0; i < storeScope.Count; i++)
            {
                var storeId = storeScope.TakeStoreId(i);
                if (journalScope.Contains(storeId))
                {
                    continue;
                }

                throw new ArgumentException(
                    $"Journal scope does not cover checkpoint RuntimeStore '{storeId}'.",
                    nameof(journalScope));
            }
        }

        private static IRuntimeDotsCheckpointExporter[]
            OrderAndValidateExporters(
                IEnumerable<IRuntimeDotsCheckpointExporter> exporters)
        {
            if (exporters == null)
            {
                return Array.Empty<IRuntimeDotsCheckpointExporter>();
            }

            var ordered = new List<IRuntimeDotsCheckpointExporter>();
            foreach (var exporter in exporters)
            {
                if (exporter == null)
                {
                    throw new ArgumentException(
                        "A DOTS checkpoint exporter cannot be null.",
                        nameof(exporters));
                }

                ordered.Add(exporter);
            }

            ordered.Sort(
                (left, right) => left.ExporterId.CompareTo(right.ExporterId));
            for (var i = 1; i < ordered.Count; i++)
            {
                if (ordered[i - 1].ExporterId != ordered[i].ExporterId)
                {
                    continue;
                }

                throw new ArgumentException(
                    $"DOTS checkpoint exporter id '{ordered[i].ExporterId}' is registered twice.",
                    nameof(exporters));
            }

            return ordered.ToArray();
        }
    }

    public readonly struct RuntimeDotsCheckpointContext
    {
        public readonly RuntimeDotsCheckpointGroup Group;
        public readonly World World;
        public readonly StoreRealm Realm;
        public readonly long CompletedTick;
        public readonly ulong JournalCursor;
        private readonly RuntimeStoresReplayCheckpointStage _storeStage;

        public RuntimeDotsCheckpointContext(
            RuntimeDotsCheckpointGroup group,
            World world,
            StoreRealm realm,
            long completedTick,
            ulong journalCursor,
            RuntimeStoresReplayCheckpointStage storeStage = null)
        {
            Group = group
                    ?? throw new ArgumentNullException(nameof(group));
            World = world != null && world.IsCreated
                ? world
                : throw new ArgumentException(
                    "A DOTS checkpoint context requires a created ECS World.",
                    nameof(world));
            if (completedTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(completedTick));
            }

            Realm = realm;
            CompletedTick = completedTick;
            JournalCursor = journalCursor;
            _storeStage = storeStage;
        }

        public bool TryTakeStore(
            FixedString32Bytes storeId,
            out RuntimeStore store)
        {
            if (Group.StoreScope != null
                && !Group.StoreScope.Contains(storeId))
            {
                store = null;
                return false;
            }

            return _storeStage != null
                ? _storeStage.TryTakeStore(storeId, out store)
                : RuntimeStores.TryGetRuntimeStore(
                    storeId,
                    Realm,
                    out store);
        }

        public RuntimeStore TakeStore(FixedString32Bytes storeId)
        {
            if (Group.StoreScope != null
                && !Group.StoreScope.Contains(storeId))
            {
                throw new InvalidOperationException(
                    $"RuntimeStore '{storeId}' is outside checkpoint group '{Group.GroupId}'.");
            }
            RuntimeStore store;
            var hasStore = _storeStage != null
                ? _storeStage.TryTakeStore(storeId, out store)
                : RuntimeStores.TryGetRuntimeStore(
                    storeId,
                    Realm,
                    out store);
            if (!hasStore)
            {
                throw new InvalidOperationException(
                    $"RuntimeStore '{storeId}' is not active in realm {Realm}.");
            }

            return store;
        }
    }

    public readonly struct RuntimeCheckpointBoundary
    {
        public readonly string GroupId;
        public readonly long CompletedTick;
        public readonly ulong JournalCursor;
        public readonly string CheckpointHash;

        public RuntimeCheckpointBoundary(
            string groupId,
            long completedTick,
            ulong journalCursor,
            string checkpointHash)
        {
            RuntimeReplayId.Validate(groupId, nameof(groupId));
            if (completedTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(completedTick));
            }
            if (!RuntimeReplayHash.IsSha256Hex(checkpointHash))
            {
                throw new ArgumentException(
                    "Checkpoint identity must be a SHA-256 hex string.",
                    nameof(checkpointHash));
            }

            GroupId = groupId;
            CompletedTick = completedTick;
            JournalCursor = journalCursor;
            CheckpointHash = checkpointHash.ToLowerInvariant();
        }
    }

    public class RuntimeDotsCheckpointCoordinator
    {
        private readonly struct RuntimeCheckpointStoreVersion
        {
            public readonly RuntimeStore Store;
            public readonly uint Epoch;
            public readonly uint StoreGeneration;
            public readonly ulong StoreRevision;

            public RuntimeCheckpointStoreVersion(RuntimeStore store)
            {
                Store = store;
                Epoch = store.Epoch;
                StoreGeneration = store.StoreGeneration;
                StoreRevision = store.StoreRevision;
            }
        }

        private readonly RuntimeDotsCheckpointGroup _group;
        private readonly RuntimeCommandsBus _commandsBus;
        private readonly RuntimeCommandJournal _journal;
        private readonly RuntimeReplayCheckpointRegistry _checkpointRegistry;
        private readonly RuntimeStoresReplayCheckpointParticipant
            _storeCheckpointParticipant;
        private readonly World _world;
        private readonly StoreRealm _realm;

        private Dictionary<FixedString32Bytes, RuntimeCheckpointStoreVersion>
            _checkpointStoreVersions = new();
        private bool _captureInProgress;

        public RuntimeDotsCheckpointGroup Group => _group;
        public RuntimeReplayCheckpointEnvelope CurrentCheckpoint { get; private set; }
        public RuntimeCheckpointBoundary CurrentBoundary { get; private set; }
        public bool HasCheckpoint => CurrentCheckpoint != null;

        public RuntimeDotsCheckpointCoordinator(
            RuntimeDotsCheckpointGroup group,
            RuntimeCommandsBus commandsBus,
            RuntimeCommandJournal journal,
            RuntimeReplayCheckpointRegistry checkpointRegistry,
            World world,
            StoreRealm realm)
        {
            _group = group
                     ?? throw new ArgumentNullException(nameof(group));
            _commandsBus = commandsBus
                           ?? throw new ArgumentNullException(
                               nameof(commandsBus));
            _journal = journal
                       ?? throw new ArgumentNullException(nameof(journal));
            if (!ReferenceEquals(_commandsBus.Journal, _journal))
            {
                throw new ArgumentException(
                    "A DOTS checkpoint coordinator must use the command bus journal.",
                    nameof(journal));
            }
            _checkpointRegistry = checkpointRegistry
                                  ?? throw new ArgumentNullException(
                                      nameof(checkpointRegistry));
            if (!checkpointRegistry.IsSealed)
            {
                throw new InvalidOperationException(
                    "A DOTS checkpoint coordinator requires a sealed checkpoint registry.");
            }
            checkpointRegistry.TryTakeParticipant<
                RuntimeStoresReplayCheckpointParticipant>(
                out var storeCheckpointParticipant);
            _storeCheckpointParticipant = storeCheckpointParticipant;
            _world = world != null && world.IsCreated
                ? world
                : throw new ArgumentException(
                    "A DOTS checkpoint coordinator requires a created ECS World.",
                    nameof(world));
            _realm = realm;

            _journal.ConfigureWindow(
                _group.GroupId,
                _group.JournalScope,
                _group.RetentionPolicy);
        }

        public RuntimeReplayCheckpointEnvelope CaptureAtCompletedTick(
            long completedTick)
        {
            ValidateBarrier(completedTick);
            if (_captureInProgress)
            {
                throw new InvalidOperationException(
                    "DOTS checkpoint capture cannot be re-entered.");
            }

            _captureInProgress = true;
            RuntimeStoresReplayCheckpointStage storeStage = null;
            try
            {
                var journalCursor = _journal.Cursor;
                _world.EntityManager.CompleteAllTrackedJobs();
                storeStage = PrepareStoreStage();

                var context = new RuntimeDotsCheckpointContext(
                    _group,
                    _world,
                    _realm,
                    completedTick,
                    journalCursor,
                    storeStage);
                for (var i = 0; i < _group.ExporterCount; i++)
                {
                    _group.TakeExporter(i).Export(in context);
                }

                if (storeStage != null)
                {
                    storeStage.FlushToQuiescence();
                }
                else
                {
                    FlushScopedStoresToQuiescence();
                }

                RuntimeReplayCheckpointEnvelope checkpoint;
                if (storeStage != null)
                {
                    _storeCheckpointParticipant.BeginCaptureFromStage(
                        storeStage);
                    try
                    {
                        checkpoint = _checkpointRegistry.Capture(
                            completedTick,
                            journalCursor);
                    }
                    finally
                    {
                        _storeCheckpointParticipant.EndCaptureFromStage(
                            storeStage);
                    }
                }
                else
                {
                    checkpoint = _checkpointRegistry.Capture(
                        completedTick,
                        journalCursor);
                }
                if (_journal.Cursor != journalCursor)
                {
                    throw new InvalidOperationException(
                        "The command journal changed during DOTS checkpoint capture.");
                }
                var boundary = new RuntimeCheckpointBoundary(
                    _group.GroupId,
                    completedTick,
                    journalCursor,
                    RuntimeReplayHash.ToHex(checkpoint.OverallHash));
                var storeRevisions =
                    CaptureScopedStoreRevisions(storeStage);
                ValidateJournalCommit(journalCursor);

                storeStage?.Publish();
                _journal.CommitCheckpoint(
                    _group.GroupId,
                    journalCursor);

                CurrentCheckpoint = checkpoint;
                CurrentBoundary = boundary;
                _checkpointStoreVersions = storeRevisions;
                return checkpoint;
            }
            finally
            {
                storeStage?.Dispose();
                _captureInProgress = false;
            }
        }

        public bool TryTakeRecoveryBoundary(
            out RuntimeCheckpointBoundary boundary)
        {
            boundary = default;
            if (!HasCheckpoint
                || !_journal.TryGetWindow(
                    _group.GroupId,
                    out var window)
                || !window.RecoveryAvailable
                || window.CheckpointCursor
                != CurrentBoundary.JournalCursor)
            {
                return false;
            }

            if (_group.StoreScope == null)
            {
                var activeStoreCount = 0;
                foreach (var store in RuntimeStores.EnumerateStores(_realm))
                {
                    activeStoreCount++;
                    if (!_checkpointStoreVersions.ContainsKey(store.Id))
                    {
                        return false;
                    }
                }
                if (activeStoreCount != _checkpointStoreVersions.Count)
                {
                    return false;
                }
            }

            foreach (var pair in _checkpointStoreVersions)
            {
                if (!RuntimeStores.TryGetRuntimeStore(
                        pair.Key,
                        _realm,
                        out var store)
                    || !ReferenceEquals(store, pair.Value.Store)
                    || store.Epoch != pair.Value.Epoch
                    || store.StoreGeneration
                    != pair.Value.StoreGeneration)
                {
                    return false;
                }
                try
                {
                    store.FlushToQuiescence();
                }
                catch
                {
                    return false;
                }
                if (store.StoreRevision
                    != pair.Value.StoreRevision)
                {
                    return false;
                }
            }

            boundary = CurrentBoundary;
            return true;
        }

        public RuntimeCheckpointBoundary? ProvideRecoveryBoundary()
        {
            return TryTakeRecoveryBoundary(out var boundary)
                ? boundary
                : null;
        }

        private RuntimeStoresReplayCheckpointStage PrepareStoreStage()
        {
            if (_group.StoreScope == null)
            {
                var hasActiveStore = false;
                foreach (var unused in RuntimeStores.EnumerateStores(_realm))
                {
                    hasActiveStore = true;
                    break;
                }
                if (!hasActiveStore)
                {
                    return null;
                }
            }

            if (_storeCheckpointParticipant == null)
            {
                throw new InvalidOperationException(
                    "A DOTS checkpoint that exports RuntimeStore state requires RuntimeStoresReplayCheckpointParticipant in its checkpoint registry.");
            }
            if (_storeCheckpointParticipant.Realm != _realm)
            {
                throw new InvalidOperationException(
                    $"DOTS checkpoint realm {_realm} does not match its RuntimeStore replay participant realm {_storeCheckpointParticipant.Realm}.");
            }

            return _storeCheckpointParticipant.PrepareCheckpointStage(
                _group.StoreScope);
        }

        private void ValidateJournalCommit(ulong cursor)
        {
            if (!_journal.TryGetWindow(
                    _group.GroupId,
                    out var window))
            {
                throw new InvalidOperationException(
                    $"Journal checkpoint group '{_group.GroupId}' is not configured.");
            }
            if (cursor > _journal.Cursor)
            {
                throw new InvalidOperationException(
                    "Checkpoint cursor cannot exceed the journal cursor.");
            }
            if (!window.RecoveryAvailable && cursor != _journal.Cursor)
            {
                throw new InvalidOperationException(
                    $"Journal checkpoint group '{_group.GroupId}' lost its recovery window and must checkpoint the current cursor {_journal.Cursor}.");
            }
            if (window.RecoveryAvailable
                && cursor < window.CheckpointCursor)
            {
                throw new InvalidOperationException(
                    $"Journal checkpoint cursor {cursor} precedes group '{_group.GroupId}' boundary {window.CheckpointCursor}.");
            }
        }

        private void ValidateBarrier(long completedTick)
        {
            if (completedTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(completedTick));
            }
            if (_commandsBus.Mode
                != RuntimeCommandsBusMode.ExternalTickBarrier)
            {
                throw new InvalidOperationException(
                    "DOTS checkpoints require RuntimeCommandsBus ExternalTickBarrier mode.");
            }
            if (!_commandsBus.HasDrainedTick
                || _commandsBus.LastApplyBeforeTick != completedTick)
            {
                throw new InvalidOperationException(
                    $"DOTS checkpoint tick {completedTick} is not the completed command barrier "
                    + $"{_commandsBus.LastApplyBeforeTick}.");
            }
            if (_journal.LastApplyBeforeTick > completedTick)
            {
                throw new InvalidOperationException(
                    $"Journal tick {_journal.LastApplyBeforeTick} is ahead of completed checkpoint tick "
                    + $"{completedTick}.");
            }
        }

        private void FlushScopedStoresToQuiescence()
        {
            var storeScope = _group.StoreScope;
            if (storeScope == null)
            {
                RuntimeStores.FlushToQuiescence(_realm);
                return;
            }

            for (var i = 0; i < storeScope.Count; i++)
            {
                var storeId = storeScope.TakeStoreId(i);
                if (!RuntimeStores.TryGetRuntimeStore(
                        storeId,
                        _realm,
                        out var store))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint RuntimeStore '{storeId}' is not active in realm {_realm}.");
                }

                store.FlushToQuiescence();
            }
        }

        private Dictionary<FixedString32Bytes, RuntimeCheckpointStoreVersion>
            CaptureScopedStoreRevisions(
                RuntimeStoresReplayCheckpointStage storeStage)
        {
            var revisions =
                new Dictionary<
                    FixedString32Bytes,
                    RuntimeCheckpointStoreVersion>();
            var storeScope = _group.StoreScope;
            if (storeStage != null)
            {
                if (storeScope == null)
                {
                    for (var i = 0; i < storeStage.Stores.Count; i++)
                    {
                        var store = storeStage.Stores[i];
                        revisions.Add(
                            store.Id,
                            new RuntimeCheckpointStoreVersion(store));
                    }

                    return revisions;
                }

                for (var i = 0; i < storeScope.Count; i++)
                {
                    var storeId = storeScope.TakeStoreId(i);
                    var store = storeStage.TakeStore(storeId);
                    revisions.Add(
                        storeId,
                        new RuntimeCheckpointStoreVersion(store));
                }

                return revisions;
            }
            if (storeScope == null)
            {
                var stores = new List<RuntimeStore>(
                    RuntimeStores.EnumerateStores(_realm));
                stores.Sort(
                    (left, right) => left.Id.CompareTo(right.Id));
                for (var i = 0; i < stores.Count; i++)
                {
                    revisions.Add(
                        stores[i].Id,
                        new RuntimeCheckpointStoreVersion(stores[i]));
                }

                return revisions;
            }

            for (var i = 0; i < storeScope.Count; i++)
            {
                var storeId = storeScope.TakeStoreId(i);
                if (!RuntimeStores.TryGetRuntimeStore(
                        storeId,
                        _realm,
                        out var store))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint RuntimeStore '{storeId}' is not active in realm {_realm}.");
                }

                revisions.Add(
                    storeId,
                    new RuntimeCheckpointStoreVersion(store));
            }

            return revisions;
        }
    }
}
