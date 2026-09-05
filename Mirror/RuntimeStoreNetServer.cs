#if MIRROR
using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoUnityExtensions;
using Mirror;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror
{
    public class RuntimeStoreNetServer : IDisposable
    {
        private readonly RuntimeProtocolServerCoordinator _coordinator;
        private double _lastStateStreamTickTime;

        public event Action<double> StateStreamSendTick;
        public bool IsSessionInvalidated => _coordinator.IsSessionInvalidated;
        public RuntimeNetworkTelemetry Telemetry => _coordinator.Telemetry;

        public event Action<int, ulong> ConnectionReady
        {
            add => _coordinator.ConnectionReady += value;
            remove => _coordinator.ConnectionReady -= value;
        }

        public event Action<int, NetStoreRef, RuntimeConnectionStoreReplicationState> BaselineStarted
        {
            add => _coordinator.BaselineStarted += value;
            remove => _coordinator.BaselineStarted -= value;
        }

        public event Action<int> ConnectionRemoved
        {
            add => _coordinator.ConnectionRemoved += value;
            remove => _coordinator.ConnectionRemoved -= value;
        }

        public RuntimeStoreNetServer(RuntimeProtocolContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            _coordinator = new RuntimeProtocolServerCoordinator(
                context,
                new RuntimeProtocolServerOutput(
                    SendManifest,
                    SendReject,
                    SendBaselineChunk,
                    SendDelta,
                    SendCommandResult,
                    RuntimeReliableDeltaTransportBudget.Fits,
                    RuntimeBaselineChunkTransportBudget
                        .GetPayloadCapacity,
                    SendStateStreamMessage,
                    SendJournalBatch,
                    SendCheckpointChunk));
            _coordinator.ConnectionInvalidated += DisconnectInvalidatedConnection;

            NetworkServer.RegisterHandler<RtSessionHello>(OnHello, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtSessionReady>(OnReady, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreAck>(OnAck, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreResyncRequest>(OnResync, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtCommandJournalResyncRequest>(
                OnJournalResync,
                requireAuthentication: true);
            NetworkServer.RegisterHandler<RtCommandEnvelope>(OnCommand, requireAuthentication: true);
            _lastStateStreamTickTime = NetworkTime.localTime;
            CoroutineParent.AddLateUpdater(this, TickStateStreams, RuntimeStore.UPDATE_ORDER + 3);
            Trace("Endpoint created; Mirror handlers registered.");
        }

        public void OnConnectionConnected(int connectionId)
        {
            Trace($"Connection connected id={connectionId}; adding protocol state.");
            _coordinator.AddConnection(connectionId);
        }

        public void OnConnectionDisconnected(int connectionId)
        {
            Trace($"Connection disconnected id={connectionId}; removing protocol state.");
            _coordinator.RemoveConnection(connectionId);
        }

        public RuntimeSessionHandshakeResult SendStateStream(int connectionId, RuntimeStateStreamFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            return _coordinator.SendStateStream(connectionId, frame);
        }

        public RuntimeNetworkTelemetrySnapshot CaptureTelemetry(bool resetWindow = false)
        {
            return _coordinator.CaptureTelemetry(resetWindow);
        }

        public RuntimeNetworkTelemetrySnapshot CaptureTelemetry(
            double windowSeconds,
            bool resetWindow = false)
        {
            return _coordinator.CaptureTelemetry(windowSeconds, resetWindow);
        }

        public void RequestBaseline(int connectionId, in NetStoreRef store)
        {
            Trace($"Baseline requested connection={connectionId}, store={store}.");
            _coordinator.RequestBaseline(connectionId, store);
        }

        public RuntimeInterestRefreshResult RefreshInterest(
            int connectionId,
            in NetStoreRef store)
        {
            return _coordinator.RefreshInterest(connectionId, store);
        }

        public int RefreshInterest(int connectionId)
        {
            return _coordinator.RefreshInterest(connectionId);
        }

        public int RefreshInterestAll()
        {
            return _coordinator.RefreshInterestAll();
        }

        public bool TryGetConnectionStoreState(
            int connectionId,
            in NetStoreRef store,
            out RuntimeConnectionStoreReplicationState state)
        {
            return _coordinator.TryGetConnectionStoreState(connectionId, store, out state);
        }

        public IReadOnlyList<RuntimeReadyConnection> GetReadyConnections()
        {
            return _coordinator.GetReadyConnections();
        }

        public void Dispose()
        {
            Trace($"Dispose. sessionInvalidated={IsSessionInvalidated}, readyConnections={_coordinator.GetReadyConnections().Count}.");
            NetworkServer.UnregisterHandler<RtSessionHello>();
            NetworkServer.UnregisterHandler<RtSessionReady>();
            NetworkServer.UnregisterHandler<RtStoreAck>();
            NetworkServer.UnregisterHandler<RtStoreResyncRequest>();
            NetworkServer.UnregisterHandler<RtCommandJournalResyncRequest>();
            NetworkServer.UnregisterHandler<RtCommandEnvelope>();
            CoroutineParent.RemoveLateUpdater(this);
            _coordinator.ConnectionInvalidated -= DisconnectInvalidatedConnection;
            _coordinator.Dispose();
        }

        private void TickStateStreams()
        {
            if (_coordinator.IsSessionInvalidated)
                return;
            var now = NetworkTime.localTime;
            if (now - _lastStateStreamTickTime < RuntimeStateStreamProtocol.SEND_INTERVAL_SECONDS)
                return;
            _lastStateStreamTickTime = now;
            StateStreamSendTick?.Invoke(now);
        }

        private void OnHello(NetworkConnectionToClient connection, RtSessionHello message)
        {
            Trace(
                $"RECV Hello connection={connection.connectionId}, nonce={message.ClientNonce}, "
                + $"protocol={message.Descriptor.ProtocolVersion}, build='{message.Descriptor.BuildId}', "
                + $"schema='{message.Descriptor.RuntimeSchemaHash}', assets='{message.Descriptor.AssetCatalogHash}'.");
            _coordinator.ReceiveHello(connection.connectionId, message.Descriptor, message.ClientNonce);
        }

        private void OnReady(NetworkConnectionToClient connection, RtSessionReady message)
        {
            Trace($"RECV Ready connection={connection.connectionId}, session={message.SessionId}.");
            _coordinator.ReceiveReady(connection.connectionId, message.SessionId);
        }

        private void OnAck(NetworkConnectionToClient connection, RtStoreAck message)
        {
            Trace(
                $"RECV Ack connection={connection.connectionId}, session={message.SessionId}, "
                + $"store={message.Store}, baseline={message.BaselineId}, delivery={message.DeliverySequence}.");
            _coordinator.ReceiveAck(connection.connectionId, new RtStoreAckData(
                message.SessionId,
                message.Store,
                message.BaselineId,
                message.DeliverySequence));
        }

        private void OnResync(NetworkConnectionToClient connection, RtStoreResyncRequest message)
        {
            Trace(
                $"RECV Resync connection={connection.connectionId}, session={message.SessionId}, "
                + $"store={message.Store}, baseline={message.BaselineId}, expectedDelivery={message.ExpectedDeliverySequence}.");
            _coordinator.ReceiveResync(connection.connectionId, new RtStoreResyncData(
                message.SessionId,
                message.Store,
                message.BaselineId,
                message.ExpectedDeliverySequence));
        }

        private void OnCommand(NetworkConnectionToClient connection, RtCommandEnvelope message)
        {
            Trace(
                $"RECV Command connection={connection.connectionId}, type={message.Value.CommandTypeId}, "
                + $"sequence={message.Value.ClientSequence}, generation={message.Value.ExpectedStoreGeneration}, "
                + $"payload={message.Value.Payload?.Length ?? 0}.");
            _coordinator.ReceiveCommand(connection.connectionId, message.Value, NetworkTime.localTime);
        }

        private void OnJournalResync(
            NetworkConnectionToClient connection,
            RtCommandJournalResyncRequest message)
        {
            Trace(
                $"RECV JournalResync connection={connection.connectionId}, session={message.SessionId}, "
                + $"group='{message.CheckpointGroupId}', cursor={message.ExpectedCursor}, "
                + $"fullBaseline={message.ForceCheckpointBaseline}.");
            _coordinator.ReceiveJournalResync(
                connection.connectionId,
                new RtCommandJournalResyncData(
                    message.SessionId,
                    message.CheckpointGroupId,
                    message.CheckpointHash,
                    message.ExpectedCursor,
                    message.ForceCheckpointBaseline));
        }

        private static void SendManifest(int connectionId, RuntimeSessionManifestSnapshot manifest)
        {
            var message = manifest.ToWireManifest();
            message.AssignedConnectionId = connectionId;
            Trace(
                $"SEND Manifest connection={connectionId}, session={message.SessionId}, "
                + $"assets={message.Assets?.Length ?? 0}, stores={message.Stores?.Length ?? 0}.");
            RequireConnection(connectionId).Send(message, Channels.Reliable);
        }

        private static void SendReject(int connectionId, RuntimeProtocolRejectCode code, string detail)
        {
            Trace($"SEND Reject connection={connectionId}, code={code}, detail='{detail}'.");
            var connection = RequireConnection(connectionId);
            connection.Send(new RtProtocolReject
            {
                Code = code,
                Detail = detail,
            }, Channels.Reliable);
            connection.Disconnect();
        }

        private static void SendBaselineChunk(int connectionId, RuntimeBaselineChunk chunk)
        {
            Trace(
                $"SEND BaselineChunk connection={connectionId}, session={chunk.SessionId}, store={chunk.Store}, "
                + $"baseline={chunk.BaselineId}, delivery={chunk.DeliverySequence}, "
                + $"chunk={chunk.ChunkIndex + 1}/{chunk.ChunkCount}, payload={chunk.Payload?.Length ?? 0}, "
                + $"logical={chunk.LogicalLength}.");
            RequireConnection(connectionId).Send(new RtBaselineChunk { Value = chunk }, Channels.Reliable);
        }

        private static void SendCheckpointChunk(
            int connectionId,
            RuntimeCheckpointChunk chunk)
        {
            Trace(
                $"SEND CheckpointChunk connection={connectionId}, session={chunk.SessionId}, "
                + $"group='{chunk.CheckpointGroupId}', section={chunk.SectionIndex + 1}/{chunk.SectionCount}, "
                + $"page={chunk.PageIndex + 1}/{chunk.PageCount}, payload={chunk.Payload?.Length ?? 0}.");
            RequireConnection(connectionId).Send(
                new RtCheckpointChunk { Value = chunk },
                Channels.Reliable);
        }

        private static void SendDelta(int connectionId, RuntimeClientDeltaEnvelope delta)
        {
            var transportEnvelope = new RuntimeReliableDeltaTransportEnvelope(
                delta.SessionId,
                delta.Store,
                delta.BaselineId,
                delta.DeliverySequence,
                delta.FromRevision,
                delta.ToRevision,
                delta.Payload,
                delta.Kind);
            if (!RuntimeReliableDeltaTransportBudget.Fits(transportEnvelope))
                throw new InvalidOperationException("RtStoreDelta exceeded its reliable transport budget after enqueue.");

            Trace(
                $"SEND Delta connection={connectionId}, session={delta.SessionId}, store={delta.Store}, "
                + $"baseline={delta.BaselineId}, delivery={delta.DeliverySequence}, "
                + $"revisions={delta.FromRevision}->{delta.ToRevision}, payload={delta.Payload?.Length ?? 0}.");
            RequireConnection(connectionId).Send(new RtStoreDelta
            {
                SessionId = delta.SessionId,
                Store = delta.Store,
                BaselineId = delta.BaselineId,
                DeliverySequence = delta.DeliverySequence,
                FromRevision = delta.FromRevision,
                ToRevision = delta.ToRevision,
                Kind = delta.Kind,
                Payload = delta.Payload,
            }, Channels.Reliable);
        }

        private static void SendCommandResult(int connectionId, RuntimeCommandResult result)
        {
            Trace(
                $"SEND CommandResult connection={connectionId}, sequence={result.ClientSequence}, "
                + $"reject={result.RejectCode}.");
            RequireConnection(connectionId).Send(new RtCommandResult
            {
                ClientSequence = result.ClientSequence,
                RejectCode = result.RejectCode,
            }, Channels.Reliable);
        }

        private static void SendStateStreamMessage(int connectionId, RtStateStreamFrameData value)
        {
            Trace(
                $"SEND StateStream connection={connectionId}, session={value.SessionId}, store={value.Store}, "
                + $"type={value.StreamTypeId}, sequence={value.Sequence}, tick={value.SimulationTick}, "
                + $"payload={value.Payload?.Length ?? 0}.");
            RequireConnection(connectionId).Send(new RtStateStreamFrame
            {
                SessionId = value.SessionId,
                Store = value.Store,
                StreamTypeId = value.StreamTypeId,
                Sequence = value.Sequence,
                SimulationTick = value.SimulationTick,
                Payload = value.Payload,
            }, Channels.Unreliable);
        }

        private static void SendJournalBatch(
            int connectionId,
            RuntimeCommandJournalBatch batch)
        {
            Trace(
                $"SEND JournalBatch connection={connectionId}, session={batch.SessionId}, "
                + $"fromCursor={batch.FromCursor}, entries={batch.Entries?.Count ?? 0}.");
            RequireConnection(connectionId).Send(
                RuntimeCommandJournalWireCodec.ToWire(batch),
                Channels.Reliable);
        }

        private static NetworkConnectionToClient RequireConnection(int connectionId)
        {
            if (NetworkServer.connections.TryGetValue(connectionId, out var connection) && connection != null)
                return connection;
            throw new InvalidOperationException($"Mirror connection {connectionId} is not active.");
        }

        private static void DisconnectInvalidatedConnection(int connectionId)
        {
            Trace($"Disconnecting invalidated protocol connection={connectionId}.");
            if (NetworkServer.connections.TryGetValue(connectionId, out var connection) && connection != null)
                connection.Disconnect();
        }

        private static void Trace(string message)
        {
            Debug.Log(
                $"[NETTRACE][RuntimeServer][t={Time.realtimeSinceStartupAsDouble:F3}] {message}");
        }
    }
}
#endif
