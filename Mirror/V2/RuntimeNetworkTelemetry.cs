using System;
using System.Collections.Generic;
using System.Diagnostics;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Actual encoded wire streams. This is intentionally independent from
    /// RuntimeReplicationDataClass because one reliable store envelope can
    /// contain both structural operations and reliable field state.
    /// </summary>
    public enum RuntimeNetworkStreamKind : byte
    {
        Baseline = 1,
        ReliableStore = 2,
        HotState = 3,
    }

    public readonly struct RuntimeNetworkStreamKey : IEquatable<RuntimeNetworkStreamKey>
    {
        public static readonly RuntimeNetworkStreamKey Baseline = new(RuntimeNetworkStreamKind.Baseline, 0);
        public static readonly RuntimeNetworkStreamKey ReliableStore = new(RuntimeNetworkStreamKind.ReliableStore, 0);

        public readonly RuntimeNetworkStreamKind Kind;
        public readonly uint StreamTypeId;

        public bool IsValid => (Kind == RuntimeNetworkStreamKind.Baseline
                                || Kind == RuntimeNetworkStreamKind.ReliableStore)
                               ? StreamTypeId == 0
                               : Kind == RuntimeNetworkStreamKind.HotState && StreamTypeId != 0;

        public RuntimeNetworkStreamKey(RuntimeNetworkStreamKind kind, uint streamTypeId)
        {
            if (kind != RuntimeNetworkStreamKind.Baseline
                && kind != RuntimeNetworkStreamKind.ReliableStore
                && kind != RuntimeNetworkStreamKind.HotState)
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
            if (kind == RuntimeNetworkStreamKind.HotState && streamTypeId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(streamTypeId), "Hot state stream type id must be non-zero.");
            }
            if (kind != RuntimeNetworkStreamKind.HotState && streamTypeId != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(streamTypeId), "Baseline and reliable-store streams use type id zero.");
            }

            Kind = kind;
            StreamTypeId = streamTypeId;
        }

        public static RuntimeNetworkStreamKey HotState(uint streamTypeId)
        {
            return new RuntimeNetworkStreamKey(RuntimeNetworkStreamKind.HotState, streamTypeId);
        }

        public bool Equals(RuntimeNetworkStreamKey other)
        {
            return Kind == other.Kind && StreamTypeId == other.StreamTypeId;
        }

        public override bool Equals(object obj)
        {
            return obj is RuntimeNetworkStreamKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine((byte)Kind, StreamTypeId);
        }

        public override string ToString()
        {
            return StreamTypeId == 0 ? Kind.ToString() : $"{Kind}:{StreamTypeId}";
        }

        public static bool operator ==(RuntimeNetworkStreamKey left, RuntimeNetworkStreamKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RuntimeNetworkStreamKey left, RuntimeNetworkStreamKey right)
        {
            return !left.Equals(right);
        }
    }

    public readonly struct RuntimeNetworkEncodeMeasurement
    {
        public readonly long ElapsedStopwatchTicks;
        public readonly long AllocatedBytes;

        public RuntimeNetworkEncodeMeasurement(long elapsedStopwatchTicks, long allocatedBytes)
        {
            if (elapsedStopwatchTicks < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(elapsedStopwatchTicks));
            }
            if (allocatedBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(allocatedBytes));
            }

            ElapsedStopwatchTicks = elapsedStopwatchTicks;
            AllocatedBytes = allocatedBytes;
        }
    }

    public readonly struct RuntimeNetworkEncodeMeasure
    {
        private readonly long _startedAt;
        private readonly long _allocatedAtStart;
        private readonly int _threadId;

        private RuntimeNetworkEncodeMeasure(
            long startedAt,
            long allocatedAtStart,
            int threadId)
        {
            _startedAt = startedAt;
            _allocatedAtStart = allocatedAtStart;
            _threadId = threadId;
        }

        public static RuntimeNetworkEncodeMeasure Begin()
        {
            return new RuntimeNetworkEncodeMeasure(
                Stopwatch.GetTimestamp(),
                GC.GetAllocatedBytesForCurrentThread(),
                Environment.CurrentManagedThreadId);
        }

        public RuntimeNetworkEncodeMeasurement Complete()
        {
            if (_startedAt == 0)
            {
                throw new InvalidOperationException("Encode measurement was not started.");
            }
            if (_threadId != Environment.CurrentManagedThreadId)
            {
                throw new InvalidOperationException("Encode allocation measurement must complete on the thread where it started.");
            }

            var elapsed = Stopwatch.GetTimestamp() - _startedAt;
            var allocated = GC.GetAllocatedBytesForCurrentThread() - _allocatedAtStart;
            return new RuntimeNetworkEncodeMeasurement(
                Math.Max(0, elapsed),
                Math.Max(0, allocated));
        }
    }

    public readonly struct RuntimeNetworkStreamMetrics
    {
        public readonly RuntimeNetworkStreamKey Stream;
        public readonly long SentBytes;
        public readonly long ReceivedBytes;
        public readonly long SentMessages;
        public readonly long ReceivedMessages;
        public readonly double SentBytesPerSecond;
        public readonly double ReceivedBytesPerSecond;
        public readonly long EncodeCount;
        public readonly double TotalEncodeMilliseconds;
        public readonly double AverageEncodeMilliseconds;
        public readonly long EncodeAllocatedBytes;

        public RuntimeNetworkStreamMetrics(
            in RuntimeNetworkStreamKey stream,
            long sentBytes,
            long receivedBytes,
            long sentMessages,
            long receivedMessages,
            double sentBytesPerSecond,
            double receivedBytesPerSecond,
            long encodeCount,
            double totalEncodeMilliseconds,
            long encodeAllocatedBytes)
        {
            Stream = stream;
            SentBytes = sentBytes;
            ReceivedBytes = receivedBytes;
            SentMessages = sentMessages;
            ReceivedMessages = receivedMessages;
            SentBytesPerSecond = sentBytesPerSecond;
            ReceivedBytesPerSecond = receivedBytesPerSecond;
            EncodeCount = encodeCount;
            TotalEncodeMilliseconds = totalEncodeMilliseconds;
            AverageEncodeMilliseconds = encodeCount == 0 ? 0 : totalEncodeMilliseconds / encodeCount;
            EncodeAllocatedBytes = encodeAllocatedBytes;
        }
    }

    public readonly struct RuntimeNetworkConnectionMetrics
    {
        public readonly int ConnectionId;
        // Current outgoing projection and last ACKed membership are separate
        // snapshots. During a pending leave either count may be larger.
        public readonly int ProjectedObjects;
        public readonly int AcknowledgedObjects;

        public RuntimeNetworkConnectionMetrics(
            int connectionId,
            int projectedObjects,
            int acknowledgedObjects)
        {
            ConnectionId = connectionId;
            ProjectedObjects = projectedObjects;
            AcknowledgedObjects = acknowledgedObjects;
        }
    }

    public class RuntimeNetworkTelemetrySnapshot
    {
        public readonly double WindowSeconds;
        public readonly IReadOnlyList<RuntimeNetworkStreamMetrics> Streams;
        public readonly IReadOnlyList<RuntimeNetworkConnectionMetrics> Connections;
        public readonly long DirtyTickCount;
        public readonly long DirtyComponentCount;
        public readonly int LastDirtyComponents;
        public readonly int MaxDirtyComponents;
        public readonly double AverageDirtyComponentsPerTick;
        public readonly long BaselineCount;
        public readonly long BaselineBytes;
        public readonly int LastBaselineBytes;
        public readonly int MaxBaselineBytes;
        public readonly long ResyncCount;

        public RuntimeNetworkTelemetrySnapshot(
            double windowSeconds,
            IReadOnlyList<RuntimeNetworkStreamMetrics> streams,
            IReadOnlyList<RuntimeNetworkConnectionMetrics> connections,
            long dirtyTickCount,
            long dirtyComponentCount,
            int lastDirtyComponents,
            int maxDirtyComponents,
            long baselineCount,
            long baselineBytes,
            int lastBaselineBytes,
            int maxBaselineBytes,
            long resyncCount)
        {
            WindowSeconds = windowSeconds;
            Streams = streams ?? throw new ArgumentNullException(nameof(streams));
            Connections = connections ?? throw new ArgumentNullException(nameof(connections));
            DirtyTickCount = dirtyTickCount;
            DirtyComponentCount = dirtyComponentCount;
            LastDirtyComponents = lastDirtyComponents;
            MaxDirtyComponents = maxDirtyComponents;
            AverageDirtyComponentsPerTick = dirtyTickCount == 0 ? 0 : (double)dirtyComponentCount / dirtyTickCount;
            BaselineCount = baselineCount;
            BaselineBytes = baselineBytes;
            LastBaselineBytes = lastBaselineBytes;
            MaxBaselineBytes = maxBaselineBytes;
            ResyncCount = resyncCount;
        }

        public bool TryGetStream(
            in RuntimeNetworkStreamKey stream,
            out RuntimeNetworkStreamMetrics metrics)
        {
            for (var i = 0; i < Streams.Count; i++)
            {
                if (Streams[i].Stream == stream)
                {
                    metrics = Streams[i];
                    return true;
                }
            }

            metrics = default;
            return false;
        }
    }

    public class RuntimeNetworkTelemetryStreamState
    {
        public long SentBytes;
        public long ReceivedBytes;
        public long SentMessages;
        public long ReceivedMessages;
        public long WindowSentBytes;
        public long WindowReceivedBytes;
        public long EncodeCount;
        public long EncodeStopwatchTicks;
        public long EncodeAllocatedBytes;
    }

    public readonly struct RuntimeNetworkTelemetryConnectionStoreKey : IEquatable<RuntimeNetworkTelemetryConnectionStoreKey>
    {
        public readonly int ConnectionId;
        public readonly NetStoreRef Store;

        public RuntimeNetworkTelemetryConnectionStoreKey(int connectionId, in NetStoreRef store)
        {
            ConnectionId = connectionId;
            Store = store;
        }

        public bool Equals(RuntimeNetworkTelemetryConnectionStoreKey other)
        {
            return ConnectionId == other.ConnectionId && Store == other.Store;
        }

        public override bool Equals(object obj)
        {
            return obj is RuntimeNetworkTelemetryConnectionStoreKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ConnectionId, Store);
        }
    }

    public readonly struct RuntimeNetworkTelemetryConnectionStoreState
    {
        public readonly int ProjectedObjects;
        public readonly int AcknowledgedObjects;

        public RuntimeNetworkTelemetryConnectionStoreState(
            int projectedObjects,
            int acknowledgedObjects)
        {
            ProjectedObjects = projectedObjects;
            AcknowledgedObjects = acknowledgedObjects;
        }
    }

    public class RuntimeNetworkTelemetry
    {
        private readonly object _sync = new();
        private readonly Dictionary<RuntimeNetworkStreamKey, RuntimeNetworkTelemetryStreamState> _streams = new();
        private readonly Dictionary<RuntimeNetworkTelemetryConnectionStoreKey, RuntimeNetworkTelemetryConnectionStoreState> _connectionStores = new();
        private long _windowStartedAt = Stopwatch.GetTimestamp();
        private long _dirtyTickCount;
        private long _dirtyComponentCount;
        private int _lastDirtyComponents;
        private int _maxDirtyComponents;
        private long _baselineCount;
        private long _baselineBytes;
        private int _lastBaselineBytes;
        private int _maxBaselineBytes;
        private long _resyncCount;

        public void RecordSent(in RuntimeNetworkStreamKey stream, int payloadBytes)
        {
            RequireValidStream(stream);
            if (payloadBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            }

            lock (_sync)
            {
                var state = GetOrCreateStream(stream);
                state.SentBytes += payloadBytes;
                state.SentMessages++;
                state.WindowSentBytes += payloadBytes;
            }
        }

        public void RecordReceived(in RuntimeNetworkStreamKey stream, int payloadBytes)
        {
            RequireValidStream(stream);
            if (payloadBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            }

            lock (_sync)
            {
                var state = GetOrCreateStream(stream);
                state.ReceivedBytes += payloadBytes;
                state.ReceivedMessages++;
                state.WindowReceivedBytes += payloadBytes;
            }
        }

        public void RecordEncode(
            in RuntimeNetworkStreamKey stream,
            in RuntimeNetworkEncodeMeasurement measurement)
        {
            RequireValidStream(stream);
            lock (_sync)
            {
                var state = GetOrCreateStream(stream);
                state.EncodeCount++;
                state.EncodeStopwatchTicks += measurement.ElapsedStopwatchTicks;
                state.EncodeAllocatedBytes += measurement.AllocatedBytes;
            }
        }

        public void RecordDirtyComponents(int dirtyComponentCount)
        {
            if (dirtyComponentCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(dirtyComponentCount));
            }

            lock (_sync)
            {
                _dirtyTickCount++;
                _dirtyComponentCount += dirtyComponentCount;
                _lastDirtyComponents = dirtyComponentCount;
                _maxDirtyComponents = Math.Max(_maxDirtyComponents, dirtyComponentCount);
            }
        }

        public void RecordCommittedBatch(in RuntimeStoreCommittedBatch batch)
        {
            RecordDirtyComponents(checked(
                batch.ComponentStructureChanges.Length + batch.ComponentChanges.Length));
        }

        public void RecordBaselineSize(int payloadBytes)
        {
            if (payloadBytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadBytes));
            }

            lock (_sync)
            {
                _baselineCount++;
                _baselineBytes += payloadBytes;
                _lastBaselineBytes = payloadBytes;
                _maxBaselineBytes = Math.Max(_maxBaselineBytes, payloadBytes);
            }
        }

        public void RecordResync()
        {
            lock (_sync)
            {
                _resyncCount++;
            }
        }

        public void SetConnectionStoreObjects(
            int connectionId,
            in NetStoreRef store,
            int projectedObjects,
            int acknowledgedObjects)
        {
            if (!store.IsValid)
            {
                throw new ArgumentException("Connection telemetry requires a valid store reference.", nameof(store));
            }
            if (projectedObjects < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(projectedObjects));
            }
            if (acknowledgedObjects < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(acknowledgedObjects));
            }

            lock (_sync)
            {
                var key = new RuntimeNetworkTelemetryConnectionStoreKey(connectionId, store);
                _connectionStores[key] = new RuntimeNetworkTelemetryConnectionStoreState(
                    projectedObjects,
                    acknowledgedObjects);
            }
        }

        public void ObserveConnectionStore(
            int connectionId,
            RuntimeConnectionStoreReplicationState state)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            SetConnectionStoreObjects(
                connectionId,
                state.Store,
                state.ProjectedMembershipCount,
                state.AcknowledgedMembershipCount);
        }

        public void RemoveConnection(int connectionId)
        {
            lock (_sync)
            {
                var remove = new List<RuntimeNetworkTelemetryConnectionStoreKey>();
                foreach (var pair in _connectionStores)
                {
                    if (pair.Key.ConnectionId == connectionId)
                    {
                        remove.Add(pair.Key);
                    }
                }

                for (var i = 0; i < remove.Count; i++)
                {
                    _connectionStores.Remove(remove[i]);
                }
            }
        }

        public RuntimeNetworkTelemetrySnapshot Capture(bool resetWindow = false)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsedTicks = Math.Max(1, now - _windowStartedAt);
            var seconds = (double)elapsedTicks / Stopwatch.Frequency;
            return Capture(seconds, resetWindow, now);
        }

        public RuntimeNetworkTelemetrySnapshot Capture(
            double windowSeconds,
            bool resetWindow = false)
        {
            return Capture(windowSeconds, resetWindow, Stopwatch.GetTimestamp());
        }

        private RuntimeNetworkTelemetrySnapshot Capture(
            double windowSeconds,
            bool resetWindow,
            long capturedAt)
        {
            if (windowSeconds <= 0 || double.IsNaN(windowSeconds) || double.IsInfinity(windowSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(windowSeconds));
            }

            lock (_sync)
            {
                var streams = new RuntimeNetworkStreamMetrics[_streams.Count];
                var streamIndex = 0;
                foreach (var pair in _streams)
                {
                    var state = pair.Value;
                    var totalEncodeMilliseconds = state.EncodeStopwatchTicks * 1000d / Stopwatch.Frequency;
                    streams[streamIndex++] = new RuntimeNetworkStreamMetrics(
                        pair.Key,
                        state.SentBytes,
                        state.ReceivedBytes,
                        state.SentMessages,
                        state.ReceivedMessages,
                        state.WindowSentBytes / windowSeconds,
                        state.WindowReceivedBytes / windowSeconds,
                        state.EncodeCount,
                        totalEncodeMilliseconds,
                        state.EncodeAllocatedBytes);
                }
                Array.Sort(streams, CompareStreams);

                var connections = BuildConnectionMetrics();
                var snapshot = new RuntimeNetworkTelemetrySnapshot(
                    windowSeconds,
                    Array.AsReadOnly(streams),
                    Array.AsReadOnly(connections),
                    _dirtyTickCount,
                    _dirtyComponentCount,
                    _lastDirtyComponents,
                    _maxDirtyComponents,
                    _baselineCount,
                    _baselineBytes,
                    _lastBaselineBytes,
                    _maxBaselineBytes,
                    _resyncCount);
                if (resetWindow)
                {
                    foreach (var pair in _streams)
                    {
                        pair.Value.WindowSentBytes = 0;
                        pair.Value.WindowReceivedBytes = 0;
                    }
                    _windowStartedAt = capturedAt;
                }

                return snapshot;
            }
        }

        private RuntimeNetworkTelemetryStreamState GetOrCreateStream(
            in RuntimeNetworkStreamKey stream)
        {
            if (_streams.TryGetValue(stream, out var state))
            {
                return state;
            }

            state = new RuntimeNetworkTelemetryStreamState();
            _streams.Add(stream, state);
            return state;
        }

        private static void RequireValidStream(in RuntimeNetworkStreamKey stream)
        {
            if (!stream.IsValid)
            {
                throw new ArgumentException("Network telemetry stream key is invalid.", nameof(stream));
            }
        }

        private RuntimeNetworkConnectionMetrics[] BuildConnectionMetrics()
        {
            var totals = new Dictionary<int, RuntimeNetworkTelemetryConnectionStoreState>();
            foreach (var pair in _connectionStores)
            {
                totals.TryGetValue(pair.Key.ConnectionId, out var current);
                totals[pair.Key.ConnectionId] = new RuntimeNetworkTelemetryConnectionStoreState(
                    checked(current.ProjectedObjects + pair.Value.ProjectedObjects),
                    checked(current.AcknowledgedObjects + pair.Value.AcknowledgedObjects));
            }

            var result = new RuntimeNetworkConnectionMetrics[totals.Count];
            var index = 0;
            foreach (var pair in totals)
            {
                result[index++] = new RuntimeNetworkConnectionMetrics(
                    pair.Key,
                    pair.Value.ProjectedObjects,
                    pair.Value.AcknowledgedObjects);
            }
            Array.Sort(result, CompareConnections);
            return result;
        }

        private static int CompareStreams(
            RuntimeNetworkStreamMetrics left,
            RuntimeNetworkStreamMetrics right)
        {
            var kind = left.Stream.Kind.CompareTo(right.Stream.Kind);
            return kind != 0 ? kind : left.Stream.StreamTypeId.CompareTo(right.Stream.StreamTypeId);
        }

        private static int CompareConnections(
            RuntimeNetworkConnectionMetrics left,
            RuntimeNetworkConnectionMetrics right)
        {
            return left.ConnectionId.CompareTo(right.ConnectionId);
        }
    }
}
