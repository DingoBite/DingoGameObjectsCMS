using System;
using System.Collections.Generic;
using DingoUnityExtensions;
using Unity.Collections;

namespace DingoGameObjectsCMS.RuntimeObjects.Commands
{
    public class RuntimeCommandJournalScope : IEquatable<RuntimeCommandJournalScope>
    {
        private static readonly RuntimeCommandJournalScope SESSION = new(true, Array.Empty<FixedString32Bytes>());

        private readonly FixedString32Bytes[] _storeIds;
        private readonly IReadOnlyList<FixedString32Bytes> _readOnlyStoreIds;

        public static RuntimeCommandJournalScope Session => SESSION;
        public bool IsSessionWide { get; }
        public IReadOnlyList<FixedString32Bytes> StoreIds => _readOnlyStoreIds;
        public int StoreCount => _storeIds.Length;

        public RuntimeCommandJournalScope(params string[] storeIds)
            : this(false, ConvertAndOrder(storeIds))
        {
        }

        public RuntimeCommandJournalScope(params FixedString32Bytes[] storeIds)
            : this(false, CopyAndOrder(storeIds))
        {
        }

        private RuntimeCommandJournalScope(bool isSessionWide, FixedString32Bytes[] storeIds)
        {
            IsSessionWide = isSessionWide;
            _storeIds = storeIds;
            _readOnlyStoreIds = Array.AsReadOnly(_storeIds);
        }

        public bool Contains(in FixedString32Bytes storeId)
        {
            if (IsSessionWide)
            {
                return true;
            }

            for (var i = 0; i < _storeIds.Length; i++)
            {
                if (_storeIds[i].Equals(storeId))
                {
                    return true;
                }
            }

            return false;
        }

