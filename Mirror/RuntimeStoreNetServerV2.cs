#if MIRROR
using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.Mirror.V2;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoUnityExtensions;
using Mirror;

namespace DingoGameObjectsCMS.Mirror
{
    public class RuntimeStoreNetServerV2 : IDisposable
    {
        private readonly RuntimeProtocolV2ServerCoordinator _coordinator;
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

        public RuntimeStoreNetServerV2(RuntimeProtocolV2Context context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            _coordinator = new RuntimeProtocolV2ServerCoordinator(
                context,
                new RuntimeProtocolV2ServerOutput(
                    SendManifest,
                    SendReject,
                    SendBaselineChunk,
                    SendDelta,
                    SendCommandResult,
                    RuntimeReliableDeltaTransportBudget.Fits,
                    SendStateStreamMessage));
            _coordinator.ConnectionInvalidated += DisconnectInvalidatedConnection;

            NetworkServer.RegisterHandler<RtSessionHello>(OnHello, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtSessionReady>(OnReady, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreAck>(OnAck, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreResyncRequest>(OnResync, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtCommandEnvelope>(OnCommand, requireAuthentication: true);
            _lastStateStreamTickTime = NetworkTime.localTime;
            CoroutineParent.AddLateUpdater(this, TickStateStreams, RuntimeStore.UPDATE_ORDER + 3);
        }

        public void OnConnectionConnected(int connectionId)
        {
            _coordinator.AddConnection(connectionId);
        }

        public void OnConnectionDisconnected(int connectionId)
        {
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
            NetworkServer.UnregisterHandler<RtSessionHello>();
            NetworkServer.UnregisterHandler<RtSessionReady>();
            NetworkServer.UnregisterHandler<RtStoreAck>();
            NetworkServer.UnregisterHandler<RtStoreResyncRequest>();
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
            _coordinator.ReceiveHello(connection.connectionId, message.Descriptor, message.ClientNonce);
        }

        private void OnReady(NetworkConnectionToClient connection, RtSessionReady message)
        {
            _coordinator.ReceiveReady(connection.connectionId, message.SessionId);
        }

        private void OnAck(NetworkConnectionToClient connection, RtStoreAck message)
        {
            _coordinator.ReceiveAck(connection.connectionId, new RtStoreAckData(
                message.SessionId,
                message.Store,
                message.BaselineId,
                message.DeliverySequence));
        }

        private void OnResync(NetworkConnectionToClient connection, RtStoreResyncRequest message)
        {
            _coordinator.ReceiveResync(connection.connectionId, new RtStoreResyncData(
                message.SessionId,
                message.Store,
                message.BaselineId,
                message.ExpectedDeliverySequence));
        }

        private void OnCommand(NetworkConnectionToClient connection, RtCommandEnvelope message)
        {
            _coordinator.ReceiveCommand(connection.connectionId, message.Value, NetworkTime.localTime);
        }

        private static void SendManifest(int connectionId, RuntimeSessionManifestSnapshot manifest)
        {
            RequireConnection(connectionId).Send(manifest.ToWireManifest(), Channels.Reliable);
        }

        private static void SendReject(int connectionId, RuntimeProtocolRejectCode code, string detail)
        {
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
            RequireConnection(connectionId).Send(new RtBaselineChunk { Value = chunk }, Channels.Reliable);
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
            RequireConnection(connectionId).Send(new RtCommandResult
            {
                ClientSequence = result.ClientSequence,
                RejectCode = result.RejectCode,
            }, Channels.Reliable);
        }

        private static void SendStateStreamMessage(int connectionId, RtStateStreamFrameData value)
        {
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

        private static NetworkConnectionToClient RequireConnection(int connectionId)
        {
            if (NetworkServer.connections.TryGetValue(connectionId, out var connection) && connection != null)
                return connection;
            throw new InvalidOperationException($"Mirror connection {connectionId} is not active.");
        }

        private static void DisconnectInvalidatedConnection(int connectionId)
        {
            if (NetworkServer.connections.TryGetValue(connectionId, out var connection) && connection != null)
                connection.Disconnect();
        }
    }
}
#endif
