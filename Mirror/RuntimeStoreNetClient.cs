#if MIRROR
using System;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoUnityExtensions;
using Mirror;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror
{
    public class RuntimeStoreNetClient : IDisposable
    {
        private const int TIMEOUT_TICK_ORDER = RuntimeStore.UPDATE_ORDER + 2;

        private readonly RuntimeProtocolClientCoordinator _coordinator;
        private bool _tickScheduled;

        public event Action<RuntimeCommandResult> CommandResultReceived
        {
            add => _coordinator.CommandResultReceived += value;
            remove => _coordinator.CommandResultReceived -= value;
        }

        public event Action<RuntimeStateStreamFrame> StateStreamFrameReceived
        {
            add => _coordinator.StateStreamFrameReceived += value;
            remove => _coordinator.StateStreamFrameReceived -= value;
        }

        public event Action<bool> ReplicaReadyChanged
        {
            add => _coordinator.ReplicaReadyChanged += value;
            remove => _coordinator.ReplicaReadyChanged -= value;
        }

        public bool IsReplicaReady => _coordinator.IsReplicaReady;
        public int AssignedConnectionId { get; private set; } = -1;
        public RuntimeNetworkTelemetry Telemetry => _coordinator.Telemetry;

        public RuntimeStoreNetClient(RuntimeProtocolContext context, ulong clientNonce)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            _coordinator = new RuntimeProtocolClientCoordinator(
                context,
                new RuntimeProtocolClientOutput(
                    SendHello,
                    SendReady,
                    SendReject,
                    SendAck,
                    SendResync,
                    SendCommand,
                    SendJournalResync),
                clientNonce);

            NetworkClient.RegisterHandler<RtSessionManifest>(OnManifest);
            NetworkClient.RegisterHandler<RtProtocolReject>(OnReject);
            NetworkClient.RegisterHandler<RtBaselineChunk>(OnBaselineChunk);
            NetworkClient.RegisterHandler<RtCheckpointChunk>(
                OnCheckpointChunk);
            NetworkClient.RegisterHandler<RtStoreDelta>(OnDelta);
            NetworkClient.RegisterHandler<RtCommandResult>(OnCommandResult);
            NetworkClient.RegisterHandler<RtStateStreamFrame>(OnStateStream);
            NetworkClient.RegisterHandler<RtCommandJournalBatch>(
                OnJournalBatch);
            Trace($"Endpoint created. nonce={clientNonce}.");
        }

        public void BeginHandshake()
        {
            AssignedConnectionId = -1;
            Trace("BeginHandshake -> sending Hello.");
            _coordinator.BeginHandshake();
            if (_tickScheduled)
                return;
            _tickScheduled = true;
            CoroutineParent.AddLateUpdater(this, Tick, TIMEOUT_TICK_ORDER);
            Trace("Handshake timeout tick scheduled.");
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

        public void Dispose()
        {
            Trace($"Dispose. session={_coordinator.SessionId}, replicaReady={IsReplicaReady}, assignedConnection={AssignedConnectionId}.");
            AssignedConnectionId = -1;
            NetworkClient.UnregisterHandler<RtSessionManifest>();
            NetworkClient.UnregisterHandler<RtProtocolReject>();
            NetworkClient.UnregisterHandler<RtBaselineChunk>();
            NetworkClient.UnregisterHandler<RtCheckpointChunk>();
            NetworkClient.UnregisterHandler<RtStoreDelta>();
            NetworkClient.UnregisterHandler<RtCommandResult>();
            NetworkClient.UnregisterHandler<RtStateStreamFrame>();
            NetworkClient.UnregisterHandler<RtCommandJournalBatch>();
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
            Trace(
                $"RECV Manifest session={message.SessionId}, assignedConnection={message.AssignedConnectionId}, "
                + $"assets={message.Assets?.Length ?? 0}, stores={message.Stores?.Length ?? 0}.");
            var result = _coordinator.ReceiveManifest(
                message.SessionId,
                message.Descriptor,
                message.Assets,
                message.Stores);
            if (result.Accepted)
            {
                AssignedConnectionId = message.AssignedConnectionId > 0
                    ? message.AssignedConnectionId
                    : -1;
            }
            Trace(
                $"Manifest result accepted={result.Accepted}, reject={result.RejectCode}, "
                + $"detail='{result.Detail}', assignedConnection={AssignedConnectionId}.");
        }

        private void OnReject(RtProtocolReject message)
        {
            Trace($"RECV Reject code={message.Code}, detail='{message.Detail}'. Disconnecting.");
            _coordinator.ReceiveReject(message.Code, message.Detail);
            NetworkClient.Disconnect();
        }

        private void OnBaselineChunk(RtBaselineChunk message)
        {
            var chunk = message.Value;
            Trace(
                $"RECV BaselineChunk session={chunk.SessionId}, store={chunk.Store}, baseline={chunk.BaselineId}, "
                + $"delivery={chunk.DeliverySequence}, chunk={chunk.ChunkIndex + 1}/{chunk.ChunkCount}, "
                + $"payload={chunk.Payload?.Length ?? 0}, logical={chunk.LogicalLength}.");
            var result = _coordinator.ReceiveBaselineChunk(
                chunk,
                NetworkTime.localTime);
            Trace(
                $"BaselineChunk result kind={result.Kind}, reject={result.RejectCode}, "
                + $"lastSequence={result.LastAppliedSequence}, replicaReady={IsReplicaReady}.");
        }

        private void OnCheckpointChunk(RtCheckpointChunk message)
        {
            var chunk = message.Value;
            Trace(
                $"RECV CheckpointChunk session={chunk.SessionId}, group='{chunk.CheckpointGroupId}', "
                + $"section={chunk.SectionIndex + 1}/{chunk.SectionCount}, "
                + $"page={chunk.PageIndex + 1}/{chunk.PageCount}, "
                + $"payload={chunk.Payload?.Length ?? 0}.");
            var result = _coordinator.ReceiveCheckpointChunk(
                chunk,
                NetworkTime.localTime);
            Trace($"CheckpointChunk result kind={result.Kind}, reject={result.RejectCode}, replicaReady={IsReplicaReady}.");
        }

        private void OnDelta(RtStoreDelta message)
        {
            Trace(
                $"RECV Delta session={message.SessionId}, store={message.Store}, baseline={message.BaselineId}, "
                + $"delivery={message.DeliverySequence}, revisions={message.FromRevision}->{message.ToRevision}, "
                + $"payload={message.Payload?.Length ?? 0}.");
            var result = _coordinator.ReceiveDelta(
                RuntimeClientDeltaEnvelope.FromWire(message),
                NetworkTime.localTime);
            Trace($"Delta result kind={result.Kind}, reject={result.RejectCode}, lastSequence={result.LastAppliedSequence}.");
        }

        private void OnCommandResult(RtCommandResult message)
        {
            Trace($"RECV CommandResult sequence={message.ClientSequence}, reject={message.RejectCode}.");
            _coordinator.ReceiveCommandResult(new RuntimeCommandResult(
                message.ClientSequence,
                message.RejectCode));
        }

        private void OnStateStream(RtStateStreamFrame message)
        {
            Trace(
                $"RECV StateStream session={message.SessionId}, store={message.Store}, "
                + $"type={message.StreamTypeId}, sequence={message.Sequence}, tick={message.SimulationTick}, "
                + $"payload={message.Payload?.Length ?? 0}.");
            _coordinator.ReceiveStateStream(new RtStateStreamFrameData(
                message.SessionId,
                message.Store,
                message.StreamTypeId,
                message.Sequence,
                message.SimulationTick,
                message.Payload));
        }

        private void OnJournalBatch(RtCommandJournalBatch message)
        {
            Trace(
                $"RECV JournalBatch session={message.SessionId}, fromCursor={message.FromCursor}, "
                + $"entries={message.Entries?.Length ?? 0}.");
            try
            {
                _coordinator.ReceiveJournalBatch(
                    RuntimeCommandJournalWireCodec.FromWire(message));
            }
            catch (Exception exception) when (exception is FormatException
                                              || exception is ArgumentException
                                              || exception is InvalidOperationException
                                              || exception is OverflowException)
            {
                Trace($"JournalBatch decode failed: {exception.GetType().Name}: {exception.Message}");
                SendReject(
                    RuntimeProtocolRejectCode.InvalidEnvelope,
                    $"Invalid journal batch: {exception.Message}");
            }
        }

        private static void SendHello(RuntimeSessionDescriptor descriptor, ulong clientNonce)
        {
            Trace(
                $"SEND Hello nonce={clientNonce}, protocol={descriptor.ProtocolVersion}, "
                + $"build='{descriptor.BuildId}', schema='{descriptor.RuntimeSchemaHash}', assets='{descriptor.AssetCatalogHash}'.");
            NetworkClient.Send(new RtSessionHello
            {
                Descriptor = descriptor,
                ClientNonce = clientNonce,
            }, Channels.Reliable);
        }

        private static void SendReady(ulong sessionId)
        {
            Trace($"SEND Ready session={sessionId}.");
            NetworkClient.Send(new RtSessionReady { SessionId = sessionId }, Channels.Reliable);
        }

        private static void SendReject(RuntimeProtocolRejectCode code, string detail)
        {
            Trace($"SEND Reject code={code}, detail='{detail}'.");
            NetworkClient.Send(new RtProtocolReject
            {
                Code = code,
                Detail = detail,
            }, Channels.Reliable);
            NetworkClient.Disconnect();
        }

        private static void SendAck(RtStoreAckData value)
        {
            Trace(
                $"SEND Ack session={value.SessionId}, store={value.Store}, baseline={value.BaselineId}, "
                + $"delivery={value.DeliverySequence}.");
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
            Trace(
                $"SEND Resync session={value.SessionId}, store={value.Store}, baseline={value.BaselineId}, "
                + $"expectedDelivery={value.ExpectedDeliverySequence}.");
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
            Trace(
                $"SEND Command type={value.CommandTypeId}, sequence={value.ClientSequence}, "
                + $"generation={value.ExpectedStoreGeneration}, payload={value.Payload?.Length ?? 0}.");
            NetworkClient.Send(new RtCommandEnvelope { Value = value }, Channels.Reliable);
        }

        private static void SendJournalResync(
            RtCommandJournalResyncData value)
        {
            Trace(
                $"SEND JournalResync session={value.SessionId}, group='{value.CheckpointGroupId}', "
                + $"cursor={value.ExpectedCursor}, fullBaseline={value.ForceCheckpointBaseline}.");
            NetworkClient.Send(
                new RtCommandJournalResyncRequest
                {
                    SessionId = value.SessionId,
                    CheckpointGroupId = value.CheckpointGroupId,
                    CheckpointHash = value.CheckpointHash,
                    ExpectedCursor = value.ExpectedCursor,
                    ForceCheckpointBaseline =
                        value.ForceCheckpointBaseline,
                },
                Channels.Reliable);
        }

        private static void Trace(string message)
        {
            Debug.Log(
                $"[NETTRACE][RuntimeClient][t={Time.realtimeSinceStartupAsDouble:F3}] {message}");
        }
    }
}
#endif