        public bool Covers(RuntimeCommandJournalScope commandScope)
        {
            if (commandScope == null)
            {
                throw new ArgumentNullException(nameof(commandScope));
            }
            if (IsSessionWide)
            {
                return true;
            }
            if (commandScope.IsSessionWide)
            {
                return false;
            }

            for (var i = 0; i < commandScope._storeIds.Length; i++)
            {
                if (!Contains(commandScope._storeIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public bool Overlaps(RuntimeCommandJournalScope other)
        {
            if (other == null)
            {
                throw new ArgumentNullException(nameof(other));
            }
            if (IsSessionWide || other.IsSessionWide)
            {
                return true;
            }

            for (var i = 0; i < other._storeIds.Length; i++)
            {
                if (Contains(other._storeIds[i]))
                {
                    return true;
                }
            }

            return false;
        }

        public bool Equals(RuntimeCommandJournalScope other)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }
            if (other == null
                || IsSessionWide != other.IsSessionWide
                || _storeIds.Length != other._storeIds.Length)
            {
                return false;
            }

            for (var i = 0; i < _storeIds.Length; i++)
            {
                if (!_storeIds[i].Equals(other._storeIds[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object obj)
        {
            return obj is RuntimeCommandJournalScope other && Equals(other);
        }

        public override int GetHashCode()
        {
            var hash = IsSessionWide ? 17 : 31;
            for (var i = 0; i < _storeIds.Length; i++)
            {
                hash = HashCode.Combine(hash, _storeIds[i]);
            }

            return hash;
        }

        public static bool operator ==(RuntimeCommandJournalScope left, RuntimeCommandJournalScope right)
        {
            return Equals(left, right);
        }

        public static bool operator !=(RuntimeCommandJournalScope left, RuntimeCommandJournalScope right)
        {
            return !Equals(left, right);
        }

        private static FixedString32Bytes[] ConvertAndOrder(string[] storeIds)
        {
            if (storeIds == null)
            {
                throw new ArgumentNullException(nameof(storeIds));
            }

            var converted = new FixedString32Bytes[storeIds.Length];
            for (var i = 0; i < storeIds.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(storeIds[i])
                    || !string.Equals(storeIds[i], storeIds[i].Trim(), StringComparison.Ordinal))
                {
                    throw new ArgumentException("Journal StoreId values must be non-empty and trimmed.", nameof(storeIds));
                }

                converted[i] = new FixedString32Bytes(storeIds[i]);
            }

            return OrderAndValidate(converted);
        }

        private static FixedString32Bytes[] CopyAndOrder(FixedString32Bytes[] storeIds)
        {
            if (storeIds == null)
            {
                throw new ArgumentNullException(nameof(storeIds));
            }

            var copy = new FixedString32Bytes[storeIds.Length];
            Array.Copy(storeIds, copy, storeIds.Length);
            return OrderAndValidate(copy);
        }

        private static FixedString32Bytes[] OrderAndValidate(FixedString32Bytes[] storeIds)
        {
            if (storeIds.Length == 0)
            {
                throw new ArgumentException("A store-set journal scope requires at least one StoreId.", nameof(storeIds));
            }

            Array.Sort(storeIds, (left, right) => string.CompareOrdinal(left.ToString(), right.ToString()));
            for (var i = 0; i < storeIds.Length; i++)
            {
                if (storeIds[i].Length == 0)
                {
                    throw new ArgumentException("Journal StoreId values cannot be empty.", nameof(storeIds));
                }
                if (i > 0 && storeIds[i - 1].Equals(storeIds[i]))
                {
                    throw new ArgumentException($"Journal StoreId '{storeIds[i]}' is duplicated.", nameof(storeIds));
                }
            }

            return storeIds;
        }
    }

    public class RuntimeCommandJournalScopeCoverageException :
        InvalidOperationException
    {
        public RuntimeCommandJournalScopeCoverageException(string message)
            : base(message)
        {
        }
    }

    public readonly struct RuntimeCommandJournalRetentionPolicy
    {
        public readonly int MaxEntries;
        public readonly long MaxPayloadBytes;
        public readonly double MaxAgeSeconds;
        public readonly double CheckpointWarningRatio;

        public RuntimeCommandJournalRetentionPolicy(
            int maxEntries,
            long maxPayloadBytes,
            double maxAgeSeconds,
            double checkpointWarningRatio = 0.8d)
        {
            if (maxEntries <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxEntries));
            }
            if (maxPayloadBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            }
            if (maxAgeSeconds <= 0d || double.IsNaN(maxAgeSeconds) || double.IsInfinity(maxAgeSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(maxAgeSeconds));
            }
            if (checkpointWarningRatio <= 0d
                || checkpointWarningRatio >= 1d
                || double.IsNaN(checkpointWarningRatio))
            {
                throw new ArgumentOutOfRangeException(nameof(checkpointWarningRatio));
            }

            MaxEntries = maxEntries;
            MaxPayloadBytes = maxPayloadBytes;
            MaxAgeSeconds = maxAgeSeconds;
            CheckpointWarningRatio = checkpointWarningRatio;
        }

        public void Validate()
        {
            if (MaxEntries <= 0
                || MaxPayloadBytes <= 0
                || MaxAgeSeconds <= 0d
                || double.IsNaN(MaxAgeSeconds)
                || double.IsInfinity(MaxAgeSeconds)
                || CheckpointWarningRatio <= 0d
                || CheckpointWarningRatio >= 1d
                || double.IsNaN(CheckpointWarningRatio))
            {
                throw new ArgumentException(
                    "Command journal retention policy is uninitialized or invalid.");
            }
        }
    }

    public readonly struct RuntimeCommandJournalWindowStatus
    {
        public readonly string GroupId;
        public readonly RuntimeCommandJournalScope Scope;
        public readonly RuntimeCommandJournalRetentionPolicy Policy;
        public readonly ulong CheckpointCursor;
        public readonly int RetainedEntries;
        public readonly long RetainedPayloadBytes;
        public readonly double OldestAgeSeconds;
        public readonly bool NeedsCheckpoint;
        public readonly bool RecoveryAvailable;

        public RuntimeCommandJournalWindowStatus(
            string groupId,
            RuntimeCommandJournalScope scope,
            in RuntimeCommandJournalRetentionPolicy policy,
            ulong checkpointCursor,
            int retainedEntries,
            long retainedPayloadBytes,
            double oldestAgeSeconds,
            bool needsCheckpoint,
            bool recoveryAvailable)
        {
            GroupId = groupId;
            Scope = scope;
            Policy = policy;
            CheckpointCursor = checkpointCursor;
            RetainedEntries = retainedEntries;
            RetainedPayloadBytes = retainedPayloadBytes;
            OldestAgeSeconds = oldestAgeSeconds;
            NeedsCheckpoint = needsCheckpoint;
            RecoveryAvailable = recoveryAvailable;
        }
    }

    public enum RuntimeCommandJournalReadStatus : byte
    {
        Succeeded = 0,
        UnknownGroup = 1,
        CursorBeforeCheckpoint = 2,
        CursorBeyondJournal = 3,
        RecoveryUnavailable = 4,
    }

    public readonly struct RuntimeCommandJournalReadResult
    {
        public readonly RuntimeCommandJournalReadStatus Status;
        public readonly ulong RequestedCursor;
        public readonly ulong ScannedThroughCursor;
        public readonly IReadOnlyList<RuntimeCommandJournalEntry> Entries;

        public bool Succeeded => Status == RuntimeCommandJournalReadStatus.Succeeded;

        public RuntimeCommandJournalReadResult(
            RuntimeCommandJournalReadStatus status,
            ulong requestedCursor,
            ulong scannedThroughCursor,
            IReadOnlyList<RuntimeCommandJournalEntry> entries)
        {
            Status = status;
            RequestedCursor = requestedCursor;
            ScannedThroughCursor = scannedThroughCursor;
            Entries = entries ?? Array.Empty<RuntimeCommandJournalEntry>();
        }
    }

    public class RuntimeCommandJournalWindowState
    {
        public readonly string GroupId;
        public readonly RuntimeCommandJournalScope Scope;
        public readonly RuntimeCommandJournalRetentionPolicy Policy;

        public ulong CheckpointCursor { get; private set; }
        public int RetainedEntries { get; private set; }
        public long RetainedPayloadBytes { get; private set; }
        public double OldestRecordedAtSeconds { get; private set; } = double.NaN;
        public bool NeedsCheckpoint { get; private set; }
        public bool RecoveryAvailable { get; private set; } = true;

        public RuntimeCommandJournalWindowState(
            string groupId,
            RuntimeCommandJournalScope scope,
            in RuntimeCommandJournalRetentionPolicy policy,
            ulong checkpointCursor)
        {
            GroupId = groupId;
            Scope = scope;
            Policy = policy;
            CheckpointCursor = checkpointCursor;
        }

        public void Commit(ulong cursor)
        {
            CheckpointCursor = cursor;
            RetainedEntries = 0;
            RetainedPayloadBytes = 0;
            OldestRecordedAtSeconds = double.NaN;
            NeedsCheckpoint = false;
            RecoveryAvailable = true;
        }

        public void Add(in RuntimeCommandJournalEntry entry, double recordedAtSeconds)
        {
            if (!RecoveryAvailable || entry.Sequence <= CheckpointCursor || !Scope.Covers(entry.Scope))
            {
                return;
            }

            RetainedEntries++;
            RetainedPayloadBytes += entry.EncodedCommand.PayloadBytes;
            if (double.IsNaN(OldestRecordedAtSeconds))
            {
                OldestRecordedAtSeconds = recordedAtSeconds;
            }
        }

        public bool Evaluate(double nowSeconds, out bool newlyNeedsCheckpoint, out bool newlyOverflowed)
        {
            newlyNeedsCheckpoint = false;
            newlyOverflowed = false;
            if (!RecoveryAvailable)
            {
                return false;
            }

            var oldestAge = double.IsNaN(OldestRecordedAtSeconds)
                ? 0d
                : Math.Max(0d, nowSeconds - OldestRecordedAtSeconds);
            var warning = RetainedEntries >= Math.Ceiling(Policy.MaxEntries * Policy.CheckpointWarningRatio)
                          || RetainedPayloadBytes >= Math.Ceiling(Policy.MaxPayloadBytes * Policy.CheckpointWarningRatio)
                          || oldestAge >= Policy.MaxAgeSeconds * Policy.CheckpointWarningRatio;
            if (warning && !NeedsCheckpoint)
            {
                NeedsCheckpoint = true;
                newlyNeedsCheckpoint = true;
            }

            if (RetainedEntries <= Policy.MaxEntries
                && RetainedPayloadBytes <= Policy.MaxPayloadBytes
                && oldestAge <= Policy.MaxAgeSeconds)
            {
                return true;
            }

            NeedsCheckpoint = true;
            RecoveryAvailable = false;
            newlyOverflowed = true;
            return false;
        }

        public void Invalidate(
            out bool newlyNeedsCheckpoint,
            out bool newlyUnavailable)
        {
            newlyNeedsCheckpoint = !NeedsCheckpoint;
            newlyUnavailable = RecoveryAvailable;
            NeedsCheckpoint = true;
            RecoveryAvailable = false;
        }

        public RuntimeCommandJournalWindowStatus Capture(double nowSeconds)
        {
            var oldestAge = double.IsNaN(OldestRecordedAtSeconds)
                ? 0d
                : Math.Max(0d, nowSeconds - OldestRecordedAtSeconds);
            return new RuntimeCommandJournalWindowStatus(
                GroupId,
                Scope,
                Policy,
                CheckpointCursor,
                RetainedEntries,
                RetainedPayloadBytes,
                oldestAge,
                NeedsCheckpoint,
                RecoveryAvailable);
        }
    }

    public class RuntimeCommandJournal
    {
        private readonly List<RuntimeCommandJournalEntry> _entries = new();
        private readonly List<double> _recordedAtSeconds = new();
        private readonly Dictionary<string, RuntimeCommandJournalWindowState> _windows = new(StringComparer.Ordinal);
        private readonly Func<double> _clock;
        private readonly SafeMulticast<RuntimeCommandJournalEntry> _entryRecorded = new();
        private readonly SafeMulticast<string> _windowNeedsCheckpoint = new();
        private readonly SafeMulticast<string> _windowOverflowed = new();

        public ulong Cursor { get; private set; }
        public ulong Sequence => Cursor;
        public long LastApplyBeforeTick { get; private set; } = -1;
        public int RetainedEntryCount => _entries.Count;

        public RuntimeCommandJournal(Func<double> clock = null)
        {
            _clock = clock ?? DefaultClock;
        }

        public event Action<RuntimeCommandJournalEntry> EntryRecorded
        {
            add => _entryRecorded.Subscribe(value);
            remove => _entryRecorded.Unsubscribe(value);
        }

        public event Action<string> WindowNeedsCheckpoint
        {
            add => _windowNeedsCheckpoint.Subscribe(value);
            remove => _windowNeedsCheckpoint.Unsubscribe(value);
        }

        public event Action<string> WindowOverflowed
        {
            add => _windowOverflowed.Subscribe(value);
            remove => _windowOverflowed.Unsubscribe(value);
        }

        public void ConfigureWindow(
            string groupId,
            RuntimeCommandJournalScope scope,
            in RuntimeCommandJournalRetentionPolicy policy)
        {
            ValidateGroupId(groupId);
            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }
            policy.Validate();
            if (_windows.ContainsKey(groupId))
            {
                throw new InvalidOperationException($"Journal checkpoint group '{groupId}' is already configured.");
            }

            _windows.Add(groupId, new RuntimeCommandJournalWindowState(groupId, scope, policy, Cursor));
        }

        public bool TryGetWindow(string groupId, out RuntimeCommandJournalWindowStatus status)
        {
            if (!_windows.TryGetValue(groupId, out var window))
            {
                status = default;
                return false;
            }

            var now = _clock();
            var overflowed = Evaluate(window, now);
            if (overflowed)
            {
                PruneUnneededEntries();
            }
            status = window.Capture(now);
            return true;
        }

        public RuntimeCommandJournalEntry AppendInput(
            long applyBeforeTick,
            in RuntimeEncodedCommand encodedCommand,
            RuntimeCommandJournalScope scope)
        {
            return Append(applyBeforeTick, encodedCommand, scope);
        }

        public RuntimeCommandJournalEntry AppendOutcomeBatch(
            long applyBeforeTick,
            in RuntimeEncodedCommand encodedBatch,
            RuntimeCommandJournalScope scope)
        {
            return Append(applyBeforeTick, encodedBatch, scope);
        }

        public void ValidateScopeCoverage(
            RuntimeCommandJournalScope commandScope)
        {
            if (commandScope == null)
            {
                throw new ArgumentNullException(nameof(commandScope));
            }

            foreach (var window in _windows.Values)
            {
                if (window.Scope.Overlaps(commandScope)
                    && !window.Scope.Covers(commandScope))
                {
                    throw CreateScopeCoverageException(
                        window);
                }
            }
        }

        public RuntimeCommandJournalReadResult ReadAfter(string groupId, ulong cursor)
        {
            if (!_windows.TryGetValue(groupId, out var window))
            {
                return new RuntimeCommandJournalReadResult(
                    RuntimeCommandJournalReadStatus.UnknownGroup,
                    cursor,
                    Cursor,
                    null);
            }

            var overflowed = Evaluate(window, _clock());
            if (overflowed)
            {
                PruneUnneededEntries();
            }
            if (!window.RecoveryAvailable)
            {
                return new RuntimeCommandJournalReadResult(
                    RuntimeCommandJournalReadStatus.RecoveryUnavailable,
                    cursor,
                    Cursor,
                    null);
            }
            if (cursor < window.CheckpointCursor)
            {
                return new RuntimeCommandJournalReadResult(
                    RuntimeCommandJournalReadStatus.CursorBeforeCheckpoint,
                    cursor,
                    Cursor,
                    null);
            }
            if (cursor > Cursor)
            {
                return new RuntimeCommandJournalReadResult(
                    RuntimeCommandJournalReadStatus.CursorBeyondJournal,
                    cursor,
                    Cursor,
                    null);
            }

            var result = new List<RuntimeCommandJournalEntry>();
            for (var i = 0; i < _entries.Count; i++)
            {
                var entry = _entries[i];
                if (entry.Sequence > cursor && window.Scope.Covers(entry.Scope))
                {
                    result.Add(entry.Copy());
                }
            }

            return new RuntimeCommandJournalReadResult(
                RuntimeCommandJournalReadStatus.Succeeded,
                cursor,
                Cursor,
                Array.AsReadOnly(result.ToArray()));
        }

        public void CommitCheckpoint(string groupId, ulong cursor)
        {
            if (!_windows.TryGetValue(groupId, out var window))
            {
                throw new KeyNotFoundException($"Journal checkpoint group '{groupId}' is not configured.");
            }
            if (cursor > Cursor)
            {
                throw new ArgumentOutOfRangeException(nameof(cursor), cursor, "Checkpoint cursor cannot exceed the journal cursor.");
            }
            if (!window.RecoveryAvailable && cursor != Cursor)
            {
                throw new InvalidOperationException(
                    $"Journal checkpoint group '{groupId}' lost its recovery window and must checkpoint the current cursor {Cursor}.");
            }
            if (window.RecoveryAvailable && cursor < window.CheckpointCursor)
            {
                throw new InvalidOperationException(
                    $"Journal checkpoint cursor {cursor} precedes group '{groupId}' boundary {window.CheckpointCursor}.");
            }

            window.Commit(cursor);
            RebuildWindowMetrics(window);
            PruneUnneededEntries();
        }

        public void ResetBoundary(long completedTick, ulong completedSequence)
        {
            if (completedTick < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(completedTick));
            }

            Cursor = completedSequence;
            LastApplyBeforeTick = completedTick;
            _entries.Clear();
            _recordedAtSeconds.Clear();
            foreach (var window in _windows.Values)
            {
                window.Commit(completedSequence);
            }
        }

        private RuntimeCommandJournalEntry Append(
            long applyBeforeTick,
            in RuntimeEncodedCommand encodedCommand,
            RuntimeCommandJournalScope scope)
        {
            if (scope == null)
            {
                throw new ArgumentNullException(nameof(scope));
            }
            InvalidateAndRejectIncompleteScope(scope);
            if (applyBeforeTick < LastApplyBeforeTick)
            {
                throw new InvalidOperationException(
                    $"Runtime command journal tick {applyBeforeTick} precedes the last recorded tick {LastApplyBeforeTick}.");
            }
            if (Cursor == ulong.MaxValue)
            {
                throw new InvalidOperationException("Runtime command journal sequence is exhausted.");
            }

            var now = _clock();
            var entry = new RuntimeCommandJournalEntry(
                applyBeforeTick,
                Cursor + 1,
                encodedCommand,
                scope);
            Cursor = entry.Sequence;
            LastApplyBeforeTick = applyBeforeTick;

            var retained = false;
            foreach (var window in _windows.Values)
            {
                if (!window.RecoveryAvailable || !window.Scope.Covers(scope))
                {
                    continue;
                }

                retained = true;
                window.Add(entry, now);
            }
            if (retained)
            {
                _entries.Add(entry);
                _recordedAtSeconds.Add(now);
            }

            var overflowed = false;
            foreach (var window in _windows.Values)
            {
                overflowed |= Evaluate(window, now);
            }
            if (overflowed)
            {
                PruneUnneededEntries();
            }
            _entryRecorded.Invoke(entry);
            return entry;
        }

        private void InvalidateAndRejectIncompleteScope(
            RuntimeCommandJournalScope commandScope)
        {
            RuntimeCommandJournalWindowState incompleteWindow = null;
            foreach (var window in _windows.Values)
            {
                if (window.Scope.Overlaps(commandScope)
                    && !window.Scope.Covers(commandScope))
                {
                    incompleteWindow = window;
                    break;
                }
            }
            if (incompleteWindow == null)
            {
                return;
            }

            foreach (var window in _windows.Values)
            {
                if (!window.Scope.Overlaps(commandScope))
                {
                    continue;
                }

                window.Invalidate(
                    out var newlyNeedsCheckpoint,
                    out var newlyUnavailable);
                if (newlyNeedsCheckpoint)
                {
                    _windowNeedsCheckpoint.Invoke(window.GroupId);
                }
                if (newlyUnavailable)
                {
                    _windowOverflowed.Invoke(window.GroupId);
                }
            }
            PruneUnneededEntries();
            throw CreateScopeCoverageException(
                incompleteWindow);
        }

        private static RuntimeCommandJournalScopeCoverageException
            CreateScopeCoverageException(
                RuntimeCommandJournalWindowState window)
        {
            return new RuntimeCommandJournalScopeCoverageException(
                $"Journal checkpoint group '{window.GroupId}' overlaps a command scope that it does not fully cover. "
                + "A multi-store command must be delivered only through a checkpoint group containing its complete scope.");
        }

        private bool Evaluate(RuntimeCommandJournalWindowState window, double now)
        {
            window.Evaluate(now, out var newlyNeedsCheckpoint, out var newlyOverflowed);
            if (newlyNeedsCheckpoint)
            {
                _windowNeedsCheckpoint.Invoke(window.GroupId);
            }
            if (newlyOverflowed)
            {
                _windowOverflowed.Invoke(window.GroupId);
            }

            return newlyOverflowed;
        }

        private void RebuildWindowMetrics(RuntimeCommandJournalWindowState window)
        {
            var checkpointCursor = window.CheckpointCursor;
            window.Commit(checkpointCursor);
            for (var i = 0; i < _entries.Count; i++)
            {
                window.Add(_entries[i], _recordedAtSeconds[i]);
            }
            Evaluate(window, _clock());
        }

        private void PruneUnneededEntries()
        {
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                var needed = false;
                foreach (var window in _windows.Values)
                {
                    if (window.RecoveryAvailable
                        && entry.Sequence > window.CheckpointCursor
                        && window.Scope.Covers(entry.Scope))
                    {
                        needed = true;
                        break;
                    }
                }
                if (needed)
                {
                    continue;
                }

                _entries.RemoveAt(i);
                _recordedAtSeconds.RemoveAt(i);
            }
        }

        private static void ValidateGroupId(string groupId)
        {
            if (string.IsNullOrWhiteSpace(groupId)
                || !string.Equals(groupId, groupId.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Journal checkpoint group id must be non-empty and trimmed.", nameof(groupId));
            }
        }

        private static double DefaultClock()
        {
            return DateTime.UtcNow.Ticks / (double)TimeSpan.TicksPerSecond;
        }
    }
}
