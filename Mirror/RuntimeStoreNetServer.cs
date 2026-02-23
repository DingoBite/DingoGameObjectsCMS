#if MIRROR
using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoUnityExtensions;
using Mirror;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror
{
    public sealed class RuntimeStoreNetServer
    {
        private const int SNAPSHOT_HISTORY_CAPACITY = 64;

        private readonly Func<FixedString32Bytes, RuntimeStore> _stores;
        private readonly Func<IEnumerable<FixedString32Bytes>> _replicatedStoresGetter;
        private readonly RuntimeCommandsBus _commandsBus;

        private readonly Queue<(NetworkConnectionToClient conn, RtMutateMsg msg)> _mutateQueue = new();
        private readonly Dictionary<int, uint> _lastMutateSeqByConn = new();

        private readonly Dictionary<FixedString32Bytes, StoreReplicationState> _replicationByStore = new();
        private readonly Dictionary<int, Dictionary<FixedString32Bytes, ConnectionStoreState>> _connectionStates = new();
        private readonly Dictionary<int, uint> _lastCommandSeqByConn = new();

        private uint _revision;
        private uint _nextS2CCommandSeq;
        private bool _scheduled;

        public Func<NetworkConnectionToClient, GameRuntimeCommand, bool> ValidateCommand;

        public RuntimeStoreNetServer(Func<FixedString32Bytes, RuntimeStore> stores, Func<IEnumerable<FixedString32Bytes>> replicatedStoresGetter = null, RuntimeCommandsBus commandsBus = null)
        {
            _stores = stores;
            _replicatedStoresGetter = replicatedStoresGetter;
            _commandsBus = commandsBus;

            NetworkServer.RegisterHandler<RtMutateMsg>(OnMutate, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtCommandMsg>(OnCommand, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreAckMsg>(OnStoreAck, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreResyncRequestMsg>(OnStoreResyncRequest, requireAuthentication: true);

            EnsureReplicatedStores();
        }

        public void OnConnectionDisconnected(int connectionId)
        {
            _connectionStates.Remove(connectionId);
            _lastMutateSeqByConn.Remove(connectionId);
            _lastCommandSeqByConn.Remove(connectionId);
        }

        public void SendFullSnapshot(FixedString32Bytes storeKey, NetworkConnectionToClient conn)
        {
            if (conn == null)
                return;

            if (!TryEnsureStoreState(storeKey, out var state))
                return;

            var snapshot = RuntimeStoreSnapshotCodec.BuildSnapshot(state.Store);
            var snapshotId = PushSnapshot(state, snapshot);

            var payload = RuntimeStoreSnapshotCodec.BuildFullPayload(snapshot);
            var encoded = RuntimeNetSerialization.Serialize(payload);

            conn.Send(new RtStoreFullSnapshotMsg
            {
                StoreId = state.StoreId,
                SnapshotId = snapshotId,
                Payload = encoded,
            });

            var connStoreState = GetConnectionStoreState(conn.connectionId, state.StoreId, createIfMissing: true);
            if (connStoreState.LastAckSnapshotId < snapshotId)
                connStoreState.LastAckSnapshotId = snapshotId;
        }

        public void BroadcastCommand(GameRuntimeCommand command, uint tick = 0, int sender = -1)
        {
            if (command == null)
                return;

            var payload = RuntimeNetSerialization.Serialize(command);

            NetworkServer.SendToReady(new RtCommandMsg
            {
                StoreId = command.StoreId,
                Tick = tick,
                Seq = ++_nextS2CCommandSeq,
                Sender = sender,
                Payload = payload,
            }, Channels.Reliable);
        }

        public void BroadcastSpawn(FixedString32Bytes storeKey, GameRuntimeObject obj, long parentId = -1, int insertIndex = -1, uint clientSeq = 0)
        {
            var store = _stores(storeKey);

            NetworkServer.SendToReady(new RtSpawnMsg
            {
                StoreId = store.Id,
                Id = obj.InstanceId,
                ParentId = parentId,
                InsertIndex = insertIndex,
                Data = RuntimeNetSerialization.SerializeRuntimeObject(obj),
                ClientSeq = clientSeq,
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
                InsertIndex = insertIndex,
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
                NewIndex = newIndex,
            });
        }

        public void BroadcastRemove(FixedString32Bytes storeKey, long id, RemoveMode mode = RemoveMode.Subtree)
        {
            var store = _stores(storeKey);

            NetworkServer.SendToReady(new RtRemoveMsg
            {
                StoreId = store.Id,
                Id = id,
                Mode = mode,
            });
        }

        private void OnMutate(NetworkConnectionToClient conn, RtMutateMsg msg)
        {
            _mutateQueue.Enqueue((conn, msg));
            EnsureFlushScheduled();
        }

        private void OnCommand(NetworkConnectionToClient conn, RtCommandMsg msg)
        {
            if (!NetworkServer.active)
                return;

            ExecuteCommandFromMessage(conn, in msg);
        }

        private void ExecuteCommandFromMessage(NetworkConnectionToClient conn, in RtCommandMsg msg)
        {
            if (conn == null || !conn.isAuthenticated)
                return;

            if (_lastCommandSeqByConn.TryGetValue(conn.connectionId, out var lastSeq) && msg.Seq <= lastSeq)
                return;

            _lastCommandSeqByConn[conn.connectionId] = msg.Seq;

            var command = RuntimeNetSerialization.Deserialize<GameRuntimeCommand>(msg.Payload);
            if (command == null)
                return;

            var storeKey = msg.StoreId;

            if (command.StoreId.Length == 0)
                command.StoreId = storeKey;

            if (ValidateCommand != null && !ValidateCommand(conn, command))
                return;

            if (_commandsBus != null)
            {
                _commandsBus.Enqueue(command);
                return;
            }

            ExecuteCommandImmediate(command);
        }

        private void OnStoreAck(NetworkConnectionToClient conn, RtStoreAckMsg msg)
        {
            var state = GetConnectionStoreState(conn.connectionId, msg.StoreId, createIfMissing: true);
            if (state.LastAckSnapshotId < msg.SnapshotId)
                state.LastAckSnapshotId = msg.SnapshotId;
        }

        private void OnStoreResyncRequest(NetworkConnectionToClient conn, RtStoreResyncRequestMsg msg)
        {
            SendFullSnapshot(msg.StoreId, conn);
        }

        private void EnsureFlushScheduled()
        {
            if (_scheduled)
                return;

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, Flush);
        }

        private void Flush()
        {
            if (!NetworkServer.active)
            {
                _scheduled = false;
                CoroutineParent.RemoveLateUpdater(this);
                return;
            }

            try
            {
                EnsureReplicatedStores();
                FlushMutates();
                FlushReplications();
            }
            finally
            {
                _scheduled = false;
                CoroutineParent.RemoveLateUpdater(this);

                if (_mutateQueue.Count > 0 || HasDirtyStores())
                    EnsureFlushScheduled();
            }
        }

        private void FlushMutates()
        {
            while (_mutateQueue.Count > 0)
            {
                var (conn, msg) = _mutateQueue.Dequeue();
                var storeKey = msg.StoreId;
                var store = _stores(storeKey);

                if (_lastMutateSeqByConn.TryGetValue(conn.connectionId, out var last) && msg.Seq <= last)
                    continue;
                _lastMutateSeqByConn[conn.connectionId] = msg.Seq;

                if (!store.TryTakeRW(msg.TargetId, out var obj))
                    continue;

                var mutable = obj.GetById(msg.CompTypeId) as INetMutableGRC;
                if (mutable == null)
                    continue;

                var applied = new List<RuntimeMutateApplied>(4);

                var ctx = new RuntimeMutateContext(
                    store: store,
                    owner: obj,
                    targetId: msg.TargetId,
                    compTypeId: msg.CompTypeId,
                    side: RuntimeMutateSide.ServerAuthoritative,
                    connection: conn,
                    revision: ++_revision
                );

                mutable.ApplyMutatePayload(in ctx, msg.Payload, applied);

                foreach (var a in applied)
                {
                    NetworkServer.SendToReady(new RtAppliedMsg
                    {
                        StoreId = store.Id,
                        Revision = _revision,
                        TargetId = a.TargetId,
                        CompTypeId = a.CompTypeId,
                        Payload = a.Payload,
                    }, Channels.Reliable);
                }

                MarkStoreDirty(storeKey);
            }
        }

        private void FlushReplications()
        {
            foreach (var pair in _replicationByStore)
            {
                var state = pair.Value;
                if (!state.Dirty)
                    continue;

                BroadcastStoreDelta(state);
                state.Dirty = false;
            }
        }

        private void BroadcastStoreDelta(StoreReplicationState state)
        {
            var currentSnapshot = RuntimeStoreSnapshotCodec.BuildSnapshot(state.Store);
            var snapshotId = PushSnapshot(state, currentSnapshot);

            byte[] fullEncoded = null;
            var deltaByBaseline = new Dictionary<uint, byte[]>();

            foreach (var conn in NetworkServer.connections.Values)
            {
                if (conn == null || !conn.isReady)
                    continue;

                var connStoreState = GetConnectionStoreState(conn.connectionId, state.StoreId, createIfMissing: true);
                var baselineId = connStoreState.LastAckSnapshotId;

                if (baselineId == 0 || !TryGetSnapshot(state, baselineId, out var baselineSnapshot))
                {
                    fullEncoded ??= RuntimeNetSerialization.Serialize(RuntimeStoreSnapshotCodec.BuildFullPayload(currentSnapshot));

                    conn.Send(new RtStoreFullSnapshotMsg
                    {
                        StoreId = state.StoreId,
                        SnapshotId = snapshotId,
                        Payload = fullEncoded,
                    });

                    if (connStoreState.LastAckSnapshotId < snapshotId)
                        connStoreState.LastAckSnapshotId = snapshotId;
                    continue;
                }

                if (!deltaByBaseline.TryGetValue(baselineId, out var encodedDelta))
                {
                    var deltaPayload = RuntimeStoreSnapshotCodec.BuildDeltaPayload(baselineSnapshot, currentSnapshot);
                    encodedDelta = RuntimeNetSerialization.Serialize(deltaPayload);
                    deltaByBaseline[baselineId] = encodedDelta;
                }

                conn.Send(new RtStoreDeltaMsg
                {
                    StoreId = state.StoreId,
                    SnapshotId = snapshotId,
                    BaselineId = baselineId,
                    Payload = encodedDelta,
                });
            }
        }

        private bool HasDirtyStores()
        {
            foreach (var pair in _replicationByStore)
            {
                if (pair.Value.Dirty)
                    return true;
            }

            return false;
        }

        private void EnsureReplicatedStores()
        {
            if (_replicatedStoresGetter == null)
                return;

            var storeIds = _replicatedStoresGetter();
            if (storeIds == null)
                return;

            foreach (var storeId in storeIds)
            {
                TryEnsureStoreState(storeId, out _);
            }
        }

        private bool TryEnsureStoreState(FixedString32Bytes storeId, out StoreReplicationState state)
        {
            if (_replicationByStore.TryGetValue(storeId, out state))
                return true;

            var store = _stores(storeId);
            if (store == null)
                return false;

            state = new StoreReplicationState(storeId, store);
            _replicationByStore[storeId] = state;

            store.StructureChanges += _ => MarkStoreDirty(storeId);
            store.ComponentStructureChanges += _ => MarkStoreDirty(storeId);
            store.ComponentChanges += _ => MarkStoreDirty(storeId);

            return true;
        }

        private void MarkStoreDirty(FixedString32Bytes storeId)
        {
            if (!TryEnsureStoreState(storeId, out var state))
                return;

            state.Dirty = true;
            EnsureFlushScheduled();
        }

        private static bool TryGetSnapshot(StoreReplicationState state, uint snapshotId, out RuntimeStoreSnapshot snapshot)
        {
            for (var i = state.History.Count - 1; i >= 0; i--)
            {
                var entry = state.History[i];
                if (entry.SnapshotId != snapshotId)
                    continue;

                snapshot = entry.Snapshot;
                return true;
            }

            snapshot = null;
            return false;
        }

        private static uint PushSnapshot(StoreReplicationState state, RuntimeStoreSnapshot snapshot)
        {
            var snapshotId = ++state.LastSnapshotId;
            state.History.Add(new SnapshotHistoryEntry(snapshotId, snapshot));

            if (state.History.Count > SNAPSHOT_HISTORY_CAPACITY)
                state.History.RemoveAt(0);

            return snapshotId;
        }

        private static void ExecuteCommandImmediate(GameRuntimeCommand command)
        {
            var components = command.Components;
            if (components == null || components.Count == 0)
                return;

            for (var i = 0; i < components.Count; i++)
            {
                if (components[i] is ICommandLogic logic)
                    logic.Execute(command);
            }
        }

        private ConnectionStoreState GetConnectionStoreState(int connectionId, FixedString32Bytes storeId, bool createIfMissing)
        {
            if (!_connectionStates.TryGetValue(connectionId, out var byStore))
            {
                if (!createIfMissing)
                    return null;

                byStore = new Dictionary<FixedString32Bytes, ConnectionStoreState>();
                _connectionStates[connectionId] = byStore;
            }

            if (byStore.TryGetValue(storeId, out var state))
                return state;

            if (!createIfMissing)
                return null;

            state = new ConnectionStoreState();
            byStore[storeId] = state;
            return state;
        }

        private sealed class StoreReplicationState
        {
            public readonly FixedString32Bytes StoreId;
            public readonly RuntimeStore Store;
            public readonly List<SnapshotHistoryEntry> History = new();

            public bool Dirty;
            public uint LastSnapshotId;

            public StoreReplicationState(FixedString32Bytes storeId, RuntimeStore store)
            {
                StoreId = storeId;
                Store = store;
            }
        }

        private sealed class ConnectionStoreState
        {
            public uint LastAckSnapshotId;
        }

        private readonly struct SnapshotHistoryEntry
        {
            public readonly uint SnapshotId;
            public readonly RuntimeStoreSnapshot Snapshot;

            public SnapshotHistoryEntry(uint snapshotId, RuntimeStoreSnapshot snapshot)
            {
                SnapshotId = snapshotId;
                Snapshot = snapshot;
            }
        }
    }
}
#endif
