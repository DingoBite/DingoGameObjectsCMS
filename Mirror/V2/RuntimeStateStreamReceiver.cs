using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public delegate RuntimeStateStreamFrameAcceptResult RuntimeStateStreamApplyBatch<TSample>(
        NetStoreRef store,
        uint simulationTick,
        double localSimulationTimeSeconds,
        IReadOnlyList<TSample> samples,
        IReadOnlyList<RuntimeStateStreamKey> despawnedKeys);

    internal sealed class RuntimeStateStreamEphemeralStoreState
    {
        public readonly HashSet<RuntimeStateStreamKey> ActiveKeys = new();
        public uint LastAcceptedSimulationTick;
        public double LastAcceptedLocalSimulationTimeSeconds;
    }

    internal sealed class RuntimeStateStreamReconciliationBuffer<TSample>
    {
        public readonly uint ReconciliationId;
        public readonly uint SimulationTick;
        public readonly List<TSample> Samples = new();
        public readonly HashSet<RuntimeStateStreamKey> Keys = new();
        public uint ExpectedSequence;
        public int PackedBytes;
        public int SegmentCount;

        public RuntimeStateStreamReconciliationBuffer(
            uint reconciliationId,
            uint simulationTick,
            uint firstSequence)
        {
            ReconciliationId = reconciliationId;
            SimulationTick = simulationTick;
            ExpectedSequence = RuntimeStateStreamSequence.Next(firstSequence);
            SegmentCount = 1;
        }
    }

    /// <summary>
    /// Typed receiver shared by GRO-backed and ECS-only streams. Ordinary
    /// frames are applied atomically one by one. Segmented reconciliation is
    /// buffered by contiguous sequence and becomes visible only at End.
    /// </summary>
    public class RuntimeStateStreamReceiver<TSample>
    {
        public const double INTERPOLATION_DELAY_SECONDS = RuntimeStateStreamProtocol.INTERPOLATION_DELAY_SECONDS;
        public const double STALE_TIMEOUT_SECONDS = RuntimeStateStreamProtocol.STALE_TIMEOUT_SECONDS;

        private readonly RuntimeStateStreamProfile<TSample> _profile;
        private readonly RuntimeStateStreamApplyBatch<TSample> _apply;
        private readonly Dictionary<NetStoreRef, RuntimeStateStreamFrameSequenceGate> _sequenceByStore = new();
        private readonly Dictionary<NetStoreRef, RuntimeStateStreamEphemeralStoreState> _ephemeralStateByStore = new();
        private readonly Dictionary<NetStoreRef, RuntimeStateStreamReconciliationBuffer<TSample>> _reconciliationByStore = new();

        public RuntimeStateStreamReceiver(
            RuntimeStateStreamProfile<TSample> profile,
            RuntimeStateStreamApplyBatch<TSample> apply)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
        }

        public RuntimeStateStreamFrameAcceptResult Accept(
            RuntimeStateStreamFrame frame,
            double localSimulationTimeSeconds)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (double.IsNaN(localSimulationTimeSeconds) || double.IsInfinity(localSimulationTimeSeconds))
                throw new ArgumentOutOfRangeException(nameof(localSimulationTimeSeconds));
            if (frame.StreamTypeId != _profile.StreamTypeId)
                return RuntimeStateStreamFrameAcceptResult.WrongStream;
            if (!_sequenceByStore.TryGetValue(frame.Store, out var sequence))
            {
                sequence = new RuntimeStateStreamFrameSequenceGate(frame.Store, _profile.StreamTypeId);
                _sequenceByStore.Add(frame.Store, sequence);
            }
            var gateResult = sequence.CanAccept(frame);
            if (gateResult != RuntimeStateStreamFrameAcceptResult.Accepted)
                return gateResult;

            if (!TryDecodeFrame(
                    frame,
                    out var samples,
                    out var liveKeys,
                    out var explicitDespawns,
                    out var packedBytes))
            {
                return RuntimeStateStreamFrameAcceptResult.InvalidSample;
            }

            var isEphemeral = _profile.Lifetime == RuntimeStateStreamLifetime.EphemeralStreamEntity;
            _ephemeralStateByStore.TryGetValue(frame.Store, out var ephemeralState);
            if (frame.IsReconciliation)
            {
                if (explicitDespawns.Count != 0)
                    return RuntimeStateStreamFrameAcceptResult.InvalidSample;
                return AcceptReconciliation(
                    frame,
                    localSimulationTimeSeconds,
                    sequence,
                    samples,
                    liveKeys,
                    packedBytes,
                    isEphemeral,
                    ephemeralState);
            }

            // The sender never interleaves ordinary frames into a cycle. Seeing
            // one means an earlier unreliable fragment was lost, so the partial
            // buffer is disposable and the next Begin will repair it.
            _reconciliationByStore.Remove(frame.Store);
            var despawned = new List<RuntimeStateStreamKey>(explicitDespawns);
            despawned.Sort((first, second) => first.Value.CompareTo(second.Value));
            var applyResult = _apply(
                frame.Store,
                frame.SimulationTick,
                localSimulationTimeSeconds,
                samples.AsReadOnly(),
                despawned.AsReadOnly());
            if (applyResult != RuntimeStateStreamFrameAcceptResult.Accepted)
                return applyResult;

            if (isEphemeral)
            {
                ephemeralState = EnsureEphemeralState(frame.Store, ephemeralState);
                foreach (var liveKey in liveKeys)
                    ephemeralState.ActiveKeys.Add(liveKey);
                foreach (var despawnedKey in explicitDespawns)
                    ephemeralState.ActiveKeys.Remove(despawnedKey);
                TouchEphemeral(ephemeralState, frame.SimulationTick, localSimulationTimeSeconds);
            }
            sequence.Commit(frame);
            return RuntimeStateStreamFrameAcceptResult.Accepted;
        }

        /// <summary>
        /// Applies one atomic despawn batch when an ephemeral stream has stopped
        /// producing frames for the configured stale timeout. Returns true when
        /// the apply callback was invoked. A rejected callback keeps the tracked
        /// keys so the caller can retry expiration.
        /// </summary>
        public bool TryExpireStale(
            NetStoreRef store,
            double localSimulationTimeSeconds,
            out RuntimeStateStreamFrameAcceptResult applyResult)
        {
            if (!store.IsValid)
                throw new ArgumentException("State stream stale expiration requires a valid store reference.", nameof(store));
            if (double.IsNaN(localSimulationTimeSeconds) || double.IsInfinity(localSimulationTimeSeconds))
                throw new ArgumentOutOfRangeException(nameof(localSimulationTimeSeconds));
            if (_profile.Lifetime != RuntimeStateStreamLifetime.EphemeralStreamEntity)
                throw new InvalidOperationException("Stale expiration is only valid for ECS-only ephemeral state streams.");

            applyResult = RuntimeStateStreamFrameAcceptResult.Accepted;
            if (!_ephemeralStateByStore.TryGetValue(store, out var state) || state.ActiveKeys.Count == 0)
                return false;
            if (localSimulationTimeSeconds < state.LastAcceptedLocalSimulationTimeSeconds)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(localSimulationTimeSeconds),
                    "State stream stale expiration time cannot precede the last accepted frame time.");
            }
            if (localSimulationTimeSeconds - state.LastAcceptedLocalSimulationTimeSeconds < STALE_TIMEOUT_SECONDS)
                return false;

            var expired = new List<RuntimeStateStreamKey>(state.ActiveKeys);
            expired.Sort((first, second) => first.Value.CompareTo(second.Value));
            applyResult = _apply(
                store,
                state.LastAcceptedSimulationTick,
                localSimulationTimeSeconds,
                Array.Empty<TSample>(),
                expired.AsReadOnly());
            if (applyResult == RuntimeStateStreamFrameAcceptResult.Accepted)
            {
                state.ActiveKeys.Clear();
                _reconciliationByStore.Remove(store);
            }
            return true;
        }

        public bool Forget(NetStoreRef store)
        {
            var forgotSequence = _sequenceByStore.Remove(store);
            var forgotEphemeralState = _ephemeralStateByStore.Remove(store);
            var forgotReconciliation = _reconciliationByStore.Remove(store);
            return forgotSequence || forgotEphemeralState || forgotReconciliation;
        }

        public void Clear()
        {
            _sequenceByStore.Clear();
            _ephemeralStateByStore.Clear();
            _reconciliationByStore.Clear();
        }

        private RuntimeStateStreamFrameAcceptResult AcceptReconciliation(
            RuntimeStateStreamFrame frame,
            double localSimulationTimeSeconds,
            RuntimeStateStreamFrameSequenceGate sequence,
            List<TSample> fragmentSamples,
            HashSet<RuntimeStateStreamKey> fragmentKeys,
            int fragmentPackedBytes,
            bool isEphemeral,
            RuntimeStateStreamEphemeralStoreState ephemeralState)
        {
            RuntimeStateStreamReconciliationBuffer<TSample> buffer;
            if (frame.StartsReconciliation)
            {
                if (_reconciliationByStore.TryGetValue(frame.Store, out var active)
                    && active.ReconciliationId == frame.ReconciliationId)
                {
                    // A reconciliation id identifies exactly one Begin..End
                    // cycle. A second Begin with the same id cannot replace
                    // the buffer: otherwise a suffix could be accepted as a
                    // complete set after silently dropping the first prefix.
                    _reconciliationByStore.Remove(frame.Store);
                    sequence.Commit(frame);
                    return RuntimeStateStreamFrameAcceptResult.Accepted;
                }
                buffer = new RuntimeStateStreamReconciliationBuffer<TSample>(
                    frame.ReconciliationId,
                    frame.SimulationTick,
                    frame.Sequence);
                _reconciliationByStore[frame.Store] = buffer;
            }
            else if (!_reconciliationByStore.TryGetValue(frame.Store, out buffer)
                     || buffer.ReconciliationId != frame.ReconciliationId
                     || buffer.SimulationTick != frame.SimulationTick
                     || frame.Sequence != buffer.ExpectedSequence)
            {
                // A fragment was lost or belongs to an obsolete cycle. Commit
                // its delivery position, discard all partial data, and wait for
                // the next Begin; no application callback observes a prefix.
                // Malformed/obsolete fragments do not refresh ephemeral
                // liveness, otherwise garbage traffic can keep ghosts alive.
                _reconciliationByStore.Remove(frame.Store);
                sequence.Commit(frame);
                return RuntimeStateStreamFrameAcceptResult.Accepted;
            }

            if (!frame.StartsReconciliation)
            {
                if (buffer.SegmentCount >= RuntimeStateStreamProtocol.MAX_RECONCILIATION_SEGMENTS)
                {
                    // Sender and receiver share the same hard latency bound.
                    // A forged contiguous suffix cannot extend liveness or
                    // retain a partial snapshot past that bound.
                    _reconciliationByStore.Remove(frame.Store);
                    return RuntimeStateStreamFrameAcceptResult.InvalidSample;
                }
                buffer.SegmentCount++;
            }

            if (!CanAppend(buffer, fragmentKeys, fragmentSamples.Count, fragmentPackedBytes))
            {
                _reconciliationByStore.Remove(frame.Store);
                return RuntimeStateStreamFrameAcceptResult.InvalidSample;
            }

            if (!frame.CompletesReconciliation)
            {
                Append(buffer, fragmentSamples, fragmentKeys, fragmentPackedBytes);
                buffer.ExpectedSequence = RuntimeStateStreamSequence.Next(frame.Sequence);
                sequence.Commit(frame);
                if (isEphemeral)
                {
                    ephemeralState = EnsureEphemeralState(frame.Store, ephemeralState);
                    TouchEphemeral(ephemeralState, frame.SimulationTick, localSimulationTimeSeconds);
                }
                return RuntimeStateStreamFrameAcceptResult.Accepted;
            }

            var completeSamples = new List<TSample>(buffer.Samples.Count + fragmentSamples.Count);
            completeSamples.AddRange(buffer.Samples);
            completeSamples.AddRange(fragmentSamples);
            var completeKeys = new HashSet<RuntimeStateStreamKey>(buffer.Keys);
            completeKeys.UnionWith(fragmentKeys);
            var reconciledDespawns = new List<RuntimeStateStreamKey>();
            if (isEphemeral && ephemeralState != null)
            {
                foreach (var trackedKey in ephemeralState.ActiveKeys)
                {
                    if (!completeKeys.Contains(trackedKey))
                        reconciledDespawns.Add(trackedKey);
                }
            }
            reconciledDespawns.Sort((first, second) => first.Value.CompareTo(second.Value));

            var applyResult = _apply(
                frame.Store,
                frame.SimulationTick,
                localSimulationTimeSeconds,
                completeSamples.AsReadOnly(),
                reconciledDespawns.AsReadOnly());
            if (applyResult != RuntimeStateStreamFrameAcceptResult.Accepted)
            {
                // A one-frame reconciliation has no committed prefix to keep.
                // Drop its provisional buffer so the exact uncommitted frame
                // can be retried. Multipart End retries retain their prefix.
                if (frame.StartsReconciliation)
                    _reconciliationByStore.Remove(frame.Store);
                return applyResult;
            }

            _reconciliationByStore.Remove(frame.Store);
            if (isEphemeral)
            {
                ephemeralState = EnsureEphemeralState(frame.Store, ephemeralState);
                ephemeralState.ActiveKeys.Clear();
                foreach (var key in completeKeys)
                    ephemeralState.ActiveKeys.Add(key);
                TouchEphemeral(ephemeralState, frame.SimulationTick, localSimulationTimeSeconds);
            }
            sequence.Commit(frame);
            return RuntimeStateStreamFrameAcceptResult.Accepted;
        }

        private bool TryDecodeFrame(
            RuntimeStateStreamFrame frame,
            out List<TSample> samples,
            out HashSet<RuntimeStateStreamKey> liveKeys,
            out HashSet<RuntimeStateStreamKey> despawnedKeys,
            out int packedBytes)
        {
            samples = new List<TSample>(frame.Samples.Count);
            liveKeys = new HashSet<RuntimeStateStreamKey>();
            despawnedKeys = new HashSet<RuntimeStateStreamKey>();
            packedBytes = 0;
            var frameKeys = new HashSet<RuntimeStateStreamKey>();
            try
            {
                for (var i = 0; i < frame.Samples.Count; i++)
                {
                    var sample = frame.Samples[i];
                    if (!frameKeys.Add(sample.Key))
                        throw new FormatException($"State stream frame contains duplicate key '{sample.Key}'.");
                    packedBytes = checked(packedBytes + RuntimeStateStreamFrameCodec.CalculateSampleSize(sample));
                    if (sample.IsDespawn)
                    {
                        _profile.ValidatePackedSample(sample);
                        despawnedKeys.Add(sample.Key);
                    }
                    else
                    {
                        samples.Add(_profile.UnpackSample(sample));
                        liveKeys.Add(sample.Key);
                    }
                }
                return true;
            }
            catch (Exception exception) when (exception is FormatException
                                              || exception is ArgumentException
                                              || exception is InvalidOperationException
                                              || exception is OverflowException)
            {
                _ = exception;
                return false;
            }
        }

        private static bool CanAppend(
            RuntimeStateStreamReconciliationBuffer<TSample> buffer,
            HashSet<RuntimeStateStreamKey> fragmentKeys,
            int sampleCount,
            int packedBytes)
        {
            if (buffer.Samples.Count + sampleCount > RuntimeStateStreamProtocol.MAX_RECONCILIATION_SAMPLES
                || buffer.PackedBytes + packedBytes > RuntimeStateStreamProtocol.MAX_RECONCILIATION_PACKED_BYTES)
            {
                return false;
            }
            foreach (var key in fragmentKeys)
            {
                if (buffer.Keys.Contains(key))
                    return false;
            }
            return true;
        }

        private static void Append(
            RuntimeStateStreamReconciliationBuffer<TSample> buffer,
            List<TSample> samples,
            HashSet<RuntimeStateStreamKey> keys,
            int packedBytes)
        {
            buffer.Samples.AddRange(samples);
            buffer.Keys.UnionWith(keys);
            buffer.PackedBytes += packedBytes;
        }

        private RuntimeStateStreamEphemeralStoreState EnsureEphemeralState(
            NetStoreRef store,
            RuntimeStateStreamEphemeralStoreState state)
        {
            if (state != null)
                return state;
            state = new RuntimeStateStreamEphemeralStoreState();
            _ephemeralStateByStore.Add(store, state);
            return state;
        }

        private static void TouchEphemeral(
            RuntimeStateStreamEphemeralStoreState state,
            uint simulationTick,
            double localSimulationTimeSeconds)
        {
            state.LastAcceptedSimulationTick = simulationTick;
            state.LastAcceptedLocalSimulationTimeSeconds = Math.Max(
                state.LastAcceptedLocalSimulationTimeSeconds,
                localSimulationTimeSeconds);
        }
    }
}
