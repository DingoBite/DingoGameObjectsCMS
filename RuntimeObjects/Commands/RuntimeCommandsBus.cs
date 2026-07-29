using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoProjectAppStructure.Core.Model;
using DingoUnityExtensions;

namespace DingoGameObjectsCMS.RuntimeObjects.Commands
{
    public delegate void RuntimeCommandOutboundDispatcher(GameRuntimeCommand command, in RuntimeExecutionState authority);

    public class RuntimeCommandsBus : AppModelBase
    {
        public const int UPDATE_ORDER = RuntimeStore.UPDATE_ORDER + 1;

        private readonly List<GameRuntimeCommand> _queue = new(capacity: 64);
        private readonly List<GameRuntimeCommand> _processing = new(capacity: 64);

        private bool _scheduled;
        private bool _flushInProgress;
        private bool _rescheduleRequested;
        private bool _hasDrainedTick;
        private long _lastApplyBeforeTick;
        private ulong _nextExecutionId;
        private RuntimeCommandsBusMode _mode;
        private RuntimeReplayCommandRegistry _replayRegistry;
        private RuntimePersistentPatchCodecContext _replayCodecContext;
        private RuntimeReplayStoreScope _replayStoreScope;
        private bool _clearReplayStoreScopeRequested;
        private readonly RuntimeCommandJournal _journal;

        private readonly SafeMulticast<GameRuntimeCommand> _beforeExecute = new();
        private readonly SafeMulticast<GameRuntimeCommand> _afterExecute = new();
        private readonly SafeMulticast<GameRuntimeCommand, Exception> _executeFailed = new();
        private readonly SafeMulticast<RuntimeCommandExecutionResult> _commandCompleted = new();
        private RuntimeCommandOutboundDispatcher _outboundDispatcher;

        public int QueuedCount => _queue.Count;
        public IReadOnlyList<GameRuntimeCommand> QueuedCommands => _queue;
        public bool HasOutboundDispatcher => _outboundDispatcher != null;
        public RuntimeCommandsBusMode Mode => _mode;
        public bool HasReplayRegistry => _replayRegistry != null;
        public bool HasReplayStoreScope => _replayStoreScope != null;
        public bool HasDrainedTick => _hasDrainedTick;
        public long LastApplyBeforeTick => _hasDrainedTick ? _lastApplyBeforeTick : -1;
        public ulong JournalSequence => _journal.Sequence;
        public RuntimeCommandJournal Journal => _journal;

        public RuntimeCommandsBus(
            RuntimeCommandOutboundDispatcher outboundDispatcher = null,
            RuntimeCommandsBusMode mode = RuntimeCommandsBusMode.AutomaticLateUpdate,
            RuntimeReplayCommandRegistry replayRegistry = null,
            RuntimePersistentPatchCodecContext replayCodecContext = null,
            RuntimeCommandJournal journal = null)
        {
            ValidateMode(mode);
            _outboundDispatcher = outboundDispatcher;
            _mode = mode;
            _journal = journal ?? new RuntimeCommandJournal();
            if (replayRegistry != null)
                SetReplayRegistry(replayRegistry, replayCodecContext);
        }

        public RuntimeCommandsBus(
            RuntimeCommandsBusMode mode,
            RuntimeReplayCommandRegistry replayRegistry = null,
            RuntimePersistentPatchCodecContext replayCodecContext = null,
            RuntimeCommandJournal journal = null)
            : this(null, mode, replayRegistry, replayCodecContext, journal)
        {
        }

        public event Action<GameRuntimeCommand> BeforeExecute
        {
            add => _beforeExecute.Subscribe(value);
            remove => _beforeExecute.Unsubscribe(value);
        }

        public event Action<GameRuntimeCommand> AfterExecute
        {
            add => _afterExecute.Subscribe(value);
            remove => _afterExecute.Unsubscribe(value);
        }

        public event Action<GameRuntimeCommand, Exception> ExecuteFailed
        {
            add => _executeFailed.Subscribe(value);
            remove => _executeFailed.Unsubscribe(value);
        }

        public event Action<RuntimeCommandJournalEntry> JournalEntryRecorded
        {
            add => _journal.EntryRecorded += value;
            remove => _journal.EntryRecorded -= value;
        }

