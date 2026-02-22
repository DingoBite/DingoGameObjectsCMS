#if MIRROR
using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror
{
    public sealed class RuntimeStoreNetClient
    {
        private readonly Func<FixedString32Bytes, RuntimeStore> _stores;

        private readonly List<RtSpawnMsg> _pendingSpawns = new();

        public RuntimeStoreNetClient(Func<FixedString32Bytes, RuntimeStore> stores)
        {
            _stores = stores;

            NetworkClient.RegisterHandler<RtSpawnMsg>(OnSpawn);
            NetworkClient.RegisterHandler<RtAttachMsg>(OnAttach);
            NetworkClient.RegisterHandler<RtMoveMsg>(OnMove);
            NetworkClient.RegisterHandler<RtRemoveMsg>(OnRemove);
            NetworkClient.RegisterHandler<RtAppliedMsg>(OnApplied);
        }

        public void SendMutate(FixedString32Bytes storeKey, uint seq, long targetId, uint compTypeId, byte[] payload)
        {
            NetworkClient.Send(new RtMutateMsg
            {
                StoreId = storeKey,
                Seq = seq,
                TargetId = targetId,
                CompTypeId = compTypeId,
                Payload = payload
            });
        }

        private RuntimeStore GetStore(FixedString32Bytes storeKey) => _stores(storeKey);

        private void OnSpawn(RtSpawnMsg msg)
        {
            var store = GetStore(msg.StoreId);

            if (msg.ParentId >= 0 && !store.TryTakeRO(msg.ParentId, out _))
            {
                _pendingSpawns.Add(msg);
                return;
            }

            ApplySpawn(store, msg);
            DrainPending(store, msg.StoreId);
        }

        private void DrainPending(RuntimeStore store, FixedString32Bytes storeKey)
        {
            for (var i = _pendingSpawns.Count - 1; i >= 0; i--)
            {
                var p = _pendingSpawns[i];
                if ((FixedString32Bytes)p.StoreId != storeKey)
                    continue;

                if (p.ParentId < 0 || store.TryTakeRO(p.ParentId, out _))
                {
                    _pendingSpawns.RemoveAt(i);
                    ApplySpawn(store, p);
                }
            }
        }

        private void ApplySpawn(RuntimeStore store, RtSpawnMsg msg)
        {
            // var obj = store.CreateNet(msg.Id);
            // _ser.DeserializeInto(obj, msg.Data);

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

            var mutable = obj.Components.FirstOrDefault(c => c is INetMutableGRC) as INetMutableGRC; // TODO Get by msg.CompTypeId
            if (mutable == null)
                return;

            var ctx = new RuntimeMutateContext(store, obj, msg.TargetId, msg.CompTypeId, RuntimeMutateSide.ClientRemoteApply, null, msg.Revision);

            mutable.ApplyMutatePayload(in ctx, msg.Payload, null);
        }
    }
}
#endif