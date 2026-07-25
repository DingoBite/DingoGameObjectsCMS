using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
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
        private ulong _nextJournalSequence;
        private RuntimeCommandsBusMode _mode;
        private RuntimeReplayCommandRegistry _replayRegistry;
        private RuntimePersistentPatchCodecContext _replayCodecContext;

        private readonly SafeMulticast<GameRuntimeCommand> _beforeExecute = new();
        private readonly SafeMulticast<GameRuntimeCommand> _afterExecute = new();
        private readonly SafeMulticast<GameRuntimeCommand, Exception> _executeFailed = new();
        private readonly SafeMulticast<RuntimeCommandJournalEntry> _journalEntryRecorded = new();
        private readonly SafeMulticast<RuntimeCommandExecutionResult> _commandCompleted = new();
        private RuntimeCommandOutboundDispatcher _outboundDispatcher;

        public int QueuedCount => _queue.Count;
        public IReadOnlyList<GameRuntimeCommand> QueuedCommands => _queue;
        public bool HasOutboundDispatcher => _outboundDispatcher != null;
        public RuntimeCommandsBusMode Mode => _mode;
        public bool HasReplayRegistry => _replayRegistry != null;
        public bool HasDrainedTick => _hasDrainedTick;
        public long LastApplyBeforeTick => _hasDrainedTick ? _lastApplyBeforeTick : -1;
        public ulong JournalSequence => _nextJournalSequence;

        public RuntimeCommandsBus(
            RuntimeCommandOutboundDispatcher outboundDispatcher = null,
            RuntimeCommandsBusMode mode = RuntimeCommandsBusMode.AutomaticLateUpdate,
            RuntimeReplayCommandRegistry replayRegistry = null,
            RuntimePersistentPatchCodecContext replayCodecContext = null)
        {
            ValidateMode(mode);
            _outboundDispatcher = outboundDispatcher;
            _mode = mode;
            if (replayRegistry != null)
                SetReplayRegistry(replayRegistry, replayCodecContext);
        }

        public RuntimeCommandsBus(
            RuntimeCommandsBusMode mode,
            RuntimeReplayCommandRegistry replayRegistry = null,
            RuntimePersistentPatchCodecContext replayCodecContext = null)
            : this(null, mode, replayRegistry, replayCodecContext)
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
            add => _journalEntryRecorded.Subscribe(value);
            remove => _journalEntryRecorded.Unsubscribe(value);
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
            _nextJournalSequence = completedSequence;
            _hasDrainedTick = completedTick >= 0;
            _lastApplyBeforeTick = completedTick;
            _rescheduleRequested = false;
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
                    Exception replayEncodingFailure = null;
                    var replayEncodingStatus =
                        RuntimeCommandExecutionStatus.Succeeded;
                    if (_replayRegistry != null)
                    {
                        try
                        {
                            if (!_replayRegistry.TryEncode(
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
                                    replayEncodingFailure));
                            continue;
                        }

                        var journalEntry = default(RuntimeCommandJournalEntry);
                        if (encodedCommand.IsValid)
                        {
                            journalEntry = new RuntimeCommandJournalEntry(
                                applyBeforeTick,
                                ++_nextJournalSequence,
                                encodedCommand);
                        }

                        var success = new RuntimeCommandExecutionResult(
                            executionId,
                            applyBeforeTick,
                            cmd,
                            RuntimeCommandExecutionStatus.Succeeded,
                            encodedCommand,
                            journalEntry,
                            null);
                        if (success.HasJournalEntry)
                            _journalEntryRecorded.Invoke(journalEntry);
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
                            e));
                    }
                }

                _processing.Clear();
                return processedCount;
            }
            finally
            {
                _flushInProgress = false;
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
    }
}
