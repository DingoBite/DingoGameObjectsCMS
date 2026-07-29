using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Replay;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public class RuntimeCommandJournalBatch
    {
        private readonly RuntimeCommandJournalEntry[] _entries;
        private readonly IReadOnlyList<RuntimeCommandJournalEntry> _readOnlyEntries;

        public readonly ulong SessionId;
        public readonly string CheckpointGroupId;
        public readonly string CheckpointHash;
        public readonly ulong FromCursor;
        public readonly ulong ScannedThroughCursor;
        public readonly bool CompletesCatchup;

        public IReadOnlyList<RuntimeCommandJournalEntry> Entries => _readOnlyEntries;

        public RuntimeCommandJournalBatch(
            ulong sessionId,
            string checkpointGroupId,
            string checkpointHash,
            ulong fromCursor,
            ulong scannedThroughCursor,
            IReadOnlyList<RuntimeCommandJournalEntry> entries,
            bool completesCatchup = false)
        {
            if (sessionId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            }
            RuntimeReplayId.Validate(checkpointGroupId, nameof(checkpointGroupId));
            if (!RuntimeReplayHash.IsSha256Hex(checkpointHash))
            {
                throw new ArgumentException(
                    "Journal batch checkpoint identity must be a SHA-256 hex string.",
                    nameof(checkpointHash));
            }
            if (scannedThroughCursor < fromCursor)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(scannedThroughCursor),
                    "Journal batch high-water cursor cannot precede its source cursor.");
            }
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            _entries = new RuntimeCommandJournalEntry[entries.Count];
            var previousSequence = fromCursor;
            var previousTick = -1L;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (entry.Sequence <= previousSequence
                    || entry.Sequence > scannedThroughCursor
                    || entry.ApplyBeforeTick < previousTick
                    || entry.Scope == null
                    || !entry.EncodedCommand.IsValid)
                {
                    throw new ArgumentException(
                        $"Journal batch entry {i} is invalid or out of order.",
                        nameof(entries));
                }

                _entries[i] = entry.Copy();
                previousSequence = entry.Sequence;
                previousTick = entry.ApplyBeforeTick;
            }

            SessionId = sessionId;
            CheckpointGroupId = checkpointGroupId;
            CheckpointHash = checkpointHash.ToLowerInvariant();
            FromCursor = fromCursor;
            ScannedThroughCursor = scannedThroughCursor;
            CompletesCatchup = completesCatchup;
            _readOnlyEntries = Array.AsReadOnly(_entries);
        }

        public static RuntimeCommandJournalBatch FromRead(
            ulong sessionId,
            in RuntimeCheckpointBoundary boundary,
            in RuntimeCommandJournalReadResult read)
        {
            if (!read.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Cannot create a network journal batch from read status '{read.Status}'.");
            }

            return new RuntimeCommandJournalBatch(
                sessionId,
                boundary.GroupId,
                boundary.CheckpointHash,
                read.RequestedCursor,
                read.ScannedThroughCursor,
                read.Entries,
                completesCatchup: true);
        }
    }

    public enum RuntimeCommandJournalReceiveStatus : byte
    {
        Applied = 0,
        Buffered = 1,
        Duplicate = 2,
        CursorGap = 3,
        CheckpointMismatch = 4,
        ScopeMismatch = 5,
        UnknownCodec = 6,
        ApplyFailed = 7,
        InvalidBatch = 8,
    }

    public readonly struct RuntimeCommandJournalReceiveResult
    {
        public readonly RuntimeCommandJournalReceiveStatus Status;
        public readonly ulong Cursor;
        public readonly int AppliedEntryCount;
        public readonly Exception Exception;

        public bool Succeeded =>
            Status == RuntimeCommandJournalReceiveStatus.Applied
            || Status == RuntimeCommandJournalReceiveStatus.Buffered
            || Status == RuntimeCommandJournalReceiveStatus.Duplicate;

        public RuntimeCommandJournalReceiveResult(
            RuntimeCommandJournalReceiveStatus status,
            ulong cursor,
            int appliedEntryCount,
            Exception exception = null)
        {
            Status = status;
            Cursor = cursor;
            AppliedEntryCount = appliedEntryCount;
            Exception = exception;
        }
    }

    public class RuntimeCommandJournalCatchupReceiver
    {
        private readonly RuntimeCommandsBus _commandsBus;
        private readonly Action _completePlayback;
        private RuntimeCheckpointBoundary _boundary;
        private RuntimeCommandJournalScope _subscriptionScope;

        public ulong SessionId { get; private set; }
        public ulong Cursor { get; private set; }
        public long LastApplyBeforeTick { get; private set; }
        public bool NeedsResync { get; private set; }
        public bool CatchupComplete { get; private set; }

        public RuntimeCommandJournalCatchupReceiver(
            ulong sessionId,
            in RuntimeCheckpointBoundary boundary,
            RuntimeCommandJournalScope subscriptionScope,
            RuntimeCommandsBus commandsBus,
            Action completePlayback = null)
        {
            if (sessionId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            }

            SessionId = sessionId;
            _commandsBus = commandsBus ?? throw new ArgumentNullException(nameof(commandsBus));
            _completePlayback = completePlayback ?? (() => { });
            Reset(boundary, subscriptionScope);
        }

        public RuntimeCommandJournalReceiveResult Receive(
            RuntimeCommandJournalBatch batch)
        {
            if (batch == null)
            {
                return Fail(RuntimeCommandJournalReceiveStatus.InvalidBatch);
            }
            if (NeedsResync
                && batch.FromCursor == Cursor
                && batch.SessionId == SessionId
                && string.Equals(
                    batch.CheckpointGroupId,
                    _boundary.GroupId,
                    StringComparison.Ordinal)
                && string.Equals(
                    batch.CheckpointHash,
                    _boundary.CheckpointHash,
                    StringComparison.Ordinal))
            {
                NeedsResync = false;
            }
            if (NeedsResync)
            {
                return Fail(RuntimeCommandJournalReceiveStatus.CursorGap);
            }
            if (batch.SessionId != SessionId
                || !string.Equals(
                    batch.CheckpointGroupId,
                    _boundary.GroupId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    batch.CheckpointHash,
                    _boundary.CheckpointHash,
                    StringComparison.Ordinal))
            {
                return Fail(RuntimeCommandJournalReceiveStatus.CheckpointMismatch);
            }
            if (batch.ScannedThroughCursor <= Cursor)
            {
                if (batch.CompletesCatchup && !CatchupComplete)
                {
                    try
                    {
                        _completePlayback();
                    }
                    catch (Exception exception)
                    {
                        return Fail(
                            RuntimeCommandJournalReceiveStatus.ApplyFailed,
                            exception);
                    }

                    CatchupComplete = true;
                    return new RuntimeCommandJournalReceiveResult(
                        RuntimeCommandJournalReceiveStatus.Applied,
                        Cursor,
                        0);
                }

                return new RuntimeCommandJournalReceiveResult(
                    RuntimeCommandJournalReceiveStatus.Duplicate,
                    Cursor,
                    0);
            }
            if (batch.FromCursor != Cursor)
            {
                return Fail(RuntimeCommandJournalReceiveStatus.CursorGap);
            }

            var decoded = new GameRuntimeCommand[batch.Entries.Count];
            try
            {
                var validationTick = LastApplyBeforeTick;
                for (var i = 0; i < batch.Entries.Count; i++)
                {
                    var entry = batch.Entries[i];
                    if (entry.ApplyBeforeTick < validationTick)
                    {
                        return Fail(RuntimeCommandJournalReceiveStatus.InvalidBatch);
                    }
                    validationTick = entry.ApplyBeforeTick;
                    if (!_subscriptionScope.Covers(entry.Scope))
                    {
                        return Fail(RuntimeCommandJournalReceiveStatus.ScopeMismatch);
                    }

                    decoded[i] = _commandsBus.DecodeRecordedCommand(
                        entry.EncodedCommand);
                }
            }
            catch (Exception exception) when (exception is NotSupportedException
                                              || exception is ArgumentException
                                              || exception is InvalidOperationException
                                              || exception is FormatException)
            {
                return Fail(
                    RuntimeCommandJournalReceiveStatus.UnknownCodec,
                    exception);
            }

            try
            {
                for (var i = 0; i < decoded.Length; i++)
                {
                    _commandsBus.ExecuteRecordedCommand(decoded[i]);
                    LastApplyBeforeTick =
                        batch.Entries[i].ApplyBeforeTick;
                }
                _completePlayback();
            }
            catch (Exception exception)
            {
                return Fail(
                    RuntimeCommandJournalReceiveStatus.ApplyFailed,
                    exception);
            }

            Cursor = batch.ScannedThroughCursor;
            CatchupComplete |= batch.CompletesCatchup;
            return new RuntimeCommandJournalReceiveResult(
                RuntimeCommandJournalReceiveStatus.Applied,
                Cursor,
                decoded.Length);
        }

        public void Reset(
            in RuntimeCheckpointBoundary boundary,
            RuntimeCommandJournalScope subscriptionScope)
        {
            if (subscriptionScope == null)
            {
                throw new ArgumentNullException(nameof(subscriptionScope));
            }

            _boundary = boundary;
            _subscriptionScope = subscriptionScope;
            Cursor = boundary.JournalCursor;
            LastApplyBeforeTick = boundary.CompletedTick;
            NeedsResync = false;
            CatchupComplete = false;
        }

        private RuntimeCommandJournalReceiveResult Fail(
            RuntimeCommandJournalReceiveStatus status,
            Exception exception = null)
        {
            NeedsResync = true;
            return new RuntimeCommandJournalReceiveResult(
                status,
                Cursor,
                0,
                exception);
        }
    }
}