        public event Action<RuntimeCommandExecutionResult> CommandCompleted
        {
            add => _commandCompleted.Subscribe(value);
            remove => _commandCompleted.Unsubscribe(value);
        }

        public void SetMode(RuntimeCommandsBusMode mode)
        {
            ValidateMode(mode);
            if (_mode == mode)
                return;
            if (_flushInProgress)
                throw new InvalidOperationException("Runtime command bus mode cannot change during Drain.");

            CancelScheduledFlush();
            _mode = mode;
            if (_mode == RuntimeCommandsBusMode.AutomaticLateUpdate && _queue.Count > 0)
                ScheduleFlush();
        }

        public void SetReplayRegistry(
            RuntimeReplayCommandRegistry replayRegistry,
            RuntimePersistentPatchCodecContext replayCodecContext = null)
        {
            if (replayRegistry == null)
                throw new ArgumentNullException(nameof(replayRegistry));
            if (!replayRegistry.IsSealed)
                throw new InvalidOperationException("Replay command registry must be sealed before it is assigned to the command bus.");
            if (_flushInProgress)
                throw new InvalidOperationException("Replay command registry cannot change during Drain.");

            _replayRegistry = replayRegistry;
            _replayCodecContext = replayCodecContext;
        }

        public void ClearReplayRegistry()
        {
            if (_flushInProgress)
                throw new InvalidOperationException("Replay command registry cannot change during Drain.");

            _replayRegistry = null;
            _replayCodecContext = null;
            _replayStoreScope = null;
            _clearReplayStoreScopeRequested = false;
        }

        public void SetReplayStoreScope(
            RuntimeReplayStoreScope storeScope)
        {
            if (storeScope == null)
            {
                throw new ArgumentNullException(nameof(storeScope));
            }
            if (_flushInProgress)
            {
                throw new InvalidOperationException(
                    "Replay RuntimeStore scope cannot change during Drain.");
            }
            if (_replayRegistry == null)
            {
                throw new InvalidOperationException(
                    "Replay RuntimeStore scope requires a replay command registry.");
            }
            if (_replayStoreScope != null)
            {
                throw new InvalidOperationException(
                    "Replay RuntimeStore scope is already assigned.");
            }

            _replayStoreScope = storeScope;
            _clearReplayStoreScopeRequested = false;
        }

        public void ClearReplayStoreScope(
            RuntimeReplayStoreScope expectedScope = null)
        {
            if (_replayStoreScope != null
                && expectedScope != null
                && !ReferenceEquals(
                    _replayStoreScope,
                    expectedScope))
            {
                throw new InvalidOperationException(
                    "Cannot clear a replay RuntimeStore scope owned by another recorder.");
            }

            if (_flushInProgress)
            {
                _clearReplayStoreScopeRequested = true;
                return;
            }

            _replayStoreScope = null;
            _clearReplayStoreScopeRequested = false;
        }

        public void SetOutboundDispatcher(RuntimeCommandOutboundDispatcher outboundDispatcher)
        {
            _outboundDispatcher = outboundDispatcher ?? throw new ArgumentNullException(nameof(outboundDispatcher));
        }

        public void ClearOutboundDispatcher()
        {
            _outboundDispatcher = null;
        }

