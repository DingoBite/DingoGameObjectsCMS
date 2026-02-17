#if MIRROR
using System.Collections.Generic;
using System.Linq;
using Mirror;
using DingoUnityExtensions;
using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Entities;

namespace DingoGameObjectsCMS.Mirror
{
    public sealed class RuntimeStoreNetServer
    {
        private readonly RuntimeStore _store;
        private readonly IRuntimeObjectSerializer _ser;

        private readonly Queue<(NetworkConnectionToClient conn, RtMutateMsg msg)> _mutateQueue = new();
        private readonly Dictionary<int, uint> _lastSeqByConn = new();

        private uint _revision;
        private bool _scheduled;

        public RuntimeStoreNetServer(RuntimeStore store, IRuntimeObjectSerializer serializer)
        {
            _store = store;
            _ser = serializer;

            NetworkServer.RegisterHandler<RtMutateMsg>(OnMutate, requireAuthentication: true);
        }

        public void SendFullSnapshot(NetworkConnectionToClient conn)
        {
            foreach (var kv in _store.Parents.V)
            {
                SendSubtree(conn, kv.Key, parentId: -1);
            }
        }

        private void SendSubtree(NetworkConnectionToClient conn, long id, long parentId)
        {
            if (!_store.TryTakeRO(id, out var obj))
                return;

            var data = _ser.Serialize(obj);

            conn.Send(new RtSpawnMsg
            {
                StoreId = _store.Id,
                Id = id,
                ParentId = parentId,
                InsertIndex = -1,
                Data = data,
                ClientSeq = 0
            });

            if (_store.TryTakeChildren(id, out var children))
            {
                foreach (var c in children)
                {
                    SendSubtree(conn, c, parentId: id);
                }
            }
        }

        public void BroadcastSpawn(GameRuntimeObject obj, long parentId = -1, int insertIndex = -1, uint clientSeq = 0)
        {
            var data = _ser.Serialize(obj);

            NetworkServer.SendToReady(new RtSpawnMsg
            {
                StoreId = _store.Id,
                Id = obj.InstanceId,
                ParentId = parentId,
                InsertIndex = insertIndex,
                Data = data,
                ClientSeq = clientSeq
            });
        }

        public void BroadcastAttach(long parentId, long childId, int insertIndex = -1)
        {
            NetworkServer.SendToReady(new RtAttachMsg
            {
                StoreId = _store.Id,
                ParentId = parentId,
                ChildId = childId,
                InsertIndex = insertIndex
            });
        }

        public void BroadcastMove(long parentId, long childId, int newIndex)
        {
            NetworkServer.SendToReady(new RtMoveMsg
            {
                StoreId = _store.Id,
                ParentId = parentId,
                ChildId = childId,
                NewIndex = newIndex
            });
        }

        public void BroadcastRemove(long id, RemoveMode mode = RemoveMode.Subtree)
        {
            NetworkServer.SendToReady(new RtRemoveMsg
            {
                StoreId = _store.Id,
                Id = id,
                Mode = mode
            });
        }

        private void OnMutate(NetworkConnectionToClient conn, RtMutateMsg msg)
        {
            if (msg.StoreId != (Hash128)_store.Id)
                return;

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

                if (_lastSeqByConn.TryGetValue(conn.connectionId, out var last) && msg.Seq <= last)
                    continue;
                _lastSeqByConn[conn.connectionId] = msg.Seq;

                if (!_store.TryTakeRW(msg.TargetId, out var obj))
                    continue;

                var mutable = obj.Components.FirstOrDefault(c => c is INetMutableGRC) as INetMutableGRC;
                if (mutable == null)
                    continue;

                var applied = new List<RuntimeMutateApplied>(4);

                var ctx = new RuntimeMutateContext(store: _store, owner: obj, targetId: msg.TargetId, compTypeId: msg.CompTypeId, side: RuntimeMutateSide.ServerAuthoritative, connection: conn, revision: ++_revision);

                mutable.ApplyMutatePayload(in ctx, msg.Payload, applied);

                foreach (var a in applied)
                {
                    NetworkServer.SendToReady(new RtAppliedMsg
                    {
                        StoreId = _store.Id,
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