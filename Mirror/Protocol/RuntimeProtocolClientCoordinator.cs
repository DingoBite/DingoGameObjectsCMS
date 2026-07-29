using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;

namespace DingoGameObjectsCMS.Mirror.Protocol
{
    public class RuntimeProtocolClientOutput
    {
        public readonly Action<RuntimeSessionDescriptor, ulong> Hello;
        public readonly Action<ulong> Ready;
        public readonly Action<RuntimeProtocolRejectCode, string> Reject;
        public readonly Action<RtStoreAckData> Ack;
        public readonly Action<RtStoreResyncData> Resync;
        public readonly Action<RuntimeCommandEnvelope> Command;
        public readonly Action<RtCommandJournalResyncData> JournalResync;

        public RuntimeProtocolClientOutput(
            Action<RuntimeSessionDescriptor, ulong> hello,
            Action<ulong> ready,
            Action<RuntimeProtocolRejectCode, string> reject,
            Action<RtStoreAckData> ack,
            Action<RtStoreResyncData> resync,
            Action<RuntimeCommandEnvelope> command,
            Action<RtCommandJournalResyncData> journalResync = null)
        {
            Hello = hello ?? throw new ArgumentNullException(nameof(hello));
            Ready = ready ?? throw new ArgumentNullException(nameof(ready));
            Reject = reject ?? throw new ArgumentNullException(nameof(reject));
            Ack = ack ?? throw new ArgumentNullException(nameof(ack));
            Resync = resync ?? throw new ArgumentNullException(nameof(resync));
            Command = command ?? throw new ArgumentNullException(nameof(command));
            JournalResync = journalResync;
        }
    }

    public class RuntimeProtocolClientCoordinator : IDisposable
    {
        private readonly RuntimeProtocolContext _context;
        private readonly RuntimeProtocolClientOutput _output;
        private readonly RuntimeSessionClientHandshake _handshake;
        private readonly RuntimeReplicaStagingRealms _stagingRealms;
        private readonly RuntimeReplicaBaselineSpawnFactory _spawnFactory;
        private readonly RuntimeReplicaBaselineStager _stager;
        private readonly RuntimeReplicaBaselineApplier _baselineApplier = new();
        private readonly RuntimeReplicaDeltaTransaction _deltaTransaction;
        private readonly RuntimeStateStreamFrameCodec _stateStreamCodec = new();
        private readonly RuntimeNetworkTelemetry _telemetry;
        private readonly Dictionary<NetStoreRef, InitialStoreState> _initialStores = new();
        private readonly Dictionary<NetStoreRef, RuntimeClientLogicalEnvelopeReceiver> _receivers = new();
        private readonly List<RuntimeCommandJournalBatch> _pendingJournalBatches = new();
        private ulong _nextCommandSequence;
        private RuntimeCheckpointBoundary? _initialCheckpointBoundary;
        private RuntimeCommandJournalCatchupReceiver _journalReceiver;
        private int _pendingJournalEntryCount;
        private int _pendingJournalPayloadBytes;
        private bool _initialCheckpointBoundaryObserved;
        private bool _baselineGroupPublished;
        private bool _journalCatchupObserved;
        private bool _journalFullBaselineRequested;
        private bool _helloSent;
        private bool _initialPublicationComplete;
        private bool _disposed;

        public event Action<RuntimeCommandResult> CommandResultReceived;
        public event Action<RuntimeStateStreamFrame> StateStreamFrameReceived;
        public event Action<bool> ReplicaReadyChanged;

        public bool IsReplicaReady => _initialPublicationComplete;
        public ulong SessionId => _handshake.Manifest?.SessionId ?? 0;
        public RuntimeNetworkTelemetry Telemetry => _telemetry;

        public RuntimeProtocolClientCoordinator(
            RuntimeProtocolContext context,
            RuntimeProtocolClientOutput output,
            ulong clientNonce,
            RuntimeNetworkTelemetry telemetry = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _telemetry = telemetry ?? new RuntimeNetworkTelemetry();
            _handshake = new RuntimeSessionClientHandshake(clientNonce, context.ClientExpectation);
            _stagingRealms = new RuntimeReplicaStagingRealms();
            _spawnFactory = new RuntimeReplicaBaselineSpawnFactory(
                context.AssetCatalog,
                context.AssetLock,
                context.TemplateCache,
                context.ReplicationPolicies);
            var baselineCodec = new RuntimeStoreBaselineCodec(context.PatchCodecs);
            _stager = new RuntimeReplicaBaselineStager(
                context.World,
                baselineCodec,
                _spawnFactory,
                _stagingRealms);
            _deltaTransaction = new RuntimeReplicaDeltaTransaction(
                context,
                _stager,
                _spawnFactory,
                _stagingRealms);
            if (context.JournalSubscriptionScope != null)
            {
                if (output.JournalResync == null)
                {
                    throw new InvalidOperationException(
                        "Checkpoint journal transport requires a client journal resync output.");
                }
                if (context.CompleteJournalCatchup == null)
                {
                    throw new InvalidOperationException(
                        "Checkpoint journal transport requires an explicit ECS playback/completion hook.");
                }
            }
            if (context.CommandsBus != null)
                context.CommandsBus.SetOutboundDispatcher(DispatchOutboundCommand);
        }

        public RuntimeNetworkTelemetrySnapshot CaptureTelemetry(bool resetWindow = false)
        {
            return _telemetry.Capture(resetWindow);
        }

        public RuntimeNetworkTelemetrySnapshot CaptureTelemetry(
            double windowSeconds,
            bool resetWindow = false)
        {
            return _telemetry.Capture(windowSeconds, resetWindow);
        }

        public void BeginHandshake()
        {
            ThrowIfDisposed();
            if (_helloSent)
                throw new InvalidOperationException("Protocol client hello has already been sent.");
            var descriptor = _handshake.BeginHello();
            _helloSent = true;
            _output.Hello(descriptor, _handshake.ClientNonce);
        }

        public RuntimeSessionHandshakeResult ReceiveManifest(
            ulong sessionId,
            in RuntimeSessionDescriptor descriptor,
            IReadOnlyList<RuntimeAssetCatalogEntry> assets,
            IReadOnlyList<RuntimeStoreCatalogEntry> stores)
        {
            ThrowIfDisposed();
            var result = _handshake.ReceiveManifest(sessionId, descriptor, assets, stores);
            if (!result.Accepted)
            {
                _output.Reject(result.RejectCode, result.Detail);
                return result;
            }

            _initialStores.Clear();
            _pendingJournalBatches.Clear();
            _pendingJournalEntryCount = 0;
            _pendingJournalPayloadBytes = 0;
            _journalReceiver = null;
            _baselineGroupPublished = false;
            _journalCatchupObserved = false;
            _journalFullBaselineRequested = false;
            _initialCheckpointBoundary = null;
            _initialCheckpointBoundaryObserved = false;
            for (var i = 0; i < _handshake.Manifest.Stores.Count; i++)
            {
                var entry = _handshake.Manifest.Stores[i];
                var store = new NetStoreRef(entry.StoreId, entry.StoreGeneration);
                _initialStores.Add(store, new InitialStoreState(store));
            }

            _output.Ready(sessionId);
            return result;
        }

        public RuntimeClientReceiveResult ReceiveBaselineChunk(
            in RuntimeBaselineChunk chunk,
            double nowSeconds)
        {
            ThrowIfDisposed();
            if (_baselineGroupPublished
                && _journalFullBaselineRequested)
            {
                RestartInitialCheckpointGroup();
            }
            var authorization = _handshake.AuthorizeStoreAccess(chunk.SessionId, chunk.Store);
            if (!authorization.Accepted)
                return Rejected(authorization.RejectCode);
            _telemetry.RecordReceived(
                RuntimeNetworkStreamKey.Baseline,
                chunk.Payload?.Length ?? 0);

            if (_baselineGroupPublished)
            {
                if (!_receivers.TryGetValue(chunk.Store, out var receiver))
                    return Rejected(RuntimeProtocolRejectCode.InvalidStore);
                var result = receiver.ReceiveBaselineChunk(chunk, nowSeconds);
                ProcessReceiveResult(receiver, result);
                return result;
            }

            if (!_initialStores.TryGetValue(chunk.Store, out var initial))
                return Rejected(RuntimeProtocolRejectCode.InvalidStore);
            if (!AcceptInitialCheckpointBoundary(chunk))
                return RequestInitialCheckpointGroupResync();
            if (initial.ResyncRequested)
            {
                if (chunk.BaselineId < initial.LastBaselineId)
                    return Accepted(RuntimeClientReceiveResultKind.WaitingForBaseline);
                initial.ResetForReplacement();
            }
            initial.LastBaselineId = chunk.BaselineId;
            if (!initial.HasStarted)
            {
                initial.HasStarted = true;
                initial.StartedAt = nowSeconds;
            }

            var chunkResult = initial.Assembler.Accept(chunk, nowSeconds, out var payload);
            switch (chunkResult)
            {
                case RuntimeBaselineChunkResult.Accepted:
                case RuntimeBaselineChunkResult.Duplicate:
                    return Accepted(RuntimeClientReceiveResultKind.Accepted);
                case RuntimeBaselineChunkResult.DuplicateCompleted:
                    return Accepted(RuntimeClientReceiveResultKind.Duplicate);
                case RuntimeBaselineChunkResult.Completed:
                    _telemetry.RecordBaselineSize(payload.Length);
                    initial.Envelope = new RuntimeClientBaselineEnvelope(
                        chunk.SessionId,
                        chunk.Store,
                        chunk.BaselineId,
                        chunk.DeliverySequence,
                        chunk.StoreRevision,
                        payload,
                        TakeCheckpointBoundary(chunk));
                    initial.IsComplete = true;
                    return TryPublishInitialGroup(nowSeconds);
                case RuntimeBaselineChunkResult.TimedOut:
                    RequestInitialResync(initial);
                    return Accepted(RuntimeClientReceiveResultKind.ResyncRequested);
                case RuntimeBaselineChunkResult.Invalid:
                case RuntimeBaselineChunkResult.ConflictingTransfer:
                case RuntimeBaselineChunkResult.Corrupt:
                    RequestInitialResync(initial);
                    return Accepted(RuntimeClientReceiveResultKind.ResyncRequested);
                default:
                    throw new ArgumentOutOfRangeException(nameof(chunkResult), chunkResult, null);
            }
        }