        public void Dispatch(GameRuntimeCommand command, in RuntimeExecutionState authority)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            switch (authority.StableRole)
            {
                case RuntimeExecutionRole.OfflineAuthoritative:
                case RuntimeExecutionRole.ServerAuthoritative:
                case RuntimeExecutionRole.HostAuthoritative:
                    Enqueue(command);
                    return;
                case RuntimeExecutionRole.ClientReplica:
                    var outboundDispatcher = _outboundDispatcher;
                    if (outboundDispatcher == null)
                        throw new InvalidOperationException($"{nameof(RuntimeCommandsBus)} requires an outbound dispatcher for remote-client commands.");

                    outboundDispatcher(command, in authority);
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(authority), authority.StableRole, "Unknown runtime command authority role.");
            }
        }

        public void Enqueue(GameRuntimeCommand command)
        {
            if (command == null)
                return;

            _queue.Add(command);
            ScheduleFlush();
        }

        public void EnqueueRange(IEnumerable<GameRuntimeCommand> commands)
        {
            if (commands == null)
                return;

            foreach (var c in commands)
            {
                if (c != null)
                    _queue.Add(c);
            }

            ScheduleFlush();
        }

        public void Clear()
        {
            if (_flushInProgress)
                throw new InvalidOperationException("Runtime command queue cannot be cleared during Drain.");

            CancelScheduledFlush();
            _queue.Clear();
            _processing.Clear();
            _rescheduleRequested = false;
        }

        public void ResetReplayBoundary(
            long completedTick,
            ulong completedSequence)
        {
            if (completedTick < -1)
                throw new ArgumentOutOfRangeException(nameof(completedTick));
            if (_flushInProgress)
            {
                throw new InvalidOperationException(
                    "Runtime replay boundary cannot reset during Drain.");
            }
            if (_queue.Count > 0 || _processing.Count > 0)
            {
                throw new InvalidOperationException(
                    "Runtime replay boundary requires an empty command bus.");
            }

            CancelScheduledFlush();
            _journal.ResetBoundary(completedTick, completedSequence);
            _hasDrainedTick = completedTick >= 0;
            _lastApplyBeforeTick = completedTick;
            _rescheduleRequested = false;
        }

        public RuntimeCommandJournalEntry AppendOutcomeBatch(
            long applyBeforeTick,
            GameRuntimeCommand outcomeBatch,
            RuntimeCommandJournalScope scope = null)
        {
            if (applyBeforeTick < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(applyBeforeTick));
            }
            if (outcomeBatch == null)
            {
                throw new ArgumentNullException(nameof(outcomeBatch));
            }
            if (_flushInProgress)
            {
                throw new InvalidOperationException(
                    "A record-only outcome batch cannot be appended during command Drain.");
            }
            if (_replayRegistry == null)
            {
                throw new InvalidOperationException(
                    "A record-only outcome batch requires a replay command registry.");
            }
            if (!_replayRegistry.TryEncode(
                    outcomeBatch,
                    _replayCodecContext,
                    out var encoded))
            {
                throw new NotSupportedException(
                    "Runtime outcome batch has no registered replay command component.");
            }

            return _journal.AppendOutcomeBatch(
                applyBeforeTick,
                encoded,
                scope ?? ResolveJournalScope(outcomeBatch));
        }

        public RuntimeCommandJournalEntry AppendOutcomeBatch(
            long applyBeforeTick,
            in RuntimeEncodedCommand encodedBatch,
            RuntimeCommandJournalScope scope)
        {
            if (_flushInProgress)
            {
                throw new InvalidOperationException(
                    "A record-only outcome batch cannot be appended during command Drain.");
            }

            return _journal.AppendOutcomeBatch(
                applyBeforeTick,
                encodedBatch,
                scope);
        }

        public GameRuntimeCommand DecodeRecordedCommand(
            in RuntimeEncodedCommand encodedCommand)
        {
            if (_replayRegistry == null)
            {
                throw new InvalidOperationException(
                    "Recorded command decoding requires a replay command registry.");
            }

            return _replayRegistry.Decode(
                encodedCommand,
                _replayCodecContext);
        }

        public void ExecuteRecordedCommand(GameRuntimeCommand command)
        {
            if (command == null)
            {
                throw new ArgumentNullException(nameof(command));
            }
            if (_flushInProgress)
            {
                throw new InvalidOperationException(
                    "A recorded command cannot execute during command Drain.");
            }

            ExecuteCommand(command);
        }

        public int Drain(long applyBeforeTick)
        {
            if (applyBeforeTick < 0)
                throw new ArgumentOutOfRangeException(nameof(applyBeforeTick));
            if (_flushInProgress)
                throw new InvalidOperationException("Runtime command Drain cannot be re-entered.");
            if (_hasDrainedTick && applyBeforeTick < _lastApplyBeforeTick)
            {
                throw new InvalidOperationException(
                    $"Runtime command apply-before tick {applyBeforeTick} precedes the last drained tick {_lastApplyBeforeTick}.");
            }
            if (applyBeforeTick < _journal.LastApplyBeforeTick)
            {
                throw new InvalidOperationException(
                    $"Runtime command apply-before tick {applyBeforeTick} precedes the last journal tick {_journal.LastApplyBeforeTick}.");
            }

            CancelScheduledFlush();
            return DrainCore(applyBeforeTick);
        }

        private void ScheduleFlush()
        {
            if (_mode != RuntimeCommandsBusMode.AutomaticLateUpdate)
                return;
            if (_flushInProgress)
            {
                _rescheduleRequested = true;
                return;
            }
            if (_scheduled)
                return;

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, Flush, UPDATE_ORDER);
        }

        private void Flush()
        {
            CancelScheduledFlush();
            DrainCore(NextAutomaticApplyBeforeTick());
        }

        private int DrainCore(long applyBeforeTick)
        {
            var processedCount = 0;
            _flushInProgress = true;
            try
            {
                _hasDrainedTick = true;
                _lastApplyBeforeTick = applyBeforeTick;
                if (_queue.Count == 0)
                    return 0;

                _processing.Clear();
                _processing.AddRange(_queue);
                _queue.Clear();

                foreach (var cmd in _processing)
                {
                    if (cmd == null)
                        continue;

                    processedCount++;
                    var executionId = ++_nextExecutionId;
                    var encodedCommand = default(RuntimeEncodedCommand);
                    var journalScope = RuntimeCommandJournalScope.Session;
                    Exception replayEncodingFailure = null;
                    var rejectBeforeExecute = false;
                    var replayJournalExcluded = false;
                    var replayEncodingStatus =
                        RuntimeCommandExecutionStatus.Succeeded;
                    if (_replayRegistry != null)
                    {
                        try
                        {
                            replayJournalExcluded =
                                _replayStoreScope != null
                                && !IsIncludedInReplayStoreScope(
                                    cmd,
                                    _replayStoreScope);
                            if (!replayJournalExcluded
                                && !_replayRegistry.TryEncode(
                                    cmd,
                                    _replayCodecContext,
                                    out encodedCommand))
                            {
                                replayEncodingStatus =
                                    RuntimeCommandExecutionStatus.Unsupported;
                                replayEncodingFailure =
                                    new NotSupportedException(
                                        "Runtime command has no registered replay command component.");
                            }
                            if (encodedCommand.IsValid)
                            {
                                journalScope = ResolveJournalScope(cmd);
                                _journal.ValidateScopeCoverage(
                                    journalScope);
                            }
                        }
                        catch (
                            RuntimeCommandJournalScopeCoverageException
                            e)
                        {
                            rejectBeforeExecute = true;
                            replayEncodingStatus =
                                RuntimeCommandExecutionStatus.Failed;
                            replayEncodingFailure = e;
                        }
                        catch (NotSupportedException e)
                        {
                            replayEncodingStatus =
                                RuntimeCommandExecutionStatus.Unsupported;
                            replayEncodingFailure = e;
                        }
                        catch (Exception e)
                        {
                            replayEncodingStatus =
                                RuntimeCommandExecutionStatus.Failed;
                            replayEncodingFailure = e;
                        }
                    }

                    if (rejectBeforeExecute)
                    {
                        _executeFailed.Invoke(
                            cmd,
                            replayEncodingFailure);
                        _commandCompleted.Invoke(
                            new RuntimeCommandExecutionResult(
                                executionId,
                                applyBeforeTick,
                                cmd,
                                replayEncodingStatus,
                                encodedCommand,
                                default,
                                replayEncodingFailure,
                                replayJournalExcluded));
                        continue;
                    }

                    try
                    {
                        _beforeExecute.Invoke(cmd);
                        ExecuteCommand(cmd);
                        if (replayEncodingFailure != null)
                        {
                            _afterExecute.Invoke(cmd);
                            _commandCompleted.Invoke(
                                new RuntimeCommandExecutionResult(
                                    executionId,
                                    applyBeforeTick,
                                    cmd,
                                    replayEncodingStatus,
                                    encodedCommand,
                                    default,
                                    replayEncodingFailure,
                                    replayJournalExcluded));
                            continue;
                        }

                        var journalEntry = default(RuntimeCommandJournalEntry);
                        if (encodedCommand.IsValid)
                        {
                            journalEntry = _journal.AppendInput(
                                applyBeforeTick,
                                encodedCommand,
                                journalScope);
                        }

                        var success = new RuntimeCommandExecutionResult(
                            executionId,
                            applyBeforeTick,
                            cmd,
                            RuntimeCommandExecutionStatus.Succeeded,
                            encodedCommand,
                            journalEntry,
                            null,
                            replayJournalExcluded);
                        _afterExecute.Invoke(cmd);
                        _commandCompleted.Invoke(success);
                    }
                    catch (Exception e)
                    {
                        _executeFailed.Invoke(cmd, e);
                        _commandCompleted.Invoke(new RuntimeCommandExecutionResult(
                            executionId,
                            applyBeforeTick,
                            cmd,
                            RuntimeCommandExecutionStatus.Failed,
                            encodedCommand,
                            default,
                            e,
                            replayJournalExcluded));
                    }
                }

                _processing.Clear();
                return processedCount;
            }
            finally
            {
                _flushInProgress = false;
                if (_clearReplayStoreScopeRequested)
                {
                    _replayStoreScope = null;
                    _clearReplayStoreScopeRequested = false;
                }
                var needReschedule = _mode == RuntimeCommandsBusMode.AutomaticLateUpdate
                                     && (_rescheduleRequested || _queue.Count > 0);
                _rescheduleRequested = false;

                if (needReschedule)
                    ScheduleFlush();
            }
        }

        private long NextAutomaticApplyBeforeTick()
        {
            if (!_hasDrainedTick)
                return 0;
            if (_lastApplyBeforeTick == long.MaxValue)
                throw new InvalidOperationException("Runtime command apply-before tick exhausted Int64 range.");
            return _lastApplyBeforeTick + 1;
        }

        private void CancelScheduledFlush()
        {
            if (!_scheduled)
                return;

            _scheduled = false;
            CoroutineParent.RemoveLateUpdater(this);
        }

        private static void ValidateMode(RuntimeCommandsBusMode mode)
        {
            if (mode != RuntimeCommandsBusMode.AutomaticLateUpdate
                && mode != RuntimeCommandsBusMode.ExternalTickBarrier)
            {
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown runtime command bus mode.");
            }
        }

        private static void ExecuteCommand(GameRuntimeCommand command)
        {
            var components = command.Components;
            if (components == null || components.Count == 0)
                return;

            foreach (var c in components)
            {
                if (c is ICommandLogic logic)
                    logic.Execute(command);
            }
        }

        private static bool IsIncludedInReplayStoreScope(
            GameRuntimeCommand command,
            RuntimeReplayStoreScope storeScope)
        {
            var components = command.Components;
            IRuntimeReplayStoreScopedCommand classifier = null;
            if (components != null)
            {
                foreach (var component in components)
                {
                    if (component
                        is not IRuntimeReplayStoreScopedCommand candidate)
                    {
                        continue;
                    }
                    if (classifier != null)
                    {
                        throw new NotSupportedException(
                            "Runtime command contains multiple replay "
                            + "RuntimeStore scope classifiers.");
                    }

                    classifier = candidate;
                }
            }
            if (classifier == null)
            {
                throw new NotSupportedException(
                    "Runtime command has no replay RuntimeStore "
                    + "scope classifier.");
            }

            var disposition =
                classifier.ClassifyReplayStoreScope(storeScope);
            return disposition switch
            {
                RuntimeReplayStoreScopeDisposition.Included => true,
                RuntimeReplayStoreScopeDisposition.OutsideScope => false,
                _ => throw new InvalidOperationException(
                    $"Unknown replay RuntimeStore scope disposition "
                    + $"'{disposition}'."),
            };
        }

        private static RuntimeCommandJournalScope ResolveJournalScope(
            GameRuntimeCommand command)
        {
            var components = command.Components;
            IRuntimeCommandJournalScopeProvider provider = null;
            if (components != null)
            {
                foreach (var component in components)
                {
                    if (component is not IRuntimeCommandJournalScopeProvider candidate)
                    {
                        continue;
                    }
                    if (provider != null)
                    {
                        throw new NotSupportedException(
                            "Runtime command contains multiple command journal scope providers.");
                    }

                    provider = candidate;
                }
            }

            return provider?.GetRuntimeCommandJournalScope(command)
                   ?? RuntimeCommandJournalScope.Session;
        }
    }
}
