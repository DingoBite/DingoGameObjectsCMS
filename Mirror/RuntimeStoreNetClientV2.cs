#if MIRROR
using System;
using DingoGameObjectsCMS.Mirror.V2;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoUnityExtensions;
using Mirror;

namespace DingoGameObjectsCMS.Mirror
{
    public class RuntimeStoreNetClientV2 : IDisposable
    {
        private const int TIMEOUT_TICK_ORDER = RuntimeStore.UPDATE_ORDER + 2;

        private readonly RuntimeProtocolV2ClientCoordinator _coordinator;
        private bool _tickScheduled;

        public event Action<RuntimeCommandResult> CommandResultReceived
        {
            add => _coordinator.CommandResultReceived += value;
            remove => _coordinator.CommandResultReceived -= value;
        }

        public event Action<RtMotionStateData> MotionStateReceived
        {
            add => _coordinator.MotionStateReceived += value;
            remove => _coordinator.MotionStateReceived -= value;
        }

        public event Action<bool> ReplicaReadyChanged
        {
            add => _coordinator.ReplicaReadyChanged += value;
            remove => _coordinator.ReplicaReadyChanged -= value;
        }

        public bool IsReplicaReady => _coordinator.IsReplicaReady;

        public RuntimeStoreNetClientV2(RuntimeProtocolV2Context context, ulong clientNonce)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            _coordinator = new RuntimeProtocolV2ClientCoordinator(
                context,
                new RuntimeProtocolV2ClientOutput(
                    SendHello,
                    SendReady,
                    SendReject,
                    SendAck,
                    SendResync,
                    SendCommand),
                clientNonce);

            NetworkClient.RegisterHandler<RtSessionManifest>(OnManifest);
            NetworkClient.RegisterHandler<RtProtocolReject>(OnReject);
            NetworkClient.RegisterHandler<RtBaselineChunk>(OnBaselineChunk);
            NetworkClient.RegisterHandler<RtStoreDelta>(OnDelta);
            NetworkClient.RegisterHandler<RtCommandResult>(OnCommandResult);
            NetworkClient.RegisterHandler<RtMotionState>(OnMotion);
        }

        public void BeginHandshake()
        {
            _coordinator.BeginHandshake();
            if (_tickScheduled)
                return;
            _tickScheduled = true;
            CoroutineParent.AddLateUpdater(this, Tick, TIMEOUT_TICK_ORDER);
        }

        public void Dispose()
        {
            NetworkClient.UnregisterHandler<RtSessionManifest>();
            NetworkClient.UnregisterHandler<RtProtocolReject>();
            NetworkClient.UnregisterHandler<RtBaselineChunk>();
            NetworkClient.UnregisterHandler<RtStoreDelta>();
            NetworkClient.UnregisterHandler<RtCommandResult>();
            NetworkClient.UnregisterHandler<RtMotionState>();
            if (_tickScheduled)
            {
                _tickScheduled = false;
                CoroutineParent.RemoveLateUpdater(this);
            }
            _coordinator.Dispose();
        }

        private void Tick()
        {
            _coordinator.Tick(NetworkTime.localTime);
        }

        private void OnManifest(RtSessionManifest message)
        {
            _coordinator.ReceiveManifest(
                message.SessionId,
                message.Descriptor,
                message.Assets,
                message.Stores);
        }

        private void OnReject(RtProtocolReject message)
        {
            _coordinator.ReceiveReject(message.Code, message.Detail);
            NetworkClient.Disconnect();
        }

        private void OnBaselineChunk(RtBaselineChunk message)
        {
            _coordinator.ReceiveBaselineChunk(message.Value, NetworkTime.localTime);
        }

        private void OnDelta(RtStoreDelta message)
        {
            _coordinator.ReceiveDelta(RuntimeClientDeltaEnvelope.FromWire(message), NetworkTime.localTime);
        }

        private void OnCommandResult(RtCommandResult message)
        {
            _coordinator.ReceiveCommandResult(new RuntimeCommandResult(
                message.ClientSequence,
                message.RejectCode));
        }

        private void OnMotion(RtMotionState message)
        {
            _coordinator.ReceiveMotion(new RtMotionStateData(
                message.SessionId,
                message.Store,
                message.SimulationTick,
                message.Payload));
        }

        private static void SendHello(RuntimeSessionDescriptor descriptor, ulong clientNonce)
        {
            NetworkClient.Send(new RtSessionHello
            {
                Descriptor = descriptor,
                ClientNonce = clientNonce,
            }, Channels.Reliable);
        }

        private static void SendReady(ulong sessionId)
        {
            NetworkClient.Send(new RtSessionReady { SessionId = sessionId }, Channels.Reliable);
        }

        private static void SendReject(RuntimeProtocolRejectCode code, string detail)
        {
            NetworkClient.Send(new RtProtocolReject
            {
                Code = code,
                Detail = detail,
            }, Channels.Reliable);
            NetworkClient.Disconnect();
        }

        private static void SendAck(RtStoreAckData value)
        {
            NetworkClient.Send(new RtStoreAck
            {
                SessionId = value.SessionId,
                Store = value.Store,
                BaselineId = value.BaselineId,
                DeliverySequence = value.DeliverySequence,
            }, Channels.Reliable);
        }

        private static void SendResync(RtStoreResyncData value)
        {
            NetworkClient.Send(new RtStoreResyncRequest
            {
                SessionId = value.SessionId,
                Store = value.Store,
                BaselineId = value.BaselineId,
                ExpectedDeliverySequence = value.ExpectedDeliverySequence,
            }, Channels.Reliable);
        }

        private static void SendCommand(RuntimeCommandEnvelope value)
        {
            NetworkClient.Send(new RtCommandEnvelope { Value = value }, Channels.Reliable);
        }
    }
}
#endif