        public RuntimeClientReceiveResult ReceiveDelta(
            RuntimeClientDeltaEnvelope delta,
            double nowSeconds)
        {
            ThrowIfDisposed();
            if (delta == null)
                throw new ArgumentNullException(nameof(delta));
            var authorization = _handshake.AuthorizeStoreAccess(delta.SessionId, delta.Store);
            if (!authorization.Accepted)
                return Rejected(authorization.RejectCode);
            _telemetry.RecordReceived(
                RuntimeNetworkStreamKey.ReliableStore,
                delta.PayloadBytes);

            if (!_baselineGroupPublished)
            {
                if (!_initialStores.TryGetValue(delta.Store, out var initial))
                    return Rejected(RuntimeProtocolRejectCode.InvalidStore);
                if (initial.PendingDeltas.Count + 1 > RuntimeProtocol.MAX_PENDING_ENVELOPES
                    || initial.PendingDeltaBytes + delta.PayloadBytes > RuntimeProtocol.MAX_PENDING_ENVELOPE_BYTES)
                {
                    RequestInitialResync(initial);
                    return Accepted(RuntimeClientReceiveResultKind.ResyncRequested);
                }

                initial.PendingDeltas.Add(delta);
                initial.PendingDeltaBytes += delta.PayloadBytes;
                return Accepted(RuntimeClientReceiveResultKind.Buffered);
            }

            if (!_receivers.TryGetValue(delta.Store, out var receiver))
                return Rejected(RuntimeProtocolRejectCode.InvalidStore);
            var result = receiver.ReceiveDelta(delta, nowSeconds);
            ProcessReceiveResult(receiver, result);
            return result;
        }

        public void ReceiveCommandResult(in RuntimeCommandResult result)
        {
            ThrowIfDisposed();
            CommandResultReceived?.Invoke(result);
        }

        public RuntimeCommandJournalReceiveResult ReceiveJournalBatch(
            RuntimeCommandJournalBatch batch)
        {
            ThrowIfDisposed();
            if (batch == null)
            {
                return RequestJournalResync(
                    new RuntimeCommandJournalReceiveResult(
                        RuntimeCommandJournalReceiveStatus.InvalidBatch,
                        _journalReceiver?.Cursor ?? 0,
                        0));
            }
            if (_handshake.Manifest == null
                || batch.SessionId != _handshake.Manifest.SessionId)
            {
                return RequestJournalResync(
                    new RuntimeCommandJournalReceiveResult(
                        RuntimeCommandJournalReceiveStatus.CheckpointMismatch,
                        _journalReceiver?.Cursor ?? 0,
                        0));
            }

            if (!_baselineGroupPublished)
            {
                var batchBytes = TakeJournalPayloadBytes(batch);
                if (_pendingJournalBatches.Count + 1
                        > RuntimeProtocol.MAX_PENDING_ENVELOPES
                    || _pendingJournalEntryCount + batch.Entries.Count
                        > RuntimeProtocol.MAX_PENDING_JOURNAL_ENTRIES
                    || _pendingJournalPayloadBytes + batchBytes
                        > RuntimeProtocol.MAX_PENDING_JOURNAL_BYTES)
                {
                    return RequestJournalResync(
                        new RuntimeCommandJournalReceiveResult(
                            RuntimeCommandJournalReceiveStatus.InvalidBatch,
                            _initialCheckpointBoundary?.JournalCursor ?? 0,
                            0));
                }

                _pendingJournalBatches.Add(batch);
                _pendingJournalEntryCount += batch.Entries.Count;
                _pendingJournalPayloadBytes += batchBytes;
                return new RuntimeCommandJournalReceiveResult(
                    RuntimeCommandJournalReceiveStatus.Buffered,
                    _initialCheckpointBoundary?.JournalCursor ?? 0,
                    0);
            }

            return ApplyJournalBatch(batch);
        }

        public RuntimeSessionHandshakeResult ReceiveReject(RuntimeProtocolRejectCode code, string detail)
        {
            ThrowIfDisposed();
            var result = _handshake.ReceiveReject(code, detail);
            SetReplicaReady(false);
            return result;
        }

        public RuntimeSessionHandshakeResult ReceiveStateStream(in RtStateStreamFrameData value)
        {
            ThrowIfDisposed();
            var authorization = _handshake.AuthorizeStoreAccess(value.SessionId, value.Store);
            if (!authorization.Accepted)
                return authorization;
            if (!_initialPublicationComplete)
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.SessionNotReady, "Replica journal catch-up is not complete.");
            if (value.Payload == null || value.Payload.Length == 0 || value.Payload.Length > RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES)
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidEnvelope, "State stream payload size is invalid.");
            if (!_context.StateStreamProfiles.TryGet(value.StreamTypeId, out var profile))
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidEnvelope, $"State stream type id {value.StreamTypeId} is not registered.");

            RuntimeStateStreamFrame frame;
            try
            {
                frame = _stateStreamCodec.Decode(value.Payload);
                if (frame.Store != value.Store
                    || frame.StreamTypeId != value.StreamTypeId
                    || frame.Sequence != value.Sequence
                    || frame.SimulationTick != value.SimulationTick)
                {
                    throw new FormatException("State stream envelope header does not match its packed payload.");
                }
                for (var i = 0; i < frame.Samples.Count; i++)
                    profile.ValidatePackedSample(frame.Samples[i]);
            }
            catch (Exception exception) when (exception is FormatException
                                              || exception is ArgumentException
                                              || exception is InvalidOperationException
                                              || exception is OverflowException)
            {
                return RuntimeSessionHandshakeResult.Reject(
                    RuntimeProtocolRejectCode.InvalidEnvelope,
                    exception.Message);
            }
            _telemetry.RecordReceived(
                RuntimeNetworkStreamKey.HotState(value.StreamTypeId),
                value.Payload.Length);
            StateStreamFrameReceived?.Invoke(frame);
            return RuntimeSessionHandshakeResult.Success();
        }

        public void Tick(double nowSeconds)
        {
            ThrowIfDisposed();
            if (!_baselineGroupPublished)
            {
                foreach (var pair in _initialStores)
                {
                    var initial = pair.Value;
                    if (initial.HasStarted
                        && !initial.IsComplete
                        && !initial.ResyncRequested
                        && nowSeconds - initial.StartedAt > RuntimeProtocol.BASELINE_TIMEOUT_SECONDS)
                    {
                        RequestInitialResync(initial);
                    }
                }
                return;
            }

            foreach (var pair in _receivers)
            {
                var result = pair.Value.Tick(nowSeconds);
                ProcessReceiveResult(pair.Value, result);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_context.CommandsBus != null)
                _context.CommandsBus.ClearOutboundDispatcher();
            foreach (var pair in _initialStores)
            {
                var stage = pair.Value.Stage;
                if (stage != null
                    && stage.State != RuntimeReplicaBaselineStageState.Published
                    && stage.State != RuntimeReplicaBaselineStageState.Aborted
                    && stage.State != RuntimeReplicaBaselineStageState.Failed)
                {
                    stage.Abort();
                }
            }
            _initialStores.Clear();
            _receivers.Clear();
            _pendingJournalBatches.Clear();
            _journalReceiver = null;
            _pendingJournalEntryCount = 0;
            _pendingJournalPayloadBytes = 0;
            _baselineGroupPublished = false;
            SetReplicaReady(false);
        }

        private RuntimeClientReceiveResult TryPublishInitialGroup(double nowSeconds)
        {
            foreach (var pair in _initialStores)
            {
                if (!pair.Value.IsComplete)
                    return Accepted(RuntimeClientReceiveResultKind.Buffered);
            }

            var stages = new List<RuntimeReplicaBaselineStage>(_initialStores.Count);
            try
            {
                foreach (var pair in _initialStores)
                {
                    pair.Value.Stage = _stager.Prepare(pair.Value.Envelope.Payload);
                    ValidatePreparedStage(pair.Value.Stage, pair.Value.Envelope);
                    stages.Add(pair.Value.Stage);
                }
                for (var i = 0; i < stages.Count; i++)
                {
                    stages[i].Build();
                }

                // Catch up every completed initial baseline while all required
                // stores are still prepared and invisible. A later invalid
                // delta can therefore abort the whole generation without a
                // view, ECS projection or registry lifecycle ever observing a
                // prefix of the initial state.
                foreach (var pair in _initialStores)
                {
                    var initial = pair.Value;
                    initial.ApplyToStaging = true;
                    initial.Receiver = CreateInitialReceiver(initial);
                    var envelope = initial.Envelope;
                    var chunks = RuntimeBaselineChunker.Split(
                        envelope.SessionId,
                        envelope.Store,
                        envelope.BaselineId,
                        envelope.DeliverySequence,
                        envelope.StoreRevision,
                        envelope.Payload,
                        envelope.CheckpointBoundary);
                    RuntimeClientReceiveResult result = default;
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        result = initial.Receiver.ReceiveBaselineChunk(chunks[i], nowSeconds);
                        if (IsInitialCatchupFailure(result))
                            throw new InvalidOperationException($"Prepared initial baseline '{initial.Store}' was rejected: {result.Kind}.");
                    }

                    initial.PendingDeltas.Sort((left, right) => left.DeliverySequence.CompareTo(right.DeliverySequence));
                    for (var i = 0; i < initial.PendingDeltas.Count; i++)
                    {
                        result = initial.Receiver.ReceiveDelta(initial.PendingDeltas[i], nowSeconds);
                        if (IsInitialCatchupFailure(result))
                        {
                            throw new InvalidOperationException(
                                $"Prepared initial catch-up for '{initial.Store}' failed at delivery " +
                                $"{initial.PendingDeltas[i].DeliverySequence}: {result.Kind}. " +
                                _deltaTransaction.LastFailure?.Message);
                        }
                    }

                    if (initial.Receiver.AppliedBaselineId == 0)
                        throw new InvalidOperationException($"Prepared initial receiver for '{initial.Store}' did not apply its baseline.");
                }

                _baselineApplier.PublishGroup(stages, StoreNetDir.S2C);
            }
            catch
            {
                for (var i = 0; i < stages.Count; i++)
                {
                    if (stages[i].State != RuntimeReplicaBaselineStageState.Published
                        && stages[i].State != RuntimeReplicaBaselineStageState.Aborted
                        && stages[i].State != RuntimeReplicaBaselineStageState.Failed)
                    {
                        stages[i].Abort();
                    }
                }
                foreach (var pair in _initialStores)
                {
                    pair.Value.Receiver = null;
                    pair.Value.ApplyToStaging = false;
                    RequestInitialResync(pair.Value, forceNewBaseline: true);
                }
                return Accepted(RuntimeClientReceiveResultKind.ResyncRequested);
            }

            foreach (var pair in _initialStores)
            {
                var initial = pair.Value;
                initial.ApplyToStaging = false;
                _receivers.Add(pair.Key, initial.Receiver);
                initial.PendingDeltas.Clear();
                initial.PendingDeltaBytes = 0;
            }

            _baselineGroupPublished = true;
            foreach (var receiver in _receivers.Values)
                SendAppliedAck(receiver);
            if (_initialCheckpointBoundary.HasValue)
            {
                _journalReceiver = new RuntimeCommandJournalCatchupReceiver(
                    _handshake.Manifest.SessionId,
                    _initialCheckpointBoundary.Value,
                    _context.JournalSubscriptionScope,
                    _context.CommandsBus,
                    () => _context.CompleteJournalCatchup(
                        _context.World));
                _pendingJournalBatches.Sort(
                    (left, right) =>
                        left.FromCursor.CompareTo(right.FromCursor));
                for (var i = 0; i < _pendingJournalBatches.Count; i++)
                {
                    var journalResult =
                        ApplyJournalBatch(_pendingJournalBatches[i]);
                    if (!journalResult.Succeeded)
                    {
                        _pendingJournalBatches.Clear();
                        _pendingJournalEntryCount = 0;
                        _pendingJournalPayloadBytes = 0;
                        return Accepted(
                            RuntimeClientReceiveResultKind.ResyncRequested);
                    }
                }
                _pendingJournalBatches.Clear();
                _pendingJournalEntryCount = 0;
                _pendingJournalPayloadBytes = 0;
                if (!_journalCatchupObserved)
                {
                    return Accepted(
                        RuntimeClientReceiveResultKind.Buffered);
                }
            }
            else
            {
                SetReplicaReady(true);
            }
            return Accepted(RuntimeClientReceiveResultKind.BaselineApplied);
        }

        private RuntimeClientLogicalEnvelopeReceiver CreateInitialReceiver(InitialStoreState initial)
        {
            return new RuntimeClientLogicalEnvelopeReceiver(
                _handshake.Manifest.SessionId,
                initial.Store,
                _handshake.AuthorizeStoreAccess,
                (in RuntimeClientBaselineEnvelope baseline) => initial.ApplyToStaging
                    ? IsPreparedInitialBaseline(initial, baseline)
                    : ApplyBaseline(baseline),
                (in RuntimeClientDeltaEnvelope delta) => initial.ApplyToStaging
                    ? _deltaTransaction.TryApplyPrepared(initial.Stage.Store, delta)
                    : _deltaTransaction.TryApply(delta),
                request =>
                {
                    if (initial.ApplyToStaging || !_baselineGroupPublished)
                        return;

                    SendResync(new RtStoreResyncData(
                        request.SessionId,
                        request.Store,
                        request.BaselineId,
                        request.ExpectedDeliverySequence));
                });
        }

        private bool ApplyBaseline(in RuntimeClientBaselineEnvelope baseline)
        {
            RuntimeReplicaBaselineStage stage = null;
            try
            {
                stage = _stager.Prepare(baseline.Payload);
                ValidatePreparedStage(stage, baseline);
                _baselineApplier.Apply(stage, StoreNetDir.S2C);
                return true;
            }
            catch
            {
                if (stage != null
                    && stage.State != RuntimeReplicaBaselineStageState.Published
                    && stage.State != RuntimeReplicaBaselineStageState.Aborted
                    && stage.State != RuntimeReplicaBaselineStageState.Failed)
                {
                    stage.Abort();
                }
                return false;
            }
        }

        private void DispatchOutboundCommand(
            GameRuntimeCommand command,
            in RuntimeExecutionState authority)
        {
            if (!_initialPublicationComplete || _handshake.State != RuntimeSessionHandshakeState.Ready)
                throw new InvalidOperationException("Remote command cannot be sent before protocol replica readiness.");
            if (_context.CommandEncoder == null)
                throw new InvalidOperationException("Protocol client has no typed command encoder.");
            if (_nextCommandSequence == ulong.MaxValue)
                throw new InvalidOperationException("Protocol client command sequence is exhausted.");

            var sequence = _nextCommandSequence + 1;
            if (!_context.CommandEncoder(command, in authority, sequence, out var envelope))
                throw new InvalidOperationException($"No protocol typed command mapping exists for '{command.GetType().FullName}'.");
            if (envelope.ClientSequence != sequence
                || envelope.CommandTypeId == 0
                || envelope.ExpectedStoreGeneration == 0)
            {
                throw new InvalidOperationException("Typed command encoder returned an invalid protocol envelope.");
            }

            var generationAllowed = false;
            for (var i = 0; i < _handshake.Manifest.Stores.Count; i++)
            {
                if (_handshake.Manifest.Stores[i].StoreGeneration == envelope.ExpectedStoreGeneration)
                {
                    generationAllowed = true;
                    break;
                }
            }
            if (!generationAllowed)
                throw new InvalidOperationException($"Typed command references generation {envelope.ExpectedStoreGeneration} outside the session manifest.");

            _nextCommandSequence = sequence;
            _output.Command(envelope);
        }

        private void ProcessReceiveResult(
            RuntimeClientLogicalEnvelopeReceiver receiver,
            in RuntimeClientReceiveResult result)
        {
            if (result.Kind != RuntimeClientReceiveResultKind.BaselineApplied
                && result.Kind != RuntimeClientReceiveResultKind.DeltaApplied)
            {
                return;
            }

            SendAppliedAck(receiver);
        }

        private void SendAppliedAck(RuntimeClientLogicalEnvelopeReceiver receiver)
        {
            if (receiver == null || receiver.AppliedBaselineId == 0 || receiver.LastAppliedSequence == 0)
                throw new InvalidOperationException("Cannot ACK a receiver without an applied logical envelope.");
            _output.Ack(new RtStoreAckData(
                receiver.SessionId,
                receiver.Store,
                receiver.AppliedBaselineId,
                receiver.LastAppliedSequence));
        }

        private static bool IsPreparedInitialBaseline(
            InitialStoreState initial,
            in RuntimeClientBaselineEnvelope baseline)
        {
            if (initial?.Stage == null
                || initial.Stage.State != RuntimeReplicaBaselineStageState.Built)
            {
                return false;
            }

            var expected = initial.Envelope;
            return baseline.SessionId == expected.SessionId
                   && baseline.Store == expected.Store
                   && baseline.BaselineId == expected.BaselineId
                   && baseline.DeliverySequence == expected.DeliverySequence
                   && baseline.StoreRevision == expected.StoreRevision
                   && CheckpointBoundariesEqual(
                       baseline.CheckpointBoundary,
                       expected.CheckpointBoundary);
        }

        private static bool IsInitialCatchupFailure(in RuntimeClientReceiveResult result)
        {
            return result.Kind == RuntimeClientReceiveResultKind.ResyncRequested
                   || result.Kind == RuntimeClientReceiveResultKind.Rejected
                   || result.Kind == RuntimeClientReceiveResultKind.WaitingForBaseline;
        }

        private void RequestInitialResync(InitialStoreState initial, bool forceNewBaseline = false)
        {
            if (initial.ResyncRequested)
                return;
            initial.ResyncRequested = true;
            var envelope = initial.Envelope;
            SendResync(new RtStoreResyncData(
                _handshake.Manifest.SessionId,
                initial.Store,
                forceNewBaseline ? 0 : envelope.BaselineId,
                envelope.DeliverySequence == 0 ? 1 : envelope.DeliverySequence));
            initial.Assembler.Reset();
            initial.IsComplete = false;
            initial.HasStarted = false;
            initial.Stage = null;
            initial.Receiver = null;
            initial.ApplyToStaging = false;
            initial.PendingDeltas.Clear();
            initial.PendingDeltaBytes = 0;
        }

        private RuntimeClientReceiveResult
            RequestInitialCheckpointGroupResync()
        {
            _initialCheckpointBoundary = null;
            _initialCheckpointBoundaryObserved = false;
            _pendingJournalBatches.Clear();
            _pendingJournalEntryCount = 0;
            _pendingJournalPayloadBytes = 0;
            _journalReceiver = null;
            _journalCatchupObserved = false;
            foreach (var initial in _initialStores.Values)
            {
                initial.ResetForReplacement();
                RequestInitialResync(
                    initial,
                    forceNewBaseline: true);
            }

            SetReplicaReady(false);
            return Accepted(
                RuntimeClientReceiveResultKind.ResyncRequested);
        }

        private void SendResync(in RtStoreResyncData value)
        {
            _telemetry.RecordResync();
            _output.Resync(value);
        }

        private RuntimeCommandJournalReceiveResult ApplyJournalBatch(
            RuntimeCommandJournalBatch batch)
        {
            if (_journalReceiver == null)
            {
                return RequestJournalResync(
                    new RuntimeCommandJournalReceiveResult(
                        RuntimeCommandJournalReceiveStatus.CheckpointMismatch,
                        0,
                        0));
            }

            var result = _journalReceiver.Receive(batch);
            if (!result.Succeeded)
            {
                return RequestJournalResync(result);
            }

            _journalCatchupObserved =
                _journalReceiver.CatchupComplete;
            if (_baselineGroupPublished
                && _journalCatchupObserved)
            {
                SetReplicaReady(true);
            }

            return result;
        }

        private RuntimeCommandJournalReceiveResult RequestJournalResync(
            in RuntimeCommandJournalReceiveResult result)
        {
            _journalFullBaselineRequested =
                RequiresFullCheckpointBaseline(result.Status);
            var boundary = _initialCheckpointBoundary;
            if (boundary.HasValue
                && _handshake.Manifest != null
                && _output.JournalResync != null)
            {
                _telemetry.RecordResync();
                _output.JournalResync(
                    new RtCommandJournalResyncData(
                        _handshake.Manifest.SessionId,
                        boundary.Value.GroupId,
                        boundary.Value.CheckpointHash,
                        result.Cursor,
                        _journalFullBaselineRequested));
            }

            SetReplicaReady(false);
            return result;
        }

        private void RestartInitialCheckpointGroup()
        {
            foreach (var initial in _initialStores.Values)
            {
                var stage = initial.Stage;
                if (stage != null
                    && stage.State != RuntimeReplicaBaselineStageState.Published
                    && stage.State != RuntimeReplicaBaselineStageState.Aborted
                    && stage.State != RuntimeReplicaBaselineStageState.Failed)
                {
                    stage.Abort();
                }

                initial.ResetForReplacement();
            }

            _receivers.Clear();
            _pendingJournalBatches.Clear();
            _pendingJournalEntryCount = 0;
            _pendingJournalPayloadBytes = 0;
            _journalReceiver = null;
            _initialCheckpointBoundary = null;
            _initialCheckpointBoundaryObserved = false;
            _baselineGroupPublished = false;
            _journalCatchupObserved = false;
            _journalFullBaselineRequested = false;
        }

        private static bool RequiresFullCheckpointBaseline(
            RuntimeCommandJournalReceiveStatus status)
        {
            return status == RuntimeCommandJournalReceiveStatus.ApplyFailed
                   || status == RuntimeCommandJournalReceiveStatus.CheckpointMismatch
                   || status == RuntimeCommandJournalReceiveStatus.ScopeMismatch
                   || status == RuntimeCommandJournalReceiveStatus.UnknownCodec
                   || status == RuntimeCommandJournalReceiveStatus.InvalidBatch;
        }

        private static int TakeJournalPayloadBytes(
            RuntimeCommandJournalBatch batch)
        {
            var result = 0;
            for (var i = 0; i < batch.Entries.Count; i++)
            {
                result = checked(
                    result
                    + batch.Entries[i].EncodedCommand.PayloadBytes);
            }

            return result;
        }

        private static void ValidatePreparedStage(
            RuntimeReplicaBaselineStage stage,
            in RuntimeClientBaselineEnvelope envelope)
        {
            if (stage.StoreReference != envelope.Store
                || stage.BaselineId != envelope.BaselineId
                || stage.StoreRevision != envelope.StoreRevision)
            {
                throw new InvalidOperationException("Prepared baseline stage does not match its logical envelope.");
            }
        }

        private void SetReplicaReady(bool ready)
        {
            if (_initialPublicationComplete == ready && RuntimeExecutionContext.IsReplicaReady == ready)
                return;
            _initialPublicationComplete = ready;
            ReplicaReadyChanged?.Invoke(ready);
        }

        private bool AcceptInitialCheckpointBoundary(in RuntimeBaselineChunk chunk)
        {
            var boundary = TakeCheckpointBoundary(chunk);
            var requiresCheckpointBoundary =
                _context.JournalSubscriptionScope != null
                && _context.JournalSubscriptionScope.Contains(
                    chunk.Store.StoreId);
            if (boundary.HasValue
                != requiresCheckpointBoundary)
            {
                return false;
            }
            if (!requiresCheckpointBoundary)
            {
                return true;
            }

            if (!_initialCheckpointBoundaryObserved)
            {
                _initialCheckpointBoundary = boundary;
                _initialCheckpointBoundaryObserved = true;
                return true;
            }

            return CheckpointBoundariesEqual(_initialCheckpointBoundary, boundary);
        }

        private static RuntimeCheckpointBoundary? TakeCheckpointBoundary(
            in RuntimeBaselineChunk chunk)
        {
            if (string.IsNullOrEmpty(chunk.CheckpointGroupId))
            {
                return null;
            }

            return new RuntimeCheckpointBoundary(
                chunk.CheckpointGroupId,
                chunk.CompletedTick,
                chunk.JournalCursor,
                chunk.CheckpointHash);
        }

        private static bool CheckpointBoundariesEqual(
            RuntimeCheckpointBoundary? left,
            RuntimeCheckpointBoundary? right)
        {
            if (!left.HasValue || !right.HasValue)
            {
                return left.HasValue == right.HasValue;
            }

            return string.Equals(
                       left.Value.GroupId,
                       right.Value.GroupId,
                       StringComparison.Ordinal)
                   && left.Value.CompletedTick == right.Value.CompletedTick
                   && left.Value.JournalCursor == right.Value.JournalCursor
                   && string.Equals(
                       left.Value.CheckpointHash,
                       right.Value.CheckpointHash,
                       StringComparison.Ordinal);
        }

        private static RuntimeClientReceiveResult Accepted(RuntimeClientReceiveResultKind kind)
        {
            return new RuntimeClientReceiveResult(kind, RuntimeProtocolRejectCode.None, 0, 0);
        }

        private static RuntimeClientReceiveResult Rejected(RuntimeProtocolRejectCode code)
        {
            return new RuntimeClientReceiveResult(RuntimeClientReceiveResultKind.Rejected, code, 0, 0);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RuntimeProtocolClientCoordinator));
        }

        private class InitialStoreState
        {
            public readonly NetStoreRef Store;
            public readonly RuntimeBaselineChunkAssembler Assembler = new();
            public readonly List<RuntimeClientDeltaEnvelope> PendingDeltas = new();
            public RuntimeClientBaselineEnvelope Envelope;
            public RuntimeReplicaBaselineStage Stage;
            public RuntimeClientLogicalEnvelopeReceiver Receiver;
            public int PendingDeltaBytes;
            public double StartedAt;
            public bool HasStarted;
            public bool IsComplete;
            public bool ResyncRequested;
            public bool ApplyToStaging;
            public ulong LastBaselineId;

            public InitialStoreState(NetStoreRef store)
            {
                Store = store;
            }

            public void ResetForReplacement()
            {
                Assembler.Reset();
                Envelope = default;
                Stage = null;
                Receiver = null;
                PendingDeltas.Clear();
                PendingDeltaBytes = 0;
                StartedAt = 0;
                HasStarted = false;
                IsComplete = false;
                ResyncRequested = false;
                ApplyToStaging = false;
            }
        }
    }

    public readonly struct RtStateStreamFrameData
    {
        public readonly ulong SessionId;
        public readonly NetStoreRef Store;
        public readonly uint StreamTypeId;
        public readonly uint Sequence;
        public readonly uint SimulationTick;
        public readonly byte[] Payload;

        public RtStateStreamFrameData(
            ulong sessionId,
            NetStoreRef store,
            uint streamTypeId,
            uint sequence,
            uint simulationTick,
            byte[] payload)
        {
            SessionId = sessionId;
            Store = store;
            StreamTypeId = streamTypeId;
            Sequence = sequence;
            SimulationTick = simulationTick;
            Payload = payload;
        }
    }
}
