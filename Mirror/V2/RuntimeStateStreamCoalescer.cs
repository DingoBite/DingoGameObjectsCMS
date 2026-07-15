using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeStateStreamSubmitResult : byte
    {
        Accepted = 1,
        StaleSimulationTick = 2,
    }

    public enum RuntimeStateStreamFrameBuildResult : byte
    {
        Built = 1,
        NotDue = 2,
        Empty = 3,
        StaleSimulationTick = 4,
    }

    public class RuntimeConnectionStateStreamEntry
    {
        public RuntimePackedStateStreamSample Sample;
        public ulong Revision;
        public bool Pending;
        public int StopFramesRemaining;
        public int DespawnFramesRemaining;
        public bool RemoveAfterFrame;
        public bool HasBeenSent;
        public double LastSentTimeSeconds;
    }

    internal readonly struct RuntimeStateStreamReconciliationEntry
    {
        public readonly RuntimePackedStateStreamSample Sample;
        public readonly ulong EntryRevision;
        public readonly int StopFramesBefore;
        public readonly bool HasBeenSentBefore;
        public readonly double LastSentTimeSecondsBefore;

        public RuntimeStateStreamReconciliationEntry(
            RuntimePackedStateStreamSample sample,
            ulong entryRevision,
            int stopFramesBefore,
            bool hasBeenSentBefore,
            double lastSentTimeSecondsBefore)
        {
            Sample = sample;
            EntryRevision = entryRevision;
            StopFramesBefore = stopFramesBefore;
            HasBeenSentBefore = hasBeenSentBefore;
            LastSentTimeSecondsBefore = lastSentTimeSecondsBefore;
        }
    }

    public class RuntimeConnectionStateStreamCoalescer
    {
        private const double TIME_EPSILON = 0.000000001d;

        private readonly Dictionary<RuntimeStateStreamKey, RuntimeConnectionStateStreamEntry> _entries = new();
        private readonly RuntimeStateStreamSequenceCursor _sequenceCursor;
        private readonly List<RuntimeStateStreamReconciliationEntry> _reconciliationSamples = new();
        private readonly bool _heartbeatRetainedState;
        private double _nextFrameTime;
        private double _nextKeyframeTime;
        private bool _hasSubmittedTick;
        private bool _hasBuiltTick;
        private bool _hasSpillCursor;
        private bool _keyframeRequested;
        private uint _lastSubmittedTick;
        private uint _lastBuiltTick;
        private uint _activeReconciliationId;
        private uint _activeReconciliationTick;
        private int _reconciliationOffset;
        private long _spillCursorKey;
        private ulong _entryRevisionCursor;

        public readonly NetStoreRef Store;
        public readonly uint StreamTypeId;

        public int RetainedKeyCount => _entries.Count;
        public uint LastSubmittedSimulationTick => _lastSubmittedTick;
        public uint LastBuiltSimulationTick => _lastBuiltTick;
        public uint LastSequence => _sequenceCursor.LastSequence;
        public RuntimeStateStreamSequenceCursor SequenceCursor => _sequenceCursor;

        public RuntimeConnectionStateStreamCoalescer(
            NetStoreRef store,
            RuntimeStateStreamProfile profile,
            double startTimeSeconds = 0d,
            RuntimeStateStreamSequenceCursor sequenceCursor = null)
        {
            if (!store.IsValid)
                throw new ArgumentException("State stream coalescer requires a valid store reference.", nameof(store));
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (double.IsNaN(startTimeSeconds) || double.IsInfinity(startTimeSeconds))
                throw new ArgumentOutOfRangeException(nameof(startTimeSeconds));
            Store = store;
            StreamTypeId = profile.StreamTypeId;
            _heartbeatRetainedState = profile.Lifetime == RuntimeStateStreamLifetime.EphemeralStreamEntity;
            _sequenceCursor = sequenceCursor ?? new RuntimeStateStreamSequenceCursor(store, profile.StreamTypeId);
            if (_sequenceCursor.Store != store || _sequenceCursor.StreamTypeId != profile.StreamTypeId)
            {
                throw new ArgumentException(
                    $"State stream sequence cursor '{_sequenceCursor.Store}/{_sequenceCursor.StreamTypeId}' cannot drive '{store}/{profile.StreamTypeId}'.",
                    nameof(sequenceCursor));
            }
            _nextFrameTime = startTimeSeconds + RuntimeStateStreamProtocol.SEND_INTERVAL_SECONDS;
            _nextKeyframeTime = startTimeSeconds + RuntimeStateStreamProtocol.KEYFRAME_INTERVAL_SECONDS;
        }

        public RuntimeStateStreamSubmitResult Submit(
            uint simulationTick,
            IReadOnlyList<RuntimePackedStateStreamSample> samples)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            if (_hasSubmittedTick && !RuntimeStateStreamSequence.IsNewer(simulationTick, _lastSubmittedTick))
                return RuntimeStateStreamSubmitResult.StaleSimulationTick;

            var seen = new HashSet<RuntimeStateStreamKey>();
            for (var i = 0; i < samples.Count; i++)
            {
                if (!seen.Add(samples[i].Key))
                    throw new InvalidOperationException($"State stream submission contains duplicate key '{samples[i].Key}'.");
            }
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (!_entries.TryGetValue(sample.Key, out var entry))
                {
                    entry = new RuntimeConnectionStateStreamEntry();
                    _entries.Add(sample.Key, entry);
                }
                entry.Revision = TakeNextEntryRevision(sample.Key);
                entry.Sample = sample;
                entry.Pending = true;
                entry.RemoveAfterFrame = false;
                entry.DespawnFramesRemaining = 0;
                entry.StopFramesRemaining = sample.IsStop
                    ? RuntimeStateStreamProtocol.STOP_FRAME_REPETITIONS
                    : 0;
            }
            _hasSubmittedTick = true;
            _lastSubmittedTick = simulationTick;
            return RuntimeStateStreamSubmitResult.Accepted;
        }

        /// <summary>
        /// Requests an authoritative complete-set cycle at the next send slot.
        /// Large snapshots are segmented; normal pending samples wait until the
        /// immutable Begin..End cycle has been emitted.
        /// </summary>
        public void RequestKeyframe()
        {
            _keyframeRequested = true;
        }

        public RuntimeStateStreamFrameBuildResult TryBuildFrame(
            double nowSeconds,
            uint simulationTick,
            Func<RuntimeStateStreamKey, bool> isEligible,
            out RuntimeStateStreamFrame frame)
        {
            return TryBuildFrame(
                nowSeconds,
                simulationTick,
                isEligible,
                RuntimeStateStreamProtocol.MAX_UNRELIABLE_PAYLOAD_BYTES,
                out frame);
        }

        public RuntimeStateStreamFrameBuildResult TryBuildFrame(
            double nowSeconds,
            uint simulationTick,
            Func<RuntimeStateStreamKey, bool> isEligible,
            int maxPayloadBytes,
            out RuntimeStateStreamFrame frame)
        {
            frame = null;
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
                throw new ArgumentOutOfRangeException(nameof(nowSeconds));
            if (isEligible == null)
                throw new ArgumentNullException(nameof(isEligible));
            var headerSize = RuntimeStateStreamFrameCodec.CalculateHeaderSize(Store);
            if (maxPayloadBytes <= headerSize || maxPayloadBytes > RuntimeStateStreamProtocol.MAX_UNRELIABLE_PAYLOAD_BYTES)
                throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
            if (nowSeconds + TIME_EPSILON < _nextFrameTime)
                return RuntimeStateStreamFrameBuildResult.NotDue;
            if (_hasBuiltTick && !RuntimeStateStreamSequence.IsNewer(simulationTick, _lastBuiltTick))
                return RuntimeStateStreamFrameBuildResult.StaleSimulationTick;
            if (_hasSubmittedTick
                && simulationTick != _lastSubmittedTick
                && !RuntimeStateStreamSequence.IsNewer(simulationTick, _lastSubmittedTick))
            {
                return RuntimeStateStreamFrameBuildResult.StaleSimulationTick;
            }

            AdvanceFrameSchedule(nowSeconds);
            if (_activeReconciliationId != 0)
            {
                if (!CanContinueReconciliation(isEligible))
                {
                    AbortReconciliation();
                    BeginReconciliation(simulationTick, isEligible, headerSize, maxPayloadBytes);
                }
                frame = BuildReconciliationSegment(nowSeconds, simulationTick, headerSize, maxPayloadBytes);
                return RuntimeStateStreamFrameBuildResult.Built;
            }

            var periodicKeyframeDue = nowSeconds + TIME_EPSILON >= _nextKeyframeTime;
            var keyframeDue = _keyframeRequested || periodicKeyframeDue;
            if (keyframeDue)
            {
                _keyframeRequested = false;
                if (periodicKeyframeDue)
                    AdvanceKeyframeSchedule(nowSeconds);
                BeginReconciliation(simulationTick, isEligible, headerSize, maxPayloadBytes);
                frame = BuildReconciliationSegment(nowSeconds, simulationTick, headerSize, maxPayloadBytes);
                return RuntimeStateStreamFrameBuildResult.Built;
            }

            var keys = new List<RuntimeStateStreamKey>();
            foreach (var pair in _entries)
            {
                if (!isEligible(pair.Key))
                    continue;
                if (!pair.Value.Pending
                    && pair.Value.StopFramesRemaining <= 0
                    && pair.Value.DespawnFramesRemaining <= 0
                    && (!_heartbeatRetainedState
                        || !pair.Value.HasBeenSent
                        || nowSeconds - pair.Value.LastSentTimeSeconds + TIME_EPSILON
                        < RuntimeStateStreamProtocol.HEARTBEAT_INTERVAL_SECONDS))
                    continue;
                keys.Add(pair.Key);
            }
            keys.Sort((first, second) => first.Value.CompareTo(second.Value));
            if (keys.Count == 0)
                return RuntimeStateStreamFrameBuildResult.Empty;

            var startIndex = 0;
            if (_hasSpillCursor)
            {
                while (startIndex < keys.Count && keys[startIndex].Value <= _spillCursorKey)
                {
                    startIndex++;
                }
                if (startIndex == keys.Count)
                    startIndex = 0;
            }

            var selected = new List<RuntimeStateStreamKey>();
            var encodedBytes = headerSize;
            for (var offset = 0; offset < keys.Count; offset++)
            {
                if (selected.Count >= RuntimeStateStreamProtocol.MAX_SAMPLES_PER_FRAME)
                    break;
                var key = keys[(startIndex + offset) % keys.Count];
                var sampleSize = RuntimeStateStreamFrameCodec.CalculateSampleSize(_entries[key].Sample);
                if (encodedBytes + sampleSize > maxPayloadBytes)
                {
                    if (selected.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"State stream sample '{key}' exceeds payload budget {maxPayloadBytes}.");
                    }
                    break;
                }
                selected.Add(key);
                encodedBytes += sampleSize;
            }

            var samples = new RuntimePackedStateStreamSample[selected.Count];
            for (var i = 0; i < selected.Count; i++)
            {
                var entry = _entries[selected[i]];
                samples[i] = entry.Sample;
                entry.Pending = false;
                entry.HasBeenSent = true;
                entry.LastSentTimeSeconds = nowSeconds;
                if (entry.StopFramesRemaining > 0)
                    entry.StopFramesRemaining--;
                if (entry.DespawnFramesRemaining > 0)
                {
                    entry.DespawnFramesRemaining--;
                    if (entry.DespawnFramesRemaining == 0)
                        entry.RemoveAfterFrame = true;
                }
            }
            for (var i = 0; i < selected.Count; i++)
            {
                var key = selected[i];
                if (_entries.TryGetValue(key, out var entry) && entry.RemoveAfterFrame)
                    _entries.Remove(key);
            }
            _spillCursorKey = selected[selected.Count - 1].Value;
            _hasSpillCursor = true;
            Array.Sort(samples, (first, second) => first.Key.Value.CompareTo(second.Key.Value));
            frame = new RuntimeStateStreamFrame(
                Store,
                StreamTypeId,
                _sequenceCursor.TakeNext(),
                simulationTick,
                false,
                samples);
            _hasBuiltTick = true;
            _lastBuiltTick = simulationTick;
            return RuntimeStateStreamFrameBuildResult.Built;
        }

        public bool Forget(RuntimeStateStreamKey key)
        {
            if (!key.IsValid)
                throw new ArgumentException("State stream key is invalid.", nameof(key));
            return _entries.Remove(key);
        }

        public void Despawn(RuntimeStateStreamKey key)
        {
            if (!key.IsValid)
                throw new ArgumentException("State stream key is invalid.", nameof(key));
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new RuntimeConnectionStateStreamEntry();
                _entries.Add(key, entry);
            }
            entry.Revision = TakeNextEntryRevision(key);
            entry.Sample = new RuntimePackedStateStreamSample(
                key,
                RuntimeStateStreamSampleFlags.Despawn,
                Array.Empty<byte>());
            entry.Pending = true;
            entry.StopFramesRemaining = 0;
            entry.DespawnFramesRemaining = RuntimeStateStreamProtocol.DESPAWN_FRAME_REPETITIONS;
            entry.RemoveAfterFrame = false;
        }

        private void BeginReconciliation(
            uint simulationTick,
            Func<RuntimeStateStreamKey, bool> isEligible,
            int headerSize,
            int maxPayloadBytes)
        {
            _reconciliationSamples.Clear();
            var packedBytes = 0;
            foreach (var pair in _entries)
            {
                var entry = pair.Value;
                if (!isEligible(pair.Key) || entry.Sample.IsDespawn)
                    continue;
                if (_reconciliationSamples.Count >= RuntimeStateStreamProtocol.MAX_RECONCILIATION_SAMPLES)
                {
                    throw new InvalidOperationException(
                        $"State stream '{StreamTypeId}' reconciliation exceeds " +
                        $"{RuntimeStateStreamProtocol.MAX_RECONCILIATION_SAMPLES} samples.");
                }
                var sampleBytes = RuntimeStateStreamFrameCodec.CalculateSampleSize(entry.Sample);
                if (headerSize + sampleBytes > maxPayloadBytes)
                {
                    throw new InvalidOperationException(
                        $"State stream reconciliation sample '{pair.Key}' exceeds payload budget {maxPayloadBytes}.");
                }
                packedBytes = checked(packedBytes + sampleBytes);
                if (packedBytes > RuntimeStateStreamProtocol.MAX_RECONCILIATION_PACKED_BYTES)
                {
                    throw new InvalidOperationException(
                        $"State stream '{StreamTypeId}' reconciliation exceeds " +
                        $"{RuntimeStateStreamProtocol.MAX_RECONCILIATION_PACKED_BYTES} packed bytes.");
                }
                _reconciliationSamples.Add(new RuntimeStateStreamReconciliationEntry(
                    new RuntimePackedStateStreamSample(
                        entry.Sample.Key,
                        entry.Sample.Flags,
                        entry.Sample.PackedState),
                    entry.Revision,
                    entry.StopFramesRemaining,
                    entry.HasBeenSent,
                    entry.LastSentTimeSeconds));
            }
            _reconciliationSamples.Sort((first, second) =>
                first.Sample.Key.Value.CompareTo(second.Sample.Key.Value));
            var segmentCount = CalculateReconciliationSegmentCount(
                _reconciliationSamples,
                headerSize,
                maxPayloadBytes);
            if (segmentCount > RuntimeStateStreamProtocol.MAX_RECONCILIATION_SEGMENTS)
            {
                _reconciliationSamples.Clear();
                throw new InvalidOperationException(
                    $"State stream '{StreamTypeId}' reconciliation requires {segmentCount} segments; " +
                    $"the maximum is {RuntimeStateStreamProtocol.MAX_RECONCILIATION_SEGMENTS} " +
                    $"({RuntimeStateStreamProtocol.STALE_TIMEOUT_SECONDS:0.###} seconds at " +
                    $"{RuntimeStateStreamProtocol.SEND_RATE_HZ} Hz). Shard the population across typed streams.");
            }
            for (var i = 0; i < _reconciliationSamples.Count; i++)
            {
                if (_entries.TryGetValue(_reconciliationSamples[i].Sample.Key, out var entry))
                {
                    entry.Pending = false;
                    if (entry.StopFramesRemaining > 0)
                        entry.StopFramesRemaining--;
                }
            }
            // The first delivery sequence is also the cycle identity. The
            // cursor survives rebaseline, so a replacement encoder cannot
            // accidentally reuse the id of an incomplete receiver buffer.
            _activeReconciliationId = RuntimeStateStreamSequence.Next(_sequenceCursor.LastSequence);
            _activeReconciliationTick = simulationTick;
            _reconciliationOffset = 0;
        }

        private bool CanContinueReconciliation(Func<RuntimeStateStreamKey, bool> isEligible)
        {
            // Recheck the complete immutable set, including fragments that
            // were already emitted. If one key left interest, finishing the
            // old cycle would make its buffered prefix visible at End.
            for (var i = 0; i < _reconciliationSamples.Count; i++)
            {
                var key = _reconciliationSamples[i].Sample.Key;
                if (!_entries.TryGetValue(key, out var entry)
                    || entry.Sample.IsDespawn
                    || !isEligible(key))
                {
                    return false;
                }
            }
            return true;
        }

        private void AbortReconciliation()
        {
            // No End was emitted, so the receiver cannot have observed any
            // snapshot value. Restore unchanged entries as pending and roll
            // back snapshot-only stop/heartbeat accounting. A newer Submit or
            // Despawn owns its own counters and remains untouched.
            for (var i = 0; i < _reconciliationSamples.Count; i++)
            {
                var snapshot = _reconciliationSamples[i];
                if (!_entries.TryGetValue(snapshot.Sample.Key, out var entry)
                    || entry.Revision != snapshot.EntryRevision)
                {
                    continue;
                }
                entry.Pending = true;
                entry.StopFramesRemaining = snapshot.StopFramesBefore;
                entry.HasBeenSent = snapshot.HasBeenSentBefore;
                entry.LastSentTimeSeconds = snapshot.LastSentTimeSecondsBefore;
            }

            _activeReconciliationId = 0;
            _activeReconciliationTick = 0;
            _reconciliationOffset = 0;
            _reconciliationSamples.Clear();
        }

        private static int CalculateReconciliationSegmentCount(
            IReadOnlyList<RuntimeStateStreamReconciliationEntry> samples,
            int headerSize,
            int maxPayloadBytes)
        {
            if (samples.Count == 0)
                return 1;

            var segmentCount = 1;
            var segmentSampleCount = 0;
            var segmentBytes = headerSize;
            for (var i = 0; i < samples.Count; i++)
            {
                var sampleBytes = RuntimeStateStreamFrameCodec.CalculateSampleSize(samples[i].Sample);
                if (segmentSampleCount >= RuntimeStateStreamProtocol.MAX_SAMPLES_PER_FRAME
                    || segmentBytes + sampleBytes > maxPayloadBytes)
                {
                    segmentCount++;
                    segmentSampleCount = 0;
                    segmentBytes = headerSize;
                }

                // BeginReconciliation already validates that every individual
                // sample fits. Keep the invariant local to this exact packing
                // preflight so future callers cannot accidentally diverge.
                if (segmentBytes + sampleBytes > maxPayloadBytes)
                {
                    throw new InvalidOperationException(
                        $"State stream reconciliation sample '{samples[i].Sample.Key}' exceeds payload budget {maxPayloadBytes}.");
                }
                segmentSampleCount++;
                segmentBytes += sampleBytes;
            }
            return segmentCount;
        }

        private RuntimeStateStreamFrame BuildReconciliationSegment(
            double nowSeconds,
            uint buildRequestTick,
            int headerSize,
            int maxPayloadBytes)
        {
            var isBegin = _reconciliationOffset == 0;
            var selectedCount = 0;
            var encodedBytes = headerSize;
            while (_reconciliationOffset + selectedCount < _reconciliationSamples.Count
                   && selectedCount < RuntimeStateStreamProtocol.MAX_SAMPLES_PER_FRAME)
            {
                var sample = _reconciliationSamples[_reconciliationOffset + selectedCount].Sample;
                var sampleSize = RuntimeStateStreamFrameCodec.CalculateSampleSize(sample);
                if (encodedBytes + sampleSize > maxPayloadBytes)
                {
                    if (selectedCount == 0)
                    {
                        throw new InvalidOperationException(
                            $"State stream reconciliation sample '{sample.Key}' exceeds payload budget {maxPayloadBytes}.");
                    }
                    break;
                }
                selectedCount++;
                encodedBytes += sampleSize;
            }

            var isEnd = _reconciliationOffset + selectedCount == _reconciliationSamples.Count;
            var flags = RuntimeStateStreamFrameFlags.None;
            if (isBegin)
                flags |= RuntimeStateStreamFrameFlags.ReconciliationBegin;
            if (isEnd)
                flags |= RuntimeStateStreamFrameFlags.ReconciliationEnd;
            var samples = new RuntimePackedStateStreamSample[selectedCount];
            for (var i = 0; i < selectedCount; i++)
            {
                var sample = _reconciliationSamples[_reconciliationOffset + i].Sample;
                samples[i] = sample;
                if (_entries.TryGetValue(sample.Key, out var entry))
                {
                    entry.HasBeenSent = true;
                    entry.LastSentTimeSeconds = nowSeconds;
                }
            }

            var frame = new RuntimeStateStreamFrame(
                Store,
                StreamTypeId,
                _sequenceCursor.TakeNext(),
                _activeReconciliationTick,
                _activeReconciliationId,
                flags,
                samples);
            _reconciliationOffset += selectedCount;
            if (isEnd)
            {
                _activeReconciliationId = 0;
                _activeReconciliationTick = 0;
                _reconciliationOffset = 0;
                _reconciliationSamples.Clear();
                _nextKeyframeTime = Math.Max(
                    _nextKeyframeTime,
                    nowSeconds + RuntimeStateStreamProtocol.KEYFRAME_INTERVAL_SECONDS);
            }
            _hasBuiltTick = true;
            _lastBuiltTick = buildRequestTick;
            return frame;
        }

        private void AdvanceFrameSchedule(double nowSeconds)
        {
            var frameIntervals = Math.Floor(
                (nowSeconds + TIME_EPSILON - _nextFrameTime) / RuntimeStateStreamProtocol.SEND_INTERVAL_SECONDS) + 1d;
            _nextFrameTime += frameIntervals * RuntimeStateStreamProtocol.SEND_INTERVAL_SECONDS;
        }

        private void AdvanceKeyframeSchedule(double nowSeconds)
        {
            var keyframeIntervals = Math.Floor(
                (nowSeconds + TIME_EPSILON - _nextKeyframeTime) / RuntimeStateStreamProtocol.KEYFRAME_INTERVAL_SECONDS) + 1d;
            _nextKeyframeTime += keyframeIntervals * RuntimeStateStreamProtocol.KEYFRAME_INTERVAL_SECONDS;
        }

        private ulong TakeNextEntryRevision(RuntimeStateStreamKey key)
        {
            if (_entryRevisionCursor == ulong.MaxValue)
                throw new InvalidOperationException($"State stream entry '{key}' exhausted its revision range.");
            _entryRevisionCursor++;
            return _entryRevisionCursor;
        }

    }
}
