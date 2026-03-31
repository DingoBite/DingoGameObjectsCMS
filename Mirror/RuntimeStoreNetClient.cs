#if MIRROR
using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Serialization;
using DingoUnityExtensions;
using Mirror;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror
{
    public sealed class RuntimeStoreNetClient : IDisposable
    {
        private readonly Func<FixedString32Bytes, RuntimeStore> _stores;
        private readonly RuntimeCommandsBus _commandsBus;
        private readonly Func<IEnumerable<FixedString32Bytes>> _replicatedStoresGetter;
        private readonly Action<bool> _replicaReadyChanged;
        private readonly IRuntimePayloadSerializer _serializer;

        private readonly Dictionary<FixedString32Bytes, uint> _lastSnapshotByStore = new();
        private readonly HashSet<FixedString32Bytes> _resyncInFlight = new();
        private readonly HashSet<FixedString32Bytes> _initialFullSnapshots = new();
        private readonly List<QueuedCommand> _pendingOutgoingCommands = new(capacity: 32);

        private uint _nextC2SCommandSeq;
        private bool _outgoingFlushScheduled;
        private bool _commandsBusSubscribed;
        private bool _deferredFlushLogged;
        private bool _replicaReady;

        public RuntimeStoreNetClient(
            Func<FixedString32Bytes, RuntimeStore> stores,
            RuntimeCommandsBus commandsBus = null,
            Func<IEnumerable<FixedString32Bytes>> replicatedStoresGetter = null,
            Action<bool> replicaReadyChanged = null,
            IRuntimePayloadSerializer serializer = null)
        {
            _stores = stores;
            _commandsBus = commandsBus;
            _replicatedStoresGetter = replicatedStoresGetter;
            _replicaReadyChanged = replicaReadyChanged;
            _serializer = serializer ?? RuntimePayloadSerialization.Current;

            NetworkClient.RegisterHandler<RtStoreSyncMsg>(OnStoreSync);

            if (_commandsBus != null && !NetworkServer.active)
            {
                _commandsBus.BeforeExecute += OnCommandBusInvoked;
                _commandsBusSubscribed = true;
            }

            RefreshReplicaReady();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Client("MANAGER", $"RuntimeStoreNetClient initialized commandsBus={(_commandsBus != null)} subscribed={_commandsBusSubscribed} replicaReady={_replicaReady}");
        }

        public void Dispose()
        {
            if (_commandsBusSubscribed && _commandsBus != null)
            {
                _commandsBus.BeforeExecute -= OnCommandBusInvoked;
                _commandsBusSubscribed = false;
            }

            StopOutgoingFlushSchedule();
            ResetReplicaReadiness();
        }

        public void SendCommand(GameRuntimeCommand command, uint tick = 0)
        {
            if (command == null)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Client("CMD", "skip c2s command reason=null_command");

                return;
            }

            QueueOutgoingCommand(command, tick);
            EnsureOutgoingFlushScheduled();
        }

        private bool CanSendCommandsNow() => NetworkClient.isConnected && NetworkClient.connection != null && NetworkClient.connection.isAuthenticated;

        private void OnCommandBusInvoked(GameRuntimeCommand command)
        {
            if (NetworkServer.active)
                return;

            QueueOutgoingCommand(command, tick: 0);
            EnsureOutgoingFlushScheduled();
        }

        private void QueueOutgoingCommand(GameRuntimeCommand command, uint tick)
        {
            if (command == null)
                return;

            var payload = _serializer.Serialize(command);
            if (payload == null || payload.Length == 0)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Client("CMD", "skip queue c2s reason=empty_payload");

                return;
            }

            _pendingOutgoingCommands.Add(new QueuedCommand(tick, payload));

            if (RuntimeNetTrace.LOG_COMMANDS)
                RuntimeNetTrace.Client("CMD", $"queue c2s tick={tick} bytes={payload.Length} pending={_pendingOutgoingCommands.Count}");
        }

        private void EnsureOutgoingFlushScheduled()
        {
            if (_outgoingFlushScheduled)
                return;

            _outgoingFlushScheduled = true;
            CoroutineParent.AddLateUpdater(this, FlushOutgoingCommands, RuntimeCommandsBus.UPDATE_ORDER + 1);
        }

        private void StopOutgoingFlushSchedule()
        {
            if (!_outgoingFlushScheduled)
                return;

            _outgoingFlushScheduled = false;
            CoroutineParent.RemoveLateUpdater(this);
        }

        private void FlushOutgoingCommands()
        {
            if (_pendingOutgoingCommands.Count == 0)
            {
                StopOutgoingFlushSchedule();
                return;
            }

            if (!CanSendCommandsNow())
            {
                if (RuntimeNetTrace.LOG_COMMANDS && !_deferredFlushLogged)
                    RuntimeNetTrace.Client("CMD", $"defer flush c2s pending={_pendingOutgoingCommands.Count} reason=not_authenticated_or_no_connection");

                _deferredFlushLogged = true;
                return;
            }

            _deferredFlushLogged = false;

            var sentCount = 0;
            foreach (var queued in _pendingOutgoingCommands)
            {
                var seq = ++_nextC2SCommandSeq;

                NetworkClient.Send(new RtCommandMsg
                {
                    Tick = queued.Tick,
                    Seq = seq,
                    Sender = -1,
                    Payload = queued.Payload,
                }, Channels.Reliable);

                sentCount++;

                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Client("CMD", $"send c2s seq={seq} tick={queued.Tick} bytes={(queued.Payload?.Length ?? 0)}");
            }

            if (sentCount > 0)
                _pendingOutgoingCommands.RemoveRange(0, sentCount);

            if (_pendingOutgoingCommands.Count == 0)
                StopOutgoingFlushSchedule();
        }

        private RuntimeStore GetStore(FixedString32Bytes storeKey) => _stores(storeKey);

        private readonly struct QueuedCommand
        {
            public readonly uint Tick;
            public readonly byte[] Payload;

            public QueuedCommand(uint tick, byte[] payload)
            {
                Tick = tick;
                Payload = payload;
            }
        }

        private void OnStoreSync(RtStoreSyncMsg msg)
        {
            var payload = _serializer.Deserialize<RtStoreSyncPayload>(msg.Payload);
            if (payload == null)
                return;

            var storeKey = payload.StoreId;
            var store = GetStore(storeKey);

            _lastSnapshotByStore.TryGetValue(storeKey, out var lastAppliedSnapshotId);

            var allowFullSnapshotReset = payload.Mode == RtStoreSyncMode.FullSnapshot && payload.SnapshotId <= lastAppliedSnapshotId;

            if (!allowFullSnapshotReset && payload.SnapshotId <= lastAppliedSnapshotId)
            {
                if (RuntimeNetTrace.LOG_SNAPSHOTS)
                    RuntimeNetTrace.Client("SNAP", $"drop store-sync mode={payload.Mode} store={storeKey} snap={payload.SnapshotId} reason=stale last={lastAppliedSnapshotId}");

                return;
            }

            if (allowFullSnapshotReset && RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Client("SNAP", $"accept authoritative full snapshot store={storeKey} snap={payload.SnapshotId} replacing_last={lastAppliedSnapshotId}");

            if (payload.Mode == RtStoreSyncMode.DeltaTick && lastAppliedSnapshotId > 0 && payload.SnapshotId > lastAppliedSnapshotId + 1)
            {
                if (RuntimeNetTrace.LOG_SNAPSHOTS)
                    RuntimeNetTrace.Client("SNAP", $"request resync store={storeKey} reason=non_contiguous_delta have={lastAppliedSnapshotId} got={payload.SnapshotId}");

                RequestResync(storeKey);
                return;
            }

            if (RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Client("SNAP", $"recv store-sync mode={payload.Mode} store={storeKey} snap={payload.SnapshotId} struct={payload.StructureChanges.Count} compStruct={payload.ObjectStructChanges.Count} compDelta={payload.ComponentDeltas.Count} bytes={(msg.Payload?.Length ?? 0)}");

            if (!RuntimeStoreSnapshotCodec.ApplySync(store, payload, _serializer))
            {
                if (RuntimeNetTrace.LOG_SNAPSHOTS)
                    RuntimeNetTrace.Client("SNAP", $"request resync store={storeKey} reason=apply_failed mode={payload.Mode} snap={payload.SnapshotId}");

                RequestResync(storeKey);
                return;
            }

            _lastSnapshotByStore[storeKey] = payload.SnapshotId;
            _resyncInFlight.Remove(storeKey);

            if (payload.Mode == RtStoreSyncMode.FullSnapshot)
                MarkInitialFullSnapshotApplied(storeKey);

            SendStoreAck(storeKey, payload.SnapshotId);

            if (RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Client("SNAP", $"apply store-sync mode={payload.Mode} store={storeKey} snap={payload.SnapshotId}");
        }

        private void RequestResync(FixedString32Bytes storeId)
        {
            InvalidateReplicaStore(storeId);

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

        private void MarkInitialFullSnapshotApplied(FixedString32Bytes storeId)
        {
            _initialFullSnapshots.Add(storeId);
            RefreshReplicaReady();
        }

        private void InvalidateReplicaStore(FixedString32Bytes storeId)
        {
            _initialFullSnapshots.Remove(storeId);
            RefreshReplicaReady();
        }

        private void ResetReplicaReadiness()
        {
            _initialFullSnapshots.Clear();
            _resyncInFlight.Clear();
            SetReplicaReady(false);
        }

        private void RefreshReplicaReady()
        {
            var requiredStores = GetRequiredReplicaStores();
            if (requiredStores.Count == 0)
            {
                SetReplicaReady(true);
                return;
            }

            foreach (var storeId in requiredStores)
            {
                if (_initialFullSnapshots.Contains(storeId))
                    continue;

                SetReplicaReady(false);
                return;
            }

            SetReplicaReady(true);
        }

        private HashSet<FixedString32Bytes> GetRequiredReplicaStores()
        {
            var requiredStores = new HashSet<FixedString32Bytes>();
            var storeIds = _replicatedStoresGetter?.Invoke();
            if (storeIds == null)
                return requiredStores;

            foreach (var storeId in storeIds)
                requiredStores.Add(storeId);

            return requiredStores;
        }

        private void SetReplicaReady(bool ready)
        {
            if (_replicaReady == ready)
                return;

            _replicaReady = ready;
            _replicaReadyChanged?.Invoke(ready);

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Client("MANAGER", $"replica ready changed ready={ready}");
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
