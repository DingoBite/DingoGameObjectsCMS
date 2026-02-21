#if MIRROR
using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using DingoUnityExtensions;
using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror
{
    public sealed class RuntimeStoreNetServer
    {
        private readonly Func<FixedString32Bytes, RuntimeStore> _stores;

        private readonly Queue<(NetworkConnectionToClient conn, RtMutateMsg msg)> _mutateQueue = new();
        private readonly Dictionary<int, uint> _lastSeqByConn = new();

        private uint _revision;
        private bool _scheduled;

        public RuntimeStoreNetServer(Func<FixedString32Bytes, RuntimeStore> stores)
        {
            _stores = stores;

            NetworkServer.RegisterHandler<RtMutateMsg>(OnMutate, requireAuthentication: true);
        }

        public void SendFullSnapshot(FixedString32Bytes storeKey, NetworkConnectionToClient conn)
        {
            var store = _stores(storeKey);
            foreach (var kv in store.Parents.V)
            {
                SendSubtree(store, conn, kv.Key, parentId: -1);
            }
        }

        public void BroadcastSpawn(FixedString32Bytes storeKey, GameRuntimeObject obj, long parentId = -1, int insertIndex = -1, uint clientSeq = 0)
        {
            var store = _stores(storeKey);
            
            // var data = _ser.Serialize(obj);
            
            NetworkServer.SendToReady(new RtSpawnMsg
            {
                StoreId = store.Id,
                Id = obj.InstanceId,
                ParentId = parentId,
                InsertIndex = insertIndex,
                Data = null,
                ClientSeq = clientSeq
            });
        }

        public void BroadcastAttach(FixedString32Bytes storeKey, long parentId, long childId, int insertIndex = -1)
        {
            var store = _stores(storeKey);

            NetworkServer.SendToReady(new RtAttachMsg
            {
                StoreId = store.Id,
                ParentId = parentId,
                ChildId = childId,
                InsertIndex = insertIndex
            });
        }

        public void BroadcastMove(FixedString32Bytes storeKey, long parentId, long childId, int newIndex)
        {
            var store = _stores(storeKey);

            NetworkServer.SendToReady(new RtMoveMsg
            {
                StoreId = store.Id,
                ParentId = parentId,
                ChildId = childId,
                NewIndex = newIndex
            });
        }

        public void BroadcastRemove(FixedString32Bytes storeKey, long id, RemoveMode mode = RemoveMode.Subtree)
        {
            var store = _stores(storeKey);

            NetworkServer.SendToReady(new RtRemoveMsg
            {
                StoreId = store.Id,
                Id = id,
                Mode = mode
            });
        }

        private void SendSubtree(RuntimeStore store, NetworkConnectionToClient conn, long id, long parentId)
        {
            if (!store.TryTakeRO(id, out var obj))
                return;

            // var data = _ser.Serialize(obj);

            conn.Send(new RtSpawnMsg
            {
                StoreId = store.Id,
                Id = id,
                ParentId = parentId,
                InsertIndex = -1,
                Data = null,
                ClientSeq = 0
            });

            if (!store.TryTakeChildren(id, out var children))
                return;
            foreach (var c in children)
            {
                SendSubtree(store, conn, c, parentId: id);
            }
        }

        private void OnMutate(NetworkConnectionToClient conn, RtMutateMsg msg)
        {
            _mutateQueue.Enqueue((conn, msg));
            EnsureFlushScheduled();
        }

        private void EnsureFlushScheduled()
        {
            if (_scheduled)
                return;

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, FlushMutates);
        }

        private void FlushMutates()
        {
            if (!NetworkServer.active)
                return;

            while (_mutateQueue.Count > 0)
            {
                var (conn, msg) = _mutateQueue.Dequeue();
                var store = _stores(msg.StoreId);

                if (_lastSeqByConn.TryGetValue(conn.connectionId, out var last) && msg.Seq <= last)
                    continue;
                _lastSeqByConn[conn.connectionId] = msg.Seq;

                if (!store.TryTakeRW(msg.TargetId, out var obj))
                    continue;

                var mutable = obj.Components.FirstOrDefault(c => c is INetMutableGRC) as INetMutableGRC;
                if (mutable == null)
                    continue;

                var applied = new List<RuntimeMutateApplied>(4);

                var ctx = new RuntimeMutateContext(store: store, owner: obj, targetId: msg.TargetId, compTypeId: msg.CompTypeId, side: RuntimeMutateSide.ServerAuthoritative, connection: conn, revision: ++_revision);

                mutable.ApplyMutatePayload(in ctx, msg.Payload, applied);

                foreach (var a in applied)
                {
                    NetworkServer.SendToReady(new RtAppliedMsg
                    {
                        StoreId = store.Id,
                        Revision = _revision,
                        TargetId = a.TargetId,
                        CompTypeId = a.CompTypeId,
                        Payload = a.Payload
                    });
                }
            }

            _scheduled = false;
            CoroutineParent.RemoveLateUpdater(this);
        }
    }
}
#endif