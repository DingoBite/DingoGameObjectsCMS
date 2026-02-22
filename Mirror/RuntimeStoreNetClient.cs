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

        private readonly List<RtSpawnMsg> _pendingSpawns = new();
        private readonly Dictionary<FixedString32Bytes, uint> _lastSnapshotByStore = new();
        private readonly HashSet<FixedString32Bytes> _resyncInFlight = new();

        private uint _nextC2SCommandSeq;
        private uint _lastS2CCommandSeq;

        public RuntimeStoreNetClient(Func<FixedString32Bytes, RuntimeStore> stores, RuntimeCommandsBus commandsBus = null)
        {
            _stores = stores;
            _commandsBus = commandsBus;

            NetworkClient.RegisterHandler<RtSpawnMsg>(OnSpawn);
            NetworkClient.RegisterHandler<RtAttachMsg>(OnAttach);
            NetworkClient.RegisterHandler<RtMoveMsg>(OnMove);
            NetworkClient.RegisterHandler<RtRemoveMsg>(OnRemove);
            NetworkClient.RegisterHandler<RtAppliedMsg>(OnApplied);

            NetworkClient.RegisterHandler<RtCommandMsg>(OnCommand);
            NetworkClient.RegisterHandler<RtStoreFullSnapshotMsg>(OnFullSnapshot);
            NetworkClient.RegisterHandler<RtStoreDeltaMsg>(OnDelta);
        }

        public void SendMutate(FixedString32Bytes storeKey, uint seq, long targetId, uint compTypeId, byte[] payload)
        {
            NetworkClient.Send(new RtMutateMsg
            {
                StoreId = storeKey,
                Seq = seq,
                TargetId = targetId,
                CompTypeId = compTypeId,
                Payload = payload,
            }, Channels.Reliable);
        }

        public void SendCommand(GameRuntimeCommand command, uint tick = 0)
        {
            if (!NetworkClient.isConnected || command == null)
                return;

            if (!CanSendCommandsNow())
                return;

            var payload = RuntimeNetSerialization.Serialize(command);
            NetworkClient.Send(new RtCommandMsg
            {
                StoreId = command.StoreId,
                Tick = tick,
                Seq = ++_nextC2SCommandSeq,
                Sender = -1,
                Payload = payload,
            }, Channels.Reliable);
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
                return;

            _lastS2CCommandSeq = msg.Seq;

            if (_commandsBus == null)
                return;

            var command = RuntimeNetSerialization.Deserialize<GameRuntimeCommand>(msg.Payload);
            if (command == null)
                return;

            var storeKey = msg.StoreId;

            if (command.StoreId.Length == 0)
                command.StoreId = storeKey;

            _commandsBus.Enqueue(command);
        }

        private void OnFullSnapshot(RtStoreFullSnapshotMsg msg)
        {
            var storeKey = msg.StoreId;
            var store = GetStore(storeKey);

            _lastSnapshotByStore.TryGetValue(storeKey, out var lastAppliedSnapshotId);
            if (msg.SnapshotId <= lastAppliedSnapshotId)
                return;

            var payload = RuntimeNetSerialization.Deserialize<RtStoreFullSnapshotPayload>(msg.Payload);

            if (payload == null)
            {
                RequestResync(storeKey);
                return;
            }

            if (!RuntimeStoreSnapshotCodec.ApplyFullSnapshot(store, payload))
            {
                RequestResync(storeKey);
                return;
            }

            _lastSnapshotByStore[storeKey] = msg.SnapshotId;
            _resyncInFlight.Remove(storeKey);
            SendStoreAck(storeKey, msg.SnapshotId);
        }

        private void OnDelta(RtStoreDeltaMsg msg)
        {
            var storeKey = msg.StoreId;
            var store = GetStore(storeKey);

            _lastSnapshotByStore.TryGetValue(storeKey, out var lastAppliedSnapshotId);
            if (msg.SnapshotId <= lastAppliedSnapshotId)
                return;

            if (msg.BaselineId != lastAppliedSnapshotId)
            {
                RequestResync(storeKey);
                return;
            }

            var payload = RuntimeNetSerialization.Deserialize<RtStoreDeltaPayload>(msg.Payload);
            if (payload == null)
            {
                RequestResync(storeKey);
                return;
            }

            if (!RuntimeStoreSnapshotCodec.ApplyDelta(store, payload))
            {
                RequestResync(storeKey);
                return;
            }

            _lastSnapshotByStore[storeKey] = msg.SnapshotId;
            SendStoreAck(storeKey, msg.SnapshotId);
        }

        private void RequestResync(FixedString32Bytes storeId)
        {
            if (!NetworkClient.isConnected)
                return;

            if (!_resyncInFlight.Add(storeId))
                return;

            _lastSnapshotByStore.TryGetValue(storeId, out var have);
            NetworkClient.Send(new RtStoreResyncRequestMsg
            {
                StoreId = storeId,
                HaveSnapshotId = have,
            }, Channels.Reliable);
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
        }

        private void OnSpawn(RtSpawnMsg msg)
        {
            var storeKey = msg.StoreId;
            var store = GetStore(storeKey);

            if (msg.ParentId >= 0 && !store.TryTakeRO(msg.ParentId, out _))
            {
                _pendingSpawns.Add(msg);
                return;
            }

            ApplySpawn(store, msg);
            DrainPending(store, storeKey);
        }

        private void DrainPending(RuntimeStore store, FixedString32Bytes storeKey)
        {
            for (var i = _pendingSpawns.Count - 1; i >= 0; i--)
            {
                var p = _pendingSpawns[i];
                if (p.StoreId != storeKey)
                    continue;

                if (p.ParentId < 0 || store.TryTakeRO(p.ParentId, out _))
                {
                    _pendingSpawns.RemoveAt(i);
                    ApplySpawn(store, p);
                }
            }
        }

        private static void ApplySpawn(RuntimeStore store, RtSpawnMsg msg)
        {
            var obj = RuntimeNetSerialization.DeserializeRuntimeObject(msg.Data);
            if (obj == null)
                return;

            obj.InstanceId = msg.Id;
            obj.StoreId = store.Id;

            store.TryUpsertNetObject(obj);

            if (msg.ParentId < 0)
                store.PublishRootExisting(msg.Id);
            else
                store.AttachChild(msg.ParentId, msg.Id, msg.InsertIndex);
        }

        private void OnAttach(RtAttachMsg msg)
        {
            var store = GetStore(msg.StoreId);
            if (!store.TryTakeRO(msg.ParentId, out _) || !store.TryTakeRO(msg.ChildId, out _))
                return;

            store.AttachChild(msg.ParentId, msg.ChildId, msg.InsertIndex);
        }

        private void OnMove(RtMoveMsg msg)
        {
            var store = GetStore(msg.StoreId);
            if (!store.TryTakeRO(msg.ParentId, out _) || !store.TryTakeRO(msg.ChildId, out _))
                return;

            store.MoveChild(msg.ParentId, msg.ChildId, msg.NewIndex);
        }

        private void OnRemove(RtRemoveMsg msg)
        {
            var store = GetStore(msg.StoreId);
            store.Remove(msg.Id, msg.Mode, out _);
        }

        private void OnApplied(RtAppliedMsg msg)
        {
            var store = GetStore(msg.StoreId);
            if (!store.TryTakeRW(msg.TargetId, out var obj))
                return;

            var mutable = obj.GetById(msg.CompTypeId) as INetMutableGRC;
            if (mutable == null)
                return;

            var ctx = new RuntimeMutateContext(store, obj, msg.TargetId, msg.CompTypeId, RuntimeMutateSide.ClientRemoteApply, null, msg.Revision);

            mutable.ApplyMutatePayload(in ctx, msg.Payload, null);
        }
    }
}
#endif
