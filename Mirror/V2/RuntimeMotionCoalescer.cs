using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeMotionSubmitResult : byte
    {
        Accepted = 1,
        StaleSimulationTick = 2,
    }

    public enum RuntimeMotionFrameBuildResult : byte
    {
        Built = 1,
        NotDue = 2,
        Empty = 3,
        StaleSimulationTick = 4,
    }

    public class RuntimeConnectionMotionCoalescer
    {
        private const double TIME_EPSILON = 0.000000001d;

        private readonly Dictionary<NetObjectRef, MotionEntry> _entries = new();
        private double _nextFrameTime;
        private double _nextKeyframeTime;
        private bool _hasSubmittedTick;
        private bool _hasBuiltTick;
        private bool _hasSpillCursor;
        private uint _lastSubmittedTick;
        private uint _lastBuiltTick;
        private long _spillCursorObjectId;

        public readonly NetStoreRef Store;

        public int RetainedObjectCount => _entries.Count;
        public uint LastSubmittedSimulationTick => _lastSubmittedTick;
        public uint LastBuiltSimulationTick => _lastBuiltTick;

        public RuntimeConnectionMotionCoalescer(NetStoreRef store, double startTimeSeconds = 0d)
        {
            if (!store.IsValid)
                throw new ArgumentException("Motion coalescer requires a valid store reference.", nameof(store));
            if (double.IsNaN(startTimeSeconds) || double.IsInfinity(startTimeSeconds))
                throw new ArgumentOutOfRangeException(nameof(startTimeSeconds), "Start time must be finite.");

            Store = store;
            _nextFrameTime = startTimeSeconds + RuntimeMotionProtocol.SEND_INTERVAL_SECONDS;
            _nextKeyframeTime = startTimeSeconds + RuntimeMotionProtocol.KEYFRAME_INTERVAL_SECONDS;
        }

        public RuntimeMotionSubmitResult Submit(uint simulationTick, IReadOnlyList<RuntimeMotionSample> samples)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            if (_hasSubmittedTick && !RuntimeSimulationTickSequence.IsNewer(simulationTick, _lastSubmittedTick))
                return RuntimeMotionSubmitResult.StaleSimulationTick;

            var seen = new HashSet<NetObjectRef>();
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (sample.Object.Store != Store)
                    throw new InvalidOperationException($"Motion sample '{sample.Object}' does not belong to coalescer store '{Store}'.");
                if (!seen.Add(sample.Object))
                    throw new InvalidOperationException($"Motion submission contains duplicate object '{sample.Object}'.");
            }

            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                if (!_entries.TryGetValue(sample.Object, out var entry))
                {
                    entry = new MotionEntry();
                    _entries.Add(sample.Object, entry);
                }

                entry.Sample = sample;
                entry.Pending = true;
                entry.StopFramesRemaining = sample.IsStop
                    ? RuntimeMotionProtocol.STOP_FRAME_REPETITIONS
                    : 0;
            }

            _hasSubmittedTick = true;
            _lastSubmittedTick = simulationTick;
            return RuntimeMotionSubmitResult.Accepted;
        }

        public RuntimeMotionFrameBuildResult TryBuildFrame(
            double nowSeconds,
            uint simulationTick,
            Func<NetObjectRef, bool> isMotionEligible,
            out RuntimeMotionFrame frame)
        {
            return TryBuildFrame(
                nowSeconds,
                simulationTick,
                isMotionEligible,
                RuntimeMotionProtocol.MAX_UNRELIABLE_PAYLOAD_BYTES,
                out frame);
        }

        public RuntimeMotionFrameBuildResult TryBuildFrame(
            double nowSeconds,
            uint simulationTick,
            Func<NetObjectRef, bool> isMotionEligible,
            int maxPayloadBytes,
            out RuntimeMotionFrame frame)
        {
            frame = null;
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds))
                throw new ArgumentOutOfRangeException(nameof(nowSeconds), "Current time must be finite.");
            if (isMotionEligible == null)
                throw new ArgumentNullException(nameof(isMotionEligible));
            var headerSize = RuntimeMotionFrameCodec.CalculateHeaderSize(Store);
            if (maxPayloadBytes <= headerSize
                || maxPayloadBytes > RuntimeMotionProtocol.MAX_UNRELIABLE_PAYLOAD_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxPayloadBytes),
                    $"Motion payload budget must be in {headerSize + 1}..{RuntimeMotionProtocol.MAX_UNRELIABLE_PAYLOAD_BYTES} bytes.");
            }
            if (nowSeconds + TIME_EPSILON < _nextFrameTime)
                return RuntimeMotionFrameBuildResult.NotDue;
            if (_hasBuiltTick && !RuntimeSimulationTickSequence.IsNewer(simulationTick, _lastBuiltTick))
                return RuntimeMotionFrameBuildResult.StaleSimulationTick;
            if (_hasSubmittedTick
                && simulationTick != _lastSubmittedTick
                && !RuntimeSimulationTickSequence.IsNewer(simulationTick, _lastSubmittedTick))
            {
                return RuntimeMotionFrameBuildResult.StaleSimulationTick;
            }

            var keyframeDue = nowSeconds + TIME_EPSILON >= _nextKeyframeTime;
            AdvanceSchedule(nowSeconds, keyframeDue);

            var objects = new List<NetObjectRef>();
            foreach (var pair in _entries)
            {
                if (!isMotionEligible(pair.Key))
                    continue;
                if (keyframeDue)
                    pair.Value.KeyframePending = true;
                if (!pair.Value.Pending
                    && !pair.Value.KeyframePending
                    && pair.Value.StopFramesRemaining <= 0)
                {
                    continue;
                }
                objects.Add(pair.Key);
            }
            objects.Sort((left, right) => left.ObjectId.CompareTo(right.ObjectId));

            if (objects.Count == 0)
                return RuntimeMotionFrameBuildResult.Empty;

            var startIndex = 0;
            if (_hasSpillCursor)
            {
                while (startIndex < objects.Count && objects[startIndex].ObjectId <= _spillCursorObjectId)
                    startIndex++;
                if (startIndex == objects.Count)
                    startIndex = 0;
            }

            var selectedObjects = new List<NetObjectRef>();
            var encodedBytes = headerSize;
            for (var offset = 0; offset < objects.Count; offset++)
            {
                if (selectedObjects.Count >= RuntimeMotionProtocol.MAX_SAMPLES_PER_FRAME)
                    break;

                var objectIndex = (startIndex + offset) % objects.Count;
                var value = objects[objectIndex];
                var sampleSize = RuntimeMotionFrameCodec.CalculateSampleSize(_entries[value].Sample);
                if (encodedBytes + sampleSize > maxPayloadBytes)
                {
                    if (selectedObjects.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"Motion sample '{value}' requires {headerSize + sampleSize} bytes, exceeding payload budget {maxPayloadBytes}.");
                    }
                    break;
                }

                selectedObjects.Add(value);
                encodedBytes += sampleSize;
            }

            var frameIsKeyframe = false;
            var samples = new RuntimeMotionSample[selectedObjects.Count];
            for (var i = 0; i < selectedObjects.Count; i++)
            {
                var entry = _entries[selectedObjects[i]];
                samples[i] = entry.Sample;
                frameIsKeyframe |= entry.KeyframePending;
                entry.Pending = false;
                entry.KeyframePending = false;
                if (entry.StopFramesRemaining > 0)
                    entry.StopFramesRemaining--;
            }
            _spillCursorObjectId = selectedObjects[selectedObjects.Count - 1].ObjectId;
            _hasSpillCursor = true;
            Array.Sort(samples, (left, right) => left.Object.ObjectId.CompareTo(right.Object.ObjectId));

            frame = new RuntimeMotionFrame(Store, simulationTick, frameIsKeyframe, samples);
            _hasBuiltTick = true;
            _lastBuiltTick = simulationTick;
            return RuntimeMotionFrameBuildResult.Built;
        }

        public bool Forget(NetObjectRef value)
        {
            if (!value.IsValid || value.Store != Store)
                throw new ArgumentException($"Motion object '{value}' does not belong to coalescer store '{Store}'.", nameof(value));
            return _entries.Remove(value);
        }

        private void AdvanceSchedule(double nowSeconds, bool keyframe)
        {
            var frameIntervals = Math.Floor(
                (nowSeconds + TIME_EPSILON - _nextFrameTime) / RuntimeMotionProtocol.SEND_INTERVAL_SECONDS) + 1d;
            _nextFrameTime += frameIntervals * RuntimeMotionProtocol.SEND_INTERVAL_SECONDS;

            if (!keyframe)
                return;

            var keyframeIntervals = Math.Floor(
                (nowSeconds + TIME_EPSILON - _nextKeyframeTime) / RuntimeMotionProtocol.KEYFRAME_INTERVAL_SECONDS) + 1d;
            _nextKeyframeTime += keyframeIntervals * RuntimeMotionProtocol.KEYFRAME_INTERVAL_SECONDS;
        }

        private class MotionEntry
        {
            public RuntimeMotionSample Sample;
            public bool Pending;
            public bool KeyframePending;
            public int StopFramesRemaining;
        }
    }
}
