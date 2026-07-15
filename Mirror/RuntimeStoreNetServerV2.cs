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
        private double _lastMotionTickTime;

        public event Action<double> MotionSendTick;
        public bool IsSessionInvalidated => _coordinator.IsSessionInvalidated;

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
                    SendMotionMessage));
            _coordinator.ConnectionReady += CaptureReadySession;
            _coordinator.ConnectionInvalidated += DisconnectInvalidatedConnection;

            NetworkServer.RegisterHandler<RtSessionHello>(OnHello, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtSessionReady>(OnReady, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreAck>(OnAck, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreResyncRequest>(OnResync, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtCommandEnvelope>(OnCommand, requireAuthentication: true);
            _lastMotionTickTime = NetworkTime.localTime;
            CoroutineParent.AddLateUpdater(this, TickMotion, RuntimeStore.UPDATE_ORDER + 3);
        }

        public void OnConnectionConnected(int connectionId)
        {
            _coordinator.AddConnection(connectionId);
        }

        public void OnConnectionDisconnected(int connectionId)
        {
            _coordinator.RemoveConnection(connectionId);
            _sessionByConnection.Remove(connectionId);
        }

        public RuntimeSessionHandshakeResult SendMotion(int connectionId, RuntimeMotionFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            var sessionId = TakeSessionId(connectionId);
            var payload = new RuntimeMotionFrameCodec().Encode(frame);
            return _coordinator.SendMotion(connectionId, new RtMotionStateData(
                sessionId,
                frame.Store,
                frame.SimulationTick,
                payload));
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
            _coordinator.ConnectionReady -= CaptureReadySession;
            _coordinator.ConnectionInvalidated -= DisconnectInvalidatedConnection;
            _sessionByConnection.Clear();
            _coordinator.Dispose();
        }

        private void TickMotion()
        {
            if (_coordinator.IsSessionInvalidated)
                return;
            var now = NetworkTime.localTime;
            if (now - _lastMotionTickTime < RuntimeMotionProtocol.SEND_INTERVAL_SECONDS)
                return;
            _lastMotionTickTime = now;
            MotionSendTick?.Invoke(now);
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

        private static void SendMotionMessage(int connectionId, RtMotionStateData value)
        {
            RequireConnection(connectionId).Send(new RtMotionState
            {
                SessionId = value.SessionId,
                Store = value.Store,
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

        private ulong TakeSessionId(int connectionId)
        {
            // The coordinator validates the session on SendMotion. The id is
            // captured through the same ready callback used by game-side
            // ownership setup.
            if (!_sessionByConnection.TryGetValue(connectionId, out var sessionId))
                throw new InvalidOperationException($"Connection {connectionId} has not completed protocol-v2 readiness.");
            return sessionId;
        }

        private readonly System.Collections.Generic.Dictionary<int, ulong> _sessionByConnection = new();

        private void CaptureReadySession(int connectionId, ulong sessionId)
        {
            _sessionByConnection[connectionId] = sessionId;
        }

        private static void DisconnectInvalidatedConnection(int connectionId)
        {
            if (NetworkServer.connections.TryGetValue(connectionId, out var connection) && connection != null)
                connection.Disconnect();
        }
    }
}
#endif
