#if MIRROR
using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using DingoGameObjectsCMS.RuntimeObjects;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror
{
    public sealed class RuntimeStoreNetClient
    {
        private readonly Func<Hash128, RuntimeStore> _stores;
        private readonly IRuntimeObjectSerializer _ser;

        private readonly List<RtSpawnMsg> _pendingSpawns = new();

        public RuntimeStoreNetClient(Func<Hash128, RuntimeStore> stores, IRuntimeObjectSerializer serializer)
        {
            _stores = stores;
            _ser = serializer;

            NetworkClient.RegisterHandler<RtSpawnMsg>(OnSpawn);
            NetworkClient.RegisterHandler<RtAttachMsg>(OnAttach);
            NetworkClient.RegisterHandler<RtMoveMsg>(OnMove);
            NetworkClient.RegisterHandler<RtRemoveMsg>(OnRemove);
            NetworkClient.RegisterHandler<RtAppliedMsg>(OnApplied);
        }

        public void SendMutate(Hash128 storeKey, uint seq, long targetId, uint compTypeId, byte[] payload)
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

        private RuntimeStore GetStore(Hash128 storeKey) => _stores(storeKey);

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

        private void DrainPending(RuntimeStore store, Hash128 storeKey)
        {
            for (int i = _pendingSpawns.Count - 1; i >= 0; i--)
            {
                var p = _pendingSpawns[i];
                if ((Hash128)p.StoreId != storeKey)
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
            var obj = store.CreateNet(msg.Id);
            _ser.DeserializeInto(obj, msg.Data);

            if (msg.ParentId < 0)
            {
                store.PublishRootExisting(msg.Id);
            }
            else
            {
                store.AttachChild(msg.ParentId, msg.Id, msg.InsertIndex);
            }
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

            var mutable = obj.Components.FirstOrDefault(c => c is INetMutableGRC) as INetMutableGRC;
            if (mutable == null)
                return;

            var dummy = new List<RuntimeMutateApplied>(0);
            var ctx = new RuntimeMutateContext(store: store, owner: obj, targetId: msg.TargetId, compTypeId: msg.CompTypeId, side: RuntimeMutateSide.ClientRemoteApply, connection: null, revision: msg.Revision);

            mutable.ApplyMutatePayload(in ctx, msg.Payload, dummy);
        }
    }
}
#endif