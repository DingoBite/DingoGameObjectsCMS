using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;

namespace DingoGameObjectsCMS.Mirror.Protocol
{
    public class RuntimeProtocolServerOutput
    {
        public readonly Action<int, RuntimeSessionManifestSnapshot> Manifest;
        public readonly Action<int, RuntimeProtocolRejectCode, string> Reject;
        public readonly Action<int, RuntimeBaselineChunk> BaselineChunk;
        public readonly Action<int, RuntimeClientDeltaEnvelope> Delta;
        public readonly Action<int, RuntimeCommandResult> CommandResult;
        public readonly Action<int, RtStateStreamFrameData> StateStream;
        public readonly Action<int, RuntimeCommandJournalBatch> JournalBatch;
        public readonly RuntimeReliableDeltaTransportBudgetCheck ReliableDeltaFitsTransport;

        public RuntimeProtocolServerOutput(
            Action<int, RuntimeSessionManifestSnapshot> manifest,
            Action<int, RuntimeProtocolRejectCode, string> reject,
            Action<int, RuntimeBaselineChunk> baselineChunk,
            Action<int, RuntimeClientDeltaEnvelope> delta,
            Action<int, RuntimeCommandResult> commandResult,
            RuntimeReliableDeltaTransportBudgetCheck reliableDeltaFitsTransport,
            Action<int, RtStateStreamFrameData> stateStream = null,
            Action<int, RuntimeCommandJournalBatch> journalBatch = null)
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Reject = reject ?? throw new ArgumentNullException(nameof(reject));
            BaselineChunk = baselineChunk ?? throw new ArgumentNullException(nameof(baselineChunk));
            Delta = delta ?? throw new ArgumentNullException(nameof(delta));
            CommandResult = commandResult ?? throw new ArgumentNullException(nameof(commandResult));
            ReliableDeltaFitsTransport = reliableDeltaFitsTransport
                                         ?? throw new ArgumentNullException(nameof(reliableDeltaFitsTransport));
            StateStream = stateStream ?? ((connectionId, value) => { });
            JournalBatch = journalBatch;
        }
    }

    public class RuntimeProtocolServerCoordinator : IDisposable
    {
        private const int DEFAULT_JOURNAL_REVISIONS = 1_024;
        private const long DEFAULT_JOURNAL_BYTES = 16L * 1024L * 1024L;

        private readonly RuntimeProtocolContext _context;
        private readonly RuntimeProtocolServerOutput _output;
        private readonly RuntimeServerStoreProjection _projection;
        private readonly RuntimeStoreBaselineCodec _baselineCodec;
        private readonly RuntimeStoreDeltaCodec _deltaCodec;
        private readonly RuntimeStateStreamFrameCodec _stateStreamCodec = new();
        private readonly RuntimeNetworkTelemetry _telemetry;
        private readonly Dictionary<int, ConnectionState> _connections = new();
        private readonly Dictionary<NetStoreRef, StoreState> _stores = new();
        private ulong _nextSessionId;
        private bool _disposed;
        private bool _sessionInvalidated;

        public int ConnectionCount => _connections.Count;
        public bool IsSessionInvalidated => _sessionInvalidated;
        public RuntimeNetworkTelemetry Telemetry => _telemetry;
        public event Action<int, ulong> ConnectionReady;
        public event Action<int> ConnectionInvalidated;
        public event Action<int, NetStoreRef, RuntimeConnectionStoreReplicationState> BaselineStarted;
        public event Action<int> ConnectionRemoved;

        public RuntimeProtocolServerCoordinator(
            RuntimeProtocolContext context,
            RuntimeProtocolServerOutput output,
            ulong firstSessionId = 1,
            RuntimeNetworkTelemetry telemetry = null)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _output = output ?? throw new ArgumentNullException(nameof(output));
            if (context.ManifestTemplate == null)
                throw new InvalidOperationException("Protocol server requires an authoritative manifest template.");
            if (firstSessionId == 0)
                throw new ArgumentOutOfRangeException(nameof(firstSessionId));

            _nextSessionId = firstSessionId;
            _telemetry = telemetry ?? new RuntimeNetworkTelemetry();
            _projection = new RuntimeServerStoreProjection(context);
            _baselineCodec = new RuntimeStoreBaselineCodec(context.PatchCodecs);
            _deltaCodec = new RuntimeStoreDeltaCodec(context.PatchCodecs);
            if (context.CheckpointBoundaryProvider != null)
            {
                if (_output.JournalBatch == null)
                {
                    throw new InvalidOperationException(
                        "Checkpoint journal transport requires a server journal output.");
                }

                context.CommandsBus.Journal.EntryRecorded += OnJournalEntryRecorded;
            }
            var manifestStores = context.GetManifestStores();
            for (var i = 0; i < manifestStores.Count; i++)
            {
                var storeReference = manifestStores[i];
                var store = context.GetRequiredAuthoritativeStore(storeReference);
                var journal = new RuntimeStoreRevisionJournal(
                    store.Id,
                    store.Realm,
                    store.StoreGeneration,
                    store.StoreRevision,
                    new RuntimeStoreRevisionJournalLimits(DEFAULT_JOURNAL_REVISIONS, DEFAULT_JOURNAL_BYTES));
                var state = new StoreState(storeReference, store, journal);
                state.BatchHandler = batch => OnCommittedBatch(state, batch);
                store.CommittedBatch += state.BatchHandler;
                _stores.Add(storeReference, state);
            }
            RuntimeStores.StoreLifecycleChanged += OnStoreLifecycleChanged;
        }

        public void AddConnection(int connectionId)
        {
            ThrowIfDisposed();
            if (!_connections.TryAdd(connectionId, new ConnectionState()))
                throw new InvalidOperationException($"Protocol connection {connectionId} is already registered.");
        }

        public void RemoveConnection(int connectionId)
        {
            if (!_connections.Remove(connectionId))
                return;
            _telemetry.RemoveConnection(connectionId);
            _context.CommandRegistry?.RemoveConnection(connectionId);
            ConnectionRemoved?.Invoke(connectionId);
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

        public bool TryGetConnectionStoreState(
            int connectionId,
            in NetStoreRef store,
            out RuntimeConnectionStoreReplicationState state)
        {
            state = null;
            if (!_connections.TryGetValue(connectionId, out var connection)
                || connection.Handshake?.CanCreateReplica != true
                || !connection.Stores.TryGetValue(store, out var connectionStore))
            {
                return false;
            }

            state = connectionStore.Replication;
            return true;
        }

        public IReadOnlyList<RuntimeReadyConnection> GetReadyConnections()
        {
            var result = new List<RuntimeReadyConnection>();
            foreach (var pair in _connections)
            {
                if (pair.Value.Handshake?.CanCreateReplica == true)
                    result.Add(new RuntimeReadyConnection(pair.Key, pair.Value.Handshake.SessionId));
            }
            result.Sort((left, right) => left.ConnectionId.CompareTo(right.ConnectionId));
            return Array.AsReadOnly(result.ToArray());
        }

        public RuntimeSessionHandshakeResult ReceiveHello(
            int connectionId,
            in RuntimeSessionDescriptor descriptor,
            ulong clientNonce)
        {
            ThrowIfDisposed();
            var connection = GetConnection(connectionId);
            if (connection.Handshake != null)
                return Reject(connectionId, RuntimeProtocolRejectCode.InvalidEnvelope, "Connection already sent a protocol hello.");

            var sessionId = TakeSessionId();
            connection.Handshake = new RuntimeSessionServerHandshake(sessionId, _context.ManifestTemplate);
            var result = connection.Handshake.ReceiveHello(descriptor, clientNonce);
            if (!result.Accepted)
            {
                _output.Reject(connectionId, result.RejectCode, result.Detail);
                return result;
            }

            _output.Manifest(connectionId, connection.Handshake.Manifest);
            return result;
        }

        public RuntimeSessionHandshakeResult ReceiveReady(int connectionId, ulong sessionId)
        {
            ThrowIfDisposed();
            var connection = GetConnection(connectionId);
            if (connection.Handshake == null)
                return Reject(connectionId, RuntimeProtocolRejectCode.InvalidEnvelope, "Ready arrived before protocol hello.");

            RuntimeCheckpointBoundary? checkpointBoundary = null;
            if (_context.CheckpointBoundaryProvider != null)
            {
                checkpointBoundary = _context.CheckpointBoundaryProvider();
                if (!checkpointBoundary.HasValue)
                {
                    return Reject(
                        connectionId,
                        RuntimeProtocolRejectCode.SessionNotReady,
                        "No completed checkpoint is available for journal recovery.");
                }

                var read = _context.CommandsBus.Journal.ReadAfter(
                    checkpointBoundary.Value.GroupId,
                    checkpointBoundary.Value.JournalCursor);
                if (!read.Succeeded)
                {
                    return Reject(
                        connectionId,
                        RuntimeProtocolRejectCode.SessionNotReady,
                        $"Checkpoint journal recovery is unavailable: {read.Status}.");
                }
                if (!_context.CommandsBus.Journal.TryGetWindow(
                        checkpointBoundary.Value.GroupId,
                        out var window)
                    || !window.Scope.Equals(
                        _context.JournalSubscriptionScope))
                {
                    return Reject(
                        connectionId,
                        RuntimeProtocolRejectCode.InvalidManifest,
                        "Checkpoint journal window scope does not exactly match the protocol subscription scope.");
                }
            }

            var result = connection.Handshake.ReceiveReady(sessionId);
            if (!result.Accepted)
            {
                _output.Reject(connectionId, result.RejectCode, result.Detail);
                return result;
            }

            connection.CheckpointBoundary = checkpointBoundary;
            connection.JournalSendCursor =
                checkpointBoundary?.JournalCursor ?? 0;

            foreach (var pair in _stores)
            {
                var storeState = new ConnectionStoreState(
                    new RuntimeConnectionStoreReplicationState(pair.Key, pair.Value.Store.StoreRevision));
                connection.Stores.Add(pair.Key, storeState);
                BeginBaseline(connectionId, connection, pair.Value, storeState);
            }
            if (checkpointBoundary.HasValue)
            {
                SendJournalCatchup(
                    connectionId,
                    connection,
                    forceMarker: true);
            }

            ConnectionReady?.Invoke(connectionId, sessionId);

            return result;
        }

        public RuntimeSessionHandshakeResult SendStateStream(int connectionId, in RtStateStreamFrameData value)
        {
            ThrowIfDisposed();
            var connection = GetConnection(connectionId);
            var resolution = ResolveStateStreamSend(
                connection,
                value.SessionId,
                value.Store,
                value.StreamTypeId,
                out var storeState,
                out var profile);
            if (!resolution.Accepted)
                return resolution;
            if (value.Payload == null || value.Payload.Length == 0 || value.Payload.Length > RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES)
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidEnvelope, "State stream payload size is invalid.");

            RuntimeStateStreamFrame frame;
            try
            {
                frame = DecodeStateStreamPayload(value.Payload);
            }
            catch (Exception exception) when (exception is FormatException || exception is ArgumentException)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidEnvelope, exception.Message);
            }
            if (frame == null
                || frame.Store != value.Store
                || frame.StreamTypeId != value.StreamTypeId
                || frame.Sequence != value.Sequence
                || frame.SimulationTick != value.SimulationTick)
            {
                return RuntimeSessionHandshakeResult.Reject(
                    RuntimeProtocolRejectCode.InvalidEnvelope,
                    "State stream wire header does not match its packed payload.");
            }

            var validation = ValidateStateStreamFrame(value.Store, storeState, profile, frame);
            return validation.Accepted
                ? PublishStateStream(connectionId, value)
                : validation;
        }

        public RuntimeSessionHandshakeResult SendStateStream(
            int connectionId,
            RuntimeStateStreamFrame frame)
        {
            ThrowIfDisposed();
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            var connection = GetConnection(connectionId);
            var sessionId = connection.Handshake?.SessionId ?? 0;
            var resolution = ResolveStateStreamSend(
                connection,
                sessionId,
                frame.Store,
                frame.StreamTypeId,
                out var storeState,
                out var profile);
            if (!resolution.Accepted)
                return resolution;

            var encodeMeasure = RuntimeNetworkEncodeMeasure.Begin();
            RuntimeSessionHandshakeResult validation;
            byte[] payload = null;
            try
            {
                validation = ValidateStateStreamFrame(frame.Store, storeState, profile, frame);
                if (validation.Accepted)
                    payload = EncodeStateStreamFrame(frame);
            }
            catch (Exception exception) when (exception is FormatException
                                              || exception is ArgumentException
                                              || exception is InvalidOperationException
                                              || exception is OverflowException)
            {
                validation = RuntimeSessionHandshakeResult.Reject(
                    RuntimeProtocolRejectCode.InvalidEnvelope,
                    exception.Message);
            }
            finally
            {
                _telemetry.RecordEncode(
                    RuntimeNetworkStreamKey.HotState(frame.StreamTypeId),
                    encodeMeasure.Complete());
            }

            if (!validation.Accepted)
                return validation;
            if (payload == null || payload.Length == 0 || payload.Length > RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES)
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidEnvelope, "State stream payload size is invalid.");

            return PublishStateStream(connectionId, new RtStateStreamFrameData(
                sessionId,
                frame.Store,
                frame.StreamTypeId,
                frame.Sequence,
                frame.SimulationTick,
                payload));
        }

        public RuntimeSessionHandshakeResult ReceiveAck(int connectionId, in RtStoreAckData ack)
        {
            ThrowIfDisposed();
            var connection = GetConnection(connectionId);
            var authorization = Authorize(connection, ack.SessionId, ack.Store);
            if (!authorization.Accepted)
                return Reject(connectionId, authorization.RejectCode, authorization.Detail);
            if (!connection.Stores.TryGetValue(ack.Store, out var storeState))
                return Reject(connectionId, RuntimeProtocolRejectCode.InvalidStore, $"ACK references unknown store '{ack.Store}'.");

            var result = storeState.Replication.Acknowledge(ack.BaselineId, ack.DeliverySequence);
            if (result == RuntimeConnectionAckResult.Accepted
                || result == RuntimeConnectionAckResult.Duplicate
                || result == RuntimeConnectionAckResult.Stale)
            {
                _telemetry.ObserveConnectionStore(connectionId, storeState.Replication);
                return RuntimeSessionHandshakeResult.Success();
            }

            return Reject(connectionId, RuntimeProtocolRejectCode.InvalidEnvelope, $"Rejected ACK: {result}.");
        }

        public RuntimeSessionHandshakeResult ReceiveResync(int connectionId, in RtStoreResyncData request)
        {
            ThrowIfDisposed();
            var connection = GetConnection(connectionId);
            var authorization = Authorize(connection, request.SessionId, request.Store);
            if (!authorization.Accepted)
                return Reject(connectionId, authorization.RejectCode, authorization.Detail);
            if (!connection.Stores.TryGetValue(request.Store, out var connectionStore)
                || !_stores.TryGetValue(request.Store, out var storeState))
            {
                return Reject(connectionId, RuntimeProtocolRejectCode.InvalidStore, $"Resync references unknown store '{request.Store}'.");
            }

            _telemetry.RecordResync();

            if (request.BaselineId == connectionStore.Replication.BaselineId)
            {
                var activeBaseline = connectionStore.Replication.ActiveBaseline;
                if (activeBaseline != null
                    && request.ExpectedDeliverySequence == activeBaseline.DeliverySequence)
                {
                    SendBaselineChunks(connectionId, connection, activeBaseline);
                    return RuntimeSessionHandshakeResult.Success();
                }

                if (connectionStore.Replication.TryGetPendingReliableEnvelope(request.ExpectedDeliverySequence, out var pending)
                    && pending.PayloadBytes > 0)
                {
                    SendDelta(connectionId, connection, request.Store, pending);
                    return RuntimeSessionHandshakeResult.Success();
                }
            }

            BeginBaseline(connectionId, connection, storeState, connectionStore);
            return RuntimeSessionHandshakeResult.Success();
        }

        public RuntimeSessionHandshakeResult ReceiveJournalResync(
            int connectionId,
            in RtCommandJournalResyncData request)
        {
            ThrowIfDisposed();
            var connection = GetConnection(connectionId);
            if (connection.Handshake?.CanCreateReplica != true
                || request.SessionId != connection.Handshake.SessionId
                || !connection.CheckpointBoundary.HasValue)
            {
                return Reject(
                    connectionId,
                    RuntimeProtocolRejectCode.SessionNotReady,
                    "Journal resync references a session without an active checkpoint boundary.");
            }

            var boundary = connection.CheckpointBoundary.Value;
            if (!string.Equals(
                    request.CheckpointGroupId,
                    boundary.GroupId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    request.CheckpointHash,
                    boundary.CheckpointHash,
                    StringComparison.Ordinal)
                || request.ExpectedCursor < boundary.JournalCursor
                || request.ExpectedCursor > _context.CommandsBus.Journal.Cursor)
            {
                return Reject(
                    connectionId,
                    RuntimeProtocolRejectCode.InvalidEnvelope,
                    "Journal resync checkpoint identity or cursor is invalid.");
            }

            if (request.ForceCheckpointBaseline)
            {
                var nextBoundary = _context.CheckpointBoundaryProvider?.Invoke();
                if (!nextBoundary.HasValue
                    || nextBoundary.Value.JournalCursor
                        != _context.CommandsBus.Journal.Cursor)
                {
                    return Reject(
                        connectionId,
                        RuntimeProtocolRejectCode.SessionNotReady,
                        "Journal apply failure requires a new checkpoint at the current journal cursor.");
                }

                SendCheckpointBaselineGroup(
                    connectionId,
                    connection,
                    nextBoundary.Value);
                return RuntimeSessionHandshakeResult.Success();
            }

            connection.JournalSendCursor = request.ExpectedCursor;
            try
            {
                SendJournalCatchup(
                    connectionId,
                    connection,
                    forceMarker: true);
                return RuntimeSessionHandshakeResult.Success();
            }
            catch (Exception exception)
            {
                var failureDetail = exception.Message;
                var nextBoundary =
                    _context.CheckpointBoundaryProvider?.Invoke();
                if (nextBoundary.HasValue
                    && nextBoundary.Value.JournalCursor
                    > request.ExpectedCursor)
                {
                    try
                    {
                        SendCheckpointBaselineGroup(
                            connectionId,
                            connection,
                            nextBoundary.Value);
                        return RuntimeSessionHandshakeResult.Success();
                    }
                    catch (Exception fallbackException)
                    {
                        failureDetail =
                            $"{failureDetail}; checkpoint fallback failed: {fallbackException.Message}";
                    }
                }

                return Reject(
                    connectionId,
                    RuntimeProtocolRejectCode.SessionNotReady,
                    $"Journal recovery window is unavailable: {failureDetail}");
            }
        }

        private void SendCheckpointBaselineGroup(
            int connectionId,
            ConnectionState connection,
            in RuntimeCheckpointBoundary boundary)
        {
            connection.CheckpointBoundary = boundary;
            connection.JournalSendCursor =
                boundary.JournalCursor;
            foreach (var pair in _stores)
            {
                if (!connection.Stores.TryGetValue(
                        pair.Key,
                        out var connectionStore))
                {
                    throw new InvalidOperationException(
                        $"Journal recovery group is missing store '{pair.Key}'.");
                }

                BeginBaseline(
                    connectionId,
                    connection,
                    pair.Value,
                    connectionStore);
            }

            SendJournalCatchup(
                connectionId,
                connection,
                forceMarker: true);
        }

        public RuntimeCommandResult ReceiveCommand(int connectionId, in RuntimeCommandEnvelope envelope, double nowSeconds)
        {
            ThrowIfDisposed();
            var connection = GetConnection(connectionId);
            RuntimeCommandResult result;
            if (_context.CommandRegistry == null)
            {
                result = new RuntimeCommandResult(envelope.ClientSequence, RuntimeCommandRejectCode.UnknownCommand);
            }
            else
            {
                var authority = new RuntimeCommandAuthority(
                    connectionId,
                    connection.Handshake?.CanCreateReplica == true,
                    generation => AllowsGeneration(connection, generation),
                    objectMembershipValidator: target => IsAcknowledged(connection, target));
                result = _context.CommandRegistry.Dispatch(envelope, authority, nowSeconds);
            }

            _output.CommandResult(connectionId, result);
            return result;
        }

        public void RequestBaseline(int connectionId, in NetStoreRef storeReference)
        {
            ThrowIfDisposed();
            var connection = GetConnection(connectionId);
            if (connection.Handshake?.CanCreateReplica != true
                || !connection.Stores.TryGetValue(storeReference, out var connectionStore)
                || !_stores.TryGetValue(storeReference, out var storeState))
            {
                throw new InvalidOperationException($"Connection {connectionId} cannot baseline store '{storeReference}'.");
            }

            BeginBaseline(connectionId, connection, storeState, connectionStore);
        }

        public RuntimeInterestRefreshResult RefreshInterest(
            int connectionId,
            in NetStoreRef storeReference)
        {
            ThrowIfDisposed();
            if (!_connections.TryGetValue(connectionId, out var connection)
                || connection.Handshake?.CanCreateReplica != true)
            {
                return new RuntimeInterestRefreshResult(
                    RuntimeInterestRefreshStatus.NotReady,
                    detail: $"Connection {connectionId} has not completed protocol readiness.");
            }

            if (!connection.Stores.TryGetValue(storeReference, out var connectionStore)
                || !_stores.TryGetValue(storeReference, out var storeState))
            {
                return new RuntimeInterestRefreshResult(
                    RuntimeInterestRefreshStatus.InvalidStore,
                    detail: $"Connection {connectionId} cannot refresh unknown store '{storeReference}'.");
            }

            var projectionCommitted = false;
            try
            {
                SynchronizeObservedRevision(storeState, connectionStore);
                if (connectionStore.Replication.BaselineId == 0
                    || connectionStore.Replication.NeedsBaseline)
                {
                    BeginBaseline(connectionId, connection, storeState, connectionStore);
                    return new RuntimeInterestRefreshResult(
                        RuntimeInterestRefreshStatus.NeedsBaseline,
                        connectionStore.Replication.ActiveBaseline?.DeliverySequence ?? 0,
                        "Interest change was folded into a replacement baseline.");
                }

                var stagedShadow = connectionStore.Shadow.Clone();
                var nextSequence = checked(connectionStore.Replication.HighestDeliverySequence + 1);
                var delta = _projection.BuildInterestDelta(
                    storeState.Store,
                    connectionId,
                    connectionStore.Replication,
                    stagedShadow,
                    nextSequence,
                    out var enters,
                    out var leaves);
                if (delta == null)
                {
                    return new RuntimeInterestRefreshResult(
                        RuntimeInterestRefreshStatus.NoChange,
                        detail: "The projected membership and topology are unchanged.");
                }

                var encodeMeasure = RuntimeNetworkEncodeMeasure.Begin();
                var payload = _deltaCodec.Encode(delta);
                _telemetry.RecordEncode(
                    RuntimeNetworkStreamKey.ReliableStore,
                    encodeMeasure.Complete());
                var transportEnvelope = new RuntimeReliableDeltaTransportEnvelope(
                    connection.Handshake.SessionId,
                    storeState.Reference,
                    connectionStore.Replication.BaselineId,
                    delta.DeliverySequence,
                    delta.FromRevision,
                    delta.ToRevision,
                    payload,
                    delta.Kind);
                if (!_output.ReliableDeltaFitsTransport(transportEnvelope))
                {
                    BeginBaseline(connectionId, connection, storeState, connectionStore);
                    return new RuntimeInterestRefreshResult(
                        RuntimeInterestRefreshStatus.NeedsBaseline,
                        connectionStore.Replication.ActiveBaseline?.DeliverySequence ?? 0,
                        "Interest delta exceeded the reliable transport budget and was replaced by a baseline.");
                }

                var enqueue = connectionStore.Replication.TryEnqueueInterestDelta(
                    payload,
                    enters,
                    leaves,
                    out var envelope);
                if (enqueue == RuntimeConnectionDeltaEnqueueResult.NeedsBaseline)
                {
                    BeginBaseline(connectionId, connection, storeState, connectionStore);
                    return new RuntimeInterestRefreshResult(
                        RuntimeInterestRefreshStatus.NeedsBaseline,
                        connectionStore.Replication.ActiveBaseline?.DeliverySequence ?? 0,
                        "Interest delta queue overflowed and was replaced by a baseline.");
                }

                if (enqueue != RuntimeConnectionDeltaEnqueueResult.Enqueued)
                {
                    return new RuntimeInterestRefreshResult(
                        RuntimeInterestRefreshStatus.InvalidProjection,
                        detail: $"Interest membership transition was rejected: {enqueue}.");
                }

                projectionCommitted = true;
                if (envelope.DeliverySequence != delta.DeliverySequence)
                    throw new InvalidOperationException("Reliable queue allocated an unexpected interest delivery sequence.");

                connectionStore.Shadow.ReplaceWith(stagedShadow);
                _telemetry.ObserveConnectionStore(connectionId, connectionStore.Replication);
                SendDelta(connectionId, connection, storeState.Reference, envelope);
                return new RuntimeInterestRefreshResult(
                    RuntimeInterestRefreshStatus.Enqueued,
                    envelope.DeliverySequence);
            }
            catch (Exception exception) when (exception is InvalidOperationException
                                               || exception is ArgumentException
                                               || exception is FormatException)
            {
                if (projectionCommitted)
                    throw;
                // Visibility is configuration owned by the game. Reject a bad
                // proposed projection locally; do not mutate live shadow or
                // membership, emit a protocol reject, or disconnect a healthy
                // client session.
                return new RuntimeInterestRefreshResult(
                    RuntimeInterestRefreshStatus.InvalidProjection,
                    detail: exception.Message);
            }
        }

        public int RefreshInterest(int connectionId)
        {
            ThrowIfDisposed();
            var enqueued = 0;
            foreach (var storeReference in _stores.Keys)
            {
                var result = RefreshInterest(connectionId, storeReference);
                if (result.Status == RuntimeInterestRefreshStatus.Enqueued)
                    enqueued++;
            }

            return enqueued;
        }

        public int RefreshInterestAll()
        {
            ThrowIfDisposed();
            var enqueued = 0;
            var connectionIds = new List<int>(_connections.Keys);
            for (var i = 0; i < connectionIds.Count; i++)
            {
                enqueued += RefreshInterest(connectionIds[i]);
            }

            return enqueued;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_context.CheckpointBoundaryProvider != null)
            {
                _context.CommandsBus.Journal.EntryRecorded -= OnJournalEntryRecorded;
            }
            RuntimeStores.StoreLifecycleChanged -= OnStoreLifecycleChanged;
            foreach (var pair in _stores)
            {
                pair.Value.Store.CommittedBatch -= pair.Value.BatchHandler;
            }

            foreach (var connectionId in new List<int>(_connections.Keys))
            {
                RemoveConnection(connectionId);
            }
            _stores.Clear();
        }

        private void OnCommittedBatch(StoreState storeState, RuntimeStoreCommittedBatch batch)
        {
            if (_disposed || _sessionInvalidated)
                return;

            _telemetry.RecordCommittedBatch(batch);

            RuntimeStoreRevisionRecord revision;
            try
            {
                revision = storeState.Journal.Append(batch);
            }
            catch (Exception exception)
            {
                RejectAll(RuntimeProtocolRejectCode.InvalidEnvelope, exception.Message);
                return;
            }

            foreach (var pair in _connections)
            {
                var connectionId = pair.Key;
                var connection = pair.Value;
                if (connection.Handshake?.CanCreateReplica != true
                    || !connection.Stores.TryGetValue(storeState.Reference, out var connectionStore))
                {
                    continue;
                }

                try
                {
                    if (revision.ReplicationSuppressed)
                    {
                        connectionStore.Replication.ObserveFilteredRevision(revision.StoreRevision);
                        continue;
                    }

                    var nextSequence = checked(connectionStore.Replication.HighestDeliverySequence + 1);
                    var delta = _projection.BuildDelta(
                        storeState.Store,
                        revision,
                        connectionId,
                        connectionStore.Replication,
                        connectionStore.Shadow,
                        nextSequence,
                        out var enters,
                        out var leaves);
                    if (delta == null)
                    {
                        connectionStore.Replication.ObserveFilteredRevision(revision.StoreRevision);
                        continue;
                    }

                    var encodeMeasure = RuntimeNetworkEncodeMeasure.Begin();
                    var payload = _deltaCodec.Encode(delta);
                    _telemetry.RecordEncode(
                        RuntimeNetworkStreamKey.ReliableStore,
                        encodeMeasure.Complete());
                    var transportEnvelope = new RuntimeReliableDeltaTransportEnvelope(
                        connection.Handshake.SessionId,
                        storeState.Reference,
                        connectionStore.Replication.BaselineId,
                        delta.DeliverySequence,
                        delta.FromRevision,
                        delta.ToRevision,
                        payload,
                        delta.Kind);
                    if (!_output.ReliableDeltaFitsTransport(transportEnvelope))
                    {
                        BeginBaseline(connectionId, connection, storeState, connectionStore);
                        continue;
                    }

                    var enqueue = connectionStore.Replication.TryEnqueueDelta(
                        delta.FromRevision,
                        delta.ToRevision,
                        payload,
                        enters,
                        leaves,
                        out var envelope);
                    if (enqueue != RuntimeConnectionDeltaEnqueueResult.Enqueued)
                    {
                        BeginBaseline(connectionId, connection, storeState, connectionStore);
                        continue;
                    }

                    if (envelope.DeliverySequence != delta.DeliverySequence)
                        throw new InvalidOperationException("Reliable queue allocated an unexpected delivery sequence.");
                    _telemetry.ObserveConnectionStore(connectionId, connectionStore.Replication);
                    SendDelta(connectionId, connection, storeState.Reference, envelope);
                }
                catch (Exception exception)
                {
                    try
                    {
                        BeginBaseline(connectionId, connection, storeState, connectionStore);
                    }
                    catch (Exception baselineException)
                    {
                        _output.Reject(
                            connectionId,
                            RuntimeProtocolRejectCode.InvalidEnvelope,
                            $"Delta projection failed: {exception.Message}; rebaseline failed: {baselineException.Message}");
                        ConnectionInvalidated?.Invoke(connectionId);
                    }
                }
            }
        }

        private void OnJournalEntryRecorded(RuntimeCommandJournalEntry entry)
        {
            if (_disposed || _sessionInvalidated)
            {
                return;
            }

            foreach (var pair in _connections)
            {
                var connection = pair.Value;
                if (connection.Handshake?.CanCreateReplica != true
                    || !connection.CheckpointBoundary.HasValue)
                {
                    continue;
                }

                try
                {
                    if (connection.JournalSendCursor >= entry.Sequence)
                    {
                        continue;
                    }
                    if (connection.JournalSendCursor + 1 == entry.Sequence)
                    {
                        var visibleEntries =
                            _context.JournalSubscriptionScope.Covers(
                                entry.Scope)
                                ? new[] { entry }
                                : Array.Empty<RuntimeCommandJournalEntry>();
                        SendJournalBatch(
                            pair.Key,
                            connection,
                            connection.CheckpointBoundary.Value,
                            connection.JournalSendCursor,
                            entry.Sequence,
                            visibleEntries,
                            force: true,
                            completesCatchup: false);
                        continue;
                    }

                    SendJournalCatchup(
                        pair.Key,
                        connection,
                        forceMarker: false);
                }
                catch (Exception exception)
                {
                    _output.Reject(
                        pair.Key,
                        RuntimeProtocolRejectCode.InvalidEnvelope,
                        $"Journal catch-up failed: {exception.Message}");
                    ConnectionInvalidated?.Invoke(pair.Key);
                }
            }
        }

        private void OnStoreLifecycleChanged(RuntimeStoreLifecycleChange change)
        {
            if (_disposed || _sessionInvalidated || change.Realm != StoreRealm.Server)
                return;

            var isRequiredStore = false;
            foreach (var storeReference in _stores.Keys)
            {
                if (storeReference.StoreId.Equals(change.StoreId))
                {
                    isRequiredStore = true;
                    break;
                }
            }
            if (!isRequiredStore)
                return;

            _sessionInvalidated = true;
            var connectionIds = new List<int>(_connections.Keys);
            for (var i = 0; i < connectionIds.Count; i++)
            {
                var connectionId = connectionIds[i];
                _output.Reject(
                    connectionId,
                    RuntimeProtocolRejectCode.InvalidStore,
                    $"Required store '{change.StoreId}' changed lifecycle after the immutable session manifest was frozen.");
                ConnectionInvalidated?.Invoke(connectionId);
            }
        }

        private void BeginBaseline(
            int connectionId,
            ConnectionState connection,
            StoreState storeState,
            ConnectionStoreState connectionStore)
        {
            SynchronizeObservedRevision(storeState, connectionStore);
            var nextBaselineId = checked(connectionStore.Replication.BaselineId + 1);
            var stagedShadow = new RuntimeConnectionStoreShadow();
            var baseline = _projection.BuildBaseline(
                storeState.Store,
                connectionId,
                nextBaselineId,
                stagedShadow,
                out var membership);
            var encodeMeasure = RuntimeNetworkEncodeMeasure.Begin();
            var payload = _baselineCodec.Encode(baseline);
            _telemetry.RecordEncode(
                RuntimeNetworkStreamKey.Baseline,
                encodeMeasure.Complete());
            var transfer = connectionStore.Replication.BeginBaseline(
                baseline.StoreRevision,
                payload,
                membership);
            _telemetry.RecordBaselineSize(payload.Length);
            if (transfer.BaselineId != baseline.BaselineId)
                throw new InvalidOperationException("Connection baseline state allocated an unexpected baseline id.");
            connectionStore.Shadow.ReplaceWith(stagedShadow);
            _telemetry.ObserveConnectionStore(connectionId, connectionStore.Replication);
            BaselineStarted?.Invoke(connectionId, storeState.Reference, connectionStore.Replication);
            SendBaselineChunks(connectionId, connection, transfer);
        }

        private void SynchronizeObservedRevision(StoreState storeState, ConnectionStoreState connectionStore)
        {
            if (connectionStore.Replication.StoreRevision == storeState.Store.StoreRevision)
                return;

            var read = storeState.Journal.ReadAfter(connectionStore.Replication.StoreRevision);
            if (read.RequiresBaseline)
                throw new InvalidOperationException(
                    $"Store '{storeState.Reference}' revision journal cannot bridge {connectionStore.Replication.StoreRevision} to {storeState.Store.StoreRevision}.");
            for (var i = 0; i < read.Revisions.Count; i++)
            {
                connectionStore.Replication.ObserveFilteredRevision(read.Revisions[i].StoreRevision);
            }
        }

        private void SendBaselineChunks(int connectionId, ConnectionState connection, RuntimeActiveBaselineTransfer transfer)
        {
            RuntimeCheckpointBoundary? checkpointBoundary = null;
            if (connection.CheckpointBoundary.HasValue
                && _context.JournalSubscriptionScope != null
                && _context.JournalSubscriptionScope.Contains(
                    transfer.Store.StoreId))
            {
                checkpointBoundary =
                    connection.CheckpointBoundary;
            }

            var chunks = RuntimeBaselineChunker.Split(
                connection.Handshake.SessionId,
                transfer.Store,
                transfer.BaselineId,
                transfer.DeliverySequence,
                transfer.StoreRevision,
                transfer.CopyPayload(),
                checkpointBoundary);
            for (var i = 0; i < chunks.Count; i++)
            {
                _output.BaselineChunk(connectionId, chunks[i]);
                _telemetry.RecordSent(
                    RuntimeNetworkStreamKey.Baseline,
                    chunks[i].Payload?.Length ?? 0);
            }
        }

        private void SendJournalCatchup(
            int connectionId,
            ConnectionState connection,
            bool forceMarker)
        {
            var boundary = connection.CheckpointBoundary
                           ?? throw new InvalidOperationException(
                               "Connection has no checkpoint journal boundary.");
            var read = _context.CommandsBus.Journal.ReadAfter(
                boundary.GroupId,
                connection.JournalSendCursor);
            if (!read.Succeeded)
            {
                throw new InvalidOperationException(
                    $"Journal read after cursor {connection.JournalSendCursor} failed: {read.Status}.");
            }

            var fromCursor = read.RequestedCursor;
            var batchEntries = new List<RuntimeCommandJournalEntry>();
            var batchBytes = 0;
            for (var i = 0; i < read.Entries.Count; i++)
            {
                var entry = read.Entries[i];
                var entryBytes = entry.EncodedCommand.PayloadBytes;
                if (entryBytes > RuntimeProtocol.MAX_JOURNAL_BATCH_BYTES)
                {
                    throw new InvalidOperationException(
                        $"Journal entry {entry.Sequence} is {entryBytes} bytes; network batch limit is {RuntimeProtocol.MAX_JOURNAL_BATCH_BYTES}.");
                }

                var wouldOverflow = batchEntries.Count >= RuntimeProtocol.MAX_JOURNAL_BATCH_ENTRIES
                                    || batchBytes + entryBytes > RuntimeProtocol.MAX_JOURNAL_BATCH_BYTES;
                if (wouldOverflow)
                {
                    SendJournalBatch(
                        connectionId,
                        connection,
                        boundary,
                        fromCursor,
                        batchEntries[batchEntries.Count - 1].Sequence,
                        batchEntries,
                        completesCatchup: false);
                    fromCursor = batchEntries[batchEntries.Count - 1].Sequence;
                    batchEntries.Clear();
                    batchBytes = 0;
                }

                batchEntries.Add(entry);
                batchBytes += entryBytes;
            }

            SendJournalBatch(
                connectionId,
                connection,
                boundary,
                fromCursor,
                read.ScannedThroughCursor,
                batchEntries,
                forceMarker,
                completesCatchup: forceMarker);
        }

        private void SendJournalBatch(
            int connectionId,
            ConnectionState connection,
            in RuntimeCheckpointBoundary boundary,
            ulong fromCursor,
            ulong scannedThroughCursor,
            IReadOnlyList<RuntimeCommandJournalEntry> entries,
            bool force = false,
            bool completesCatchup = false)
        {
            if (!force
                && scannedThroughCursor == fromCursor
                && entries.Count == 0)
            {
                return;
            }

            var batch = new RuntimeCommandJournalBatch(
                connection.Handshake.SessionId,
                boundary.GroupId,
                boundary.CheckpointHash,
                fromCursor,
                scannedThroughCursor,
                entries,
                completesCatchup);
            _output.JournalBatch(connectionId, batch);
            connection.JournalSendCursor = scannedThroughCursor;
        }

        private void SendDelta(
            int connectionId,
            ConnectionState connection,
            in NetStoreRef store,
            RuntimeReliableEnvelope envelope)
        {
            _output.Delta(connectionId, new RuntimeClientDeltaEnvelope(
                connection.Handshake.SessionId,
                store,
                envelope.BaselineId,
                envelope.DeliverySequence,
                envelope.FromRevision,
                envelope.ToRevision,
                envelope.Payload,
                envelope.Kind));
            _telemetry.RecordSent(
                RuntimeNetworkStreamKey.ReliableStore,
                envelope.PayloadBytes);
        }

        protected virtual byte[] EncodeStateStreamFrame(RuntimeStateStreamFrame frame)
        {
            return _stateStreamCodec.Encode(frame);
        }

        protected virtual RuntimeStateStreamFrame DecodeStateStreamPayload(byte[] payload)
        {
            return _stateStreamCodec.Decode(payload);
        }

        private RuntimeSessionHandshakeResult ResolveStateStreamSend(
            ConnectionState connection,
            ulong sessionId,
            in NetStoreRef store,
            uint streamTypeId,
            out ConnectionStoreState storeState,
            out RuntimeStateStreamProfile profile)
        {
            storeState = null;
            profile = null;
            var authorization = Authorize(connection, sessionId, store);
            if (!authorization.Accepted)
                return authorization;
            if (!connection.Stores.TryGetValue(store, out storeState))
            {
                return RuntimeSessionHandshakeResult.Reject(
                    RuntimeProtocolRejectCode.InvalidStore,
                    $"State stream references unknown store '{store}'.");
            }
            if (!_context.StateStreamProfiles.TryGet(streamTypeId, out profile))
            {
                return RuntimeSessionHandshakeResult.Reject(
                    RuntimeProtocolRejectCode.InvalidEnvelope,
                    $"State stream type id {streamTypeId} is not registered.");
            }
            return RuntimeSessionHandshakeResult.Success();
        }

        private static RuntimeSessionHandshakeResult ValidateStateStreamFrame(
            in NetStoreRef store,
            ConnectionStoreState storeState,
            RuntimeStateStreamProfile profile,
            RuntimeStateStreamFrame frame)
        {
            if (frame == null || frame.Store != store || frame.StreamTypeId != profile.StreamTypeId)
            {
                return RuntimeSessionHandshakeResult.Reject(
                    RuntimeProtocolRejectCode.InvalidEnvelope,
                    "State stream frame store or type does not match its send context.");
            }
            if (profile.Membership == RuntimeStateStreamMembership.AcknowledgedRuntimeObject
                && storeState.Replication.PendingReliableCount != 0)
            {
                return RuntimeSessionHandshakeResult.Reject(
                    RuntimeProtocolRejectCode.InvalidEnvelope,
                    "RuntimeObject state stream is blocked while reliable membership/component changes await ACK.");
            }

            for (var i = 0; i < frame.Samples.Count; i++)
            {
                var sample = frame.Samples[i];
                if (profile.Membership == RuntimeStateStreamMembership.AcknowledgedRuntimeObject)
                {
                    var objectReference = new NetObjectRef(store, sample.Key.Value);
                    if (!objectReference.IsValid
                        || !storeState.Replication.IsProjected(objectReference)
                        || !storeState.Replication.IsAcknowledged(objectReference))
                    {
                        return RuntimeSessionHandshakeResult.Reject(
                            RuntimeProtocolRejectCode.InvalidEnvelope,
                            $"State stream key '{sample.Key}' is not in projected and ACKed RuntimeObject membership.");
                    }
                }

                try
                {
                    profile.ValidatePackedSample(sample);
                }
                catch (Exception exception) when (exception is FormatException
                                                  || exception is ArgumentException
                                                  || exception is InvalidOperationException
                                                  || exception is OverflowException)
                {
                    return RuntimeSessionHandshakeResult.Reject(
                        RuntimeProtocolRejectCode.InvalidEnvelope,
                        $"State stream '{profile.StreamName}' sample '{sample.Key}' is invalid: {exception.Message}");
                }
            }
            return RuntimeSessionHandshakeResult.Success();
        }

        private RuntimeSessionHandshakeResult PublishStateStream(
            int connectionId,
            in RtStateStreamFrameData value)
        {
            _output.StateStream(connectionId, value);
            _telemetry.RecordSent(
                RuntimeNetworkStreamKey.HotState(value.StreamTypeId),
                value.Payload.Length);
            return RuntimeSessionHandshakeResult.Success();
        }

        private RuntimeSessionHandshakeResult Authorize(
            ConnectionState connection,
            ulong sessionId,
            in NetStoreRef store)
        {
            if (connection.Handshake == null)
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.SessionNotReady, "Connection has no protocol handshake.");
            return connection.Handshake.AuthorizeStoreAccess(sessionId, store);
        }

        private bool AllowsGeneration(ConnectionState connection, uint generation)
        {
            if (connection.Handshake?.CanCreateReplica != true || generation == 0)
                return false;
            var stores = connection.Handshake.Manifest.Stores;
            for (var i = 0; i < stores.Count; i++)
            {
                if (stores[i].StoreGeneration == generation)
                    return true;
            }
            return false;
        }

        private static bool IsAcknowledged(ConnectionState connection, in NetObjectRef target)
        {
            return connection.Stores.TryGetValue(target.Store, out var store)
                   && store.Replication.IsAcknowledged(target);
        }

        private RuntimeSessionHandshakeResult Reject(
            int connectionId,
            RuntimeProtocolRejectCode code,
            string detail)
        {
            _output.Reject(connectionId, code, detail);
            return RuntimeSessionHandshakeResult.Reject(code, detail);
        }

        private void RejectAll(RuntimeProtocolRejectCode code, string detail)
        {
            foreach (var connectionId in _connections.Keys)
            {
                _output.Reject(connectionId, code, detail);
            }
        }

        private ConnectionState GetConnection(int connectionId)
        {
            if (_connections.TryGetValue(connectionId, out var connection))
                return connection;
            throw new KeyNotFoundException($"Protocol connection {connectionId} is not registered.");
        }

        private ulong TakeSessionId()
        {
            if (_nextSessionId == 0 || _nextSessionId == ulong.MaxValue)
                throw new InvalidOperationException("Protocol session id range is exhausted.");
            return _nextSessionId++;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RuntimeProtocolServerCoordinator));
            if (_sessionInvalidated)
                throw new InvalidOperationException("Protocol session was invalidated by required store lifecycle change.");
        }

        private class StoreState
        {
            public readonly NetStoreRef Reference;
            public readonly RuntimeStore Store;
            public readonly RuntimeStoreRevisionJournal Journal;
            public Action<RuntimeStoreCommittedBatch> BatchHandler;

            public StoreState(NetStoreRef reference, RuntimeStore store, RuntimeStoreRevisionJournal journal)
            {
                Reference = reference;
                Store = store;
                Journal = journal;
            }
        }

        private class ConnectionState
        {
            public RuntimeSessionServerHandshake Handshake;
            public readonly Dictionary<NetStoreRef, ConnectionStoreState> Stores = new();
            public RuntimeCheckpointBoundary? CheckpointBoundary;
            public ulong JournalSendCursor;
        }

        private class ConnectionStoreState
        {
            public readonly RuntimeConnectionStoreReplicationState Replication;
            public readonly RuntimeConnectionStoreShadow Shadow = new();

            public ConnectionStoreState(RuntimeConnectionStoreReplicationState replication)
            {
                Replication = replication;
            }
        }
    }

    public readonly struct RtStoreAckData
    {
        public readonly ulong SessionId;
        public readonly NetStoreRef Store;
        public readonly ulong BaselineId;
        public readonly ulong DeliverySequence;

        public RtStoreAckData(ulong sessionId, NetStoreRef store, ulong baselineId, ulong deliverySequence)
        {
            SessionId = sessionId;
            Store = store;
            BaselineId = baselineId;
            DeliverySequence = deliverySequence;
        }
    }

    public readonly struct RuntimeReadyConnection
    {
        public readonly int ConnectionId;
        public readonly ulong SessionId;

        public RuntimeReadyConnection(int connectionId, ulong sessionId)
        {
            ConnectionId = connectionId;
            SessionId = sessionId;
        }
    }

    public readonly struct RtStoreResyncData
    {
        public readonly ulong SessionId;
        public readonly NetStoreRef Store;
        public readonly ulong BaselineId;
        public readonly ulong ExpectedDeliverySequence;

        public RtStoreResyncData(ulong sessionId, NetStoreRef store, ulong baselineId, ulong expectedDeliverySequence)
        {
            SessionId = sessionId;
            Store = store;
            BaselineId = baselineId;
            ExpectedDeliverySequence = expectedDeliverySequence;
        }
    }

    public readonly struct RtCommandJournalResyncData
    {
        public readonly ulong SessionId;
        public readonly string CheckpointGroupId;
        public readonly string CheckpointHash;
        public readonly ulong ExpectedCursor;
        public readonly bool ForceCheckpointBaseline;

        public RtCommandJournalResyncData(
            ulong sessionId,
            string checkpointGroupId,
            string checkpointHash,
            ulong expectedCursor,
            bool forceCheckpointBaseline = false)
        {
            SessionId = sessionId;
            CheckpointGroupId = checkpointGroupId;
            CheckpointHash = checkpointHash;
            ExpectedCursor = expectedCursor;
            ForceCheckpointBaseline = forceCheckpointBaseline;
        }
    }
}
