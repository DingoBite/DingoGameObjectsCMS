#if MIRROR
using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Mirror;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror
{
    public sealed class RuntimeStoreNetClient
    {
        private readonly Func<FixedString32Bytes, RuntimeStore> _stores;
        private readonly RuntimeCommandsBus _commandsBus;

        private readonly Dictionary<FixedString32Bytes, uint> _lastSnapshotByStore = new();
        private readonly HashSet<FixedString32Bytes> _resyncInFlight = new();

        private uint _nextC2SCommandSeq;
        private uint _lastS2CCommandSeq;

        public RuntimeStoreNetClient(Func<FixedString32Bytes, RuntimeStore> stores, RuntimeCommandsBus commandsBus = null)
        {
            _stores = stores;
            _commandsBus = commandsBus;

            NetworkClient.RegisterHandler<RtCommandMsg>(OnCommand);
            NetworkClient.RegisterHandler<RtStoreSyncMsg>(OnStoreSync);

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Client("MANAGER", $"RuntimeStoreNetClient initialized commandsBus={(_commandsBus != null)}");
        }

        public void SendCommand(GameRuntimeCommand command, uint tick = 0)
        {
            if (!NetworkClient.isConnected || command == null)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Client("CMD", $"skip c2s command reason={(command == null ? "null_command" : "not_connected")}");

                return;
            }

            if (!CanSendCommandsNow())
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Client("CMD", "skip c2s command reason=not_authenticated_or_no_connection");

                return;
            }

            var payload = RuntimeNetSerialization.Serialize(command);
            var seq = ++_nextC2SCommandSeq;

            NetworkClient.Send(new RtCommandMsg
            {
                StoreId = command.StoreId,
                Tick = tick,
                Seq = seq,
                Sender = -1,
                Payload = payload,
            }, Channels.Reliable);

            if (RuntimeNetTrace.LOG_COMMANDS)
                RuntimeNetTrace.Client("CMD", $"send c2s seq={seq} store={command.StoreId} tick={tick} bytes={(payload?.Length ?? 0)}");
        }

        private bool CanSendCommandsNow()
        {
            return NetworkClient.isConnected &&
                   NetworkClient.connection != null &&
                   NetworkClient.connection.isAuthenticated;
        }

        private RuntimeStore GetStore(FixedString32Bytes storeKey) => _stores(storeKey);

        private void OnCommand(RtCommandMsg msg)
        {
            if (_lastS2CCommandSeq >= msg.Seq)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Client("CMD", $"drop s2c command seq={msg.Seq} reason=duplicate last={_lastS2CCommandSeq}");

                return;
            }

            _lastS2CCommandSeq = msg.Seq;

            if (_commandsBus == null)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Client("CMD", $"drop s2c command seq={msg.Seq} reason=no_commands_bus");

                return;
            }

            var command = RuntimeNetSerialization.Deserialize<GameRuntimeCommand>(msg.Payload);
            if (command == null)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Client("CMD", $"drop s2c command seq={msg.Seq} reason=deserialize_failed");

                return;
            }

            if (command.StoreId.Length == 0)
                command.StoreId = msg.StoreId;

            _commandsBus.Enqueue(command);

            if (RuntimeNetTrace.LOG_COMMANDS)
            {
                RuntimeNetTrace.Client(
                    "CMD",
                    $"enqueue s2c command seq={msg.Seq} store={command.StoreId} tick={msg.Tick} sender={msg.Sender}");
            }
        }

        private void OnStoreSync(RtStoreSyncMsg msg)
        {
            var payload = RuntimeNetSerialization.Deserialize<RtStoreSyncPayload>(msg.Payload);
            if (payload == null)
                return;

            var storeKey = payload.StoreId;
            var store = GetStore(storeKey);

            _lastSnapshotByStore.TryGetValue(storeKey, out var lastAppliedSnapshotId);

            if (payload.SnapshotId <= lastAppliedSnapshotId)
            {
                if (RuntimeNetTrace.LOG_SNAPSHOTS)
                {
                    RuntimeNetTrace.Client(
                        "SNAP",
                        $"drop store-sync mode={payload.Mode} store={storeKey} snap={payload.SnapshotId} reason=stale last={lastAppliedSnapshotId}");
                }

                return;
            }

            if (payload.Mode == RtStoreSyncMode.DeltaTick &&
                lastAppliedSnapshotId > 0 &&
                payload.SnapshotId > lastAppliedSnapshotId + 1 &&
                RuntimeNetTrace.LOG_SNAPSHOTS)
            {
                RuntimeNetTrace.Client(
                    "SNAP",
                    $"accept non-contiguous delta store={storeKey} have={lastAppliedSnapshotId} got={payload.SnapshotId} reason=global_snapshot_marker");
            }

            if (RuntimeNetTrace.LOG_SNAPSHOTS)
            {
                RuntimeNetTrace.Client(
                    "SNAP",
                    $"recv store-sync mode={payload.Mode} store={storeKey} snap={payload.SnapshotId} struct={payload.StructureChanges.Count} compStruct={payload.ObjectStructChanges.Count} compDelta={payload.ComponentDeltas.Count} bytes={(msg.Payload?.Length ?? 0)}");
            }

            if (!RuntimeStoreSnapshotCodec.ApplySync(store, payload))
            {
                if (RuntimeNetTrace.LOG_SNAPSHOTS)
                {
                    RuntimeNetTrace.Client(
                        "SNAP",
                        $"request resync store={storeKey} reason=apply_failed mode={payload.Mode} snap={payload.SnapshotId}");
                }

                RequestResync(storeKey);
                return;
            }

            _lastSnapshotByStore[storeKey] = payload.SnapshotId;
            _resyncInFlight.Remove(storeKey);
            SendStoreAck(storeKey, payload.SnapshotId);

            if (RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Client("SNAP", $"apply store-sync mode={payload.Mode} store={storeKey} snap={payload.SnapshotId}");
        }

        private void RequestResync(FixedString32Bytes storeId)
        {
            if (!NetworkClient.isConnected)
            {
                if (RuntimeNetTrace.LOG_SNAPSHOTS)
                    RuntimeNetTrace.Client("SNAP", $"skip resync store={storeId} reason=not_connected");

                return;
            }

            if (!_resyncInFlight.Add(storeId))
            {
                if (RuntimeNetTrace.LOG_SNAPSHOTS)
                    RuntimeNetTrace.Client("SNAP", $"skip resync store={storeId} reason=already_in_flight");

                return;
            }

            _lastSnapshotByStore.TryGetValue(storeId, out var have);

            NetworkClient.Send(new RtStoreResyncRequestMsg
            {
                StoreId = storeId,
                HaveSnapshotId = have,
            }, Channels.Reliable);

            if (RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Client("SNAP", $"send resync request store={storeId} have={have}");
        }

        private static void SendStoreAck(FixedString32Bytes storeId, uint snapshotId)
        {
            if (!NetworkClient.isConnected)
                return;

            NetworkClient.Send(new RtStoreAckMsg
            {
                StoreId = storeId,
                SnapshotId = snapshotId,
            }, Channels.Reliable);

            if (RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Client("SNAP", $"send ack store={storeId} snap={snapshotId}");
        }
    }
}
#endif
