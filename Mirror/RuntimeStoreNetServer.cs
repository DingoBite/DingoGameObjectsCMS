#if MIRROR
using System;
using System.Collections.Generic;
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
        private readonly Func<FixedString32Bytes, RuntimeStore> _stores;
        private readonly Func<IEnumerable<FixedString32Bytes>> _replicatedStoresGetter;
        private readonly RuntimeCommandsBus _commandsBus;

        private readonly Dictionary<FixedString32Bytes, StoreReplicationState> _replicationByStore = new();
        private readonly Dictionary<int, Dictionary<FixedString32Bytes, ConnectionStoreState>> _connectionStates = new();
        private readonly Dictionary<int, uint> _lastCommandSeqByConn = new();

        private uint _nextS2CCommandSeq;
        private bool _scheduled;

        public Func<NetworkConnectionToClient, GameRuntimeCommand, bool> ValidateCommand;

        public RuntimeStoreNetServer(
            Func<FixedString32Bytes, RuntimeStore> stores,
            Func<IEnumerable<FixedString32Bytes>> replicatedStoresGetter = null,
            RuntimeCommandsBus commandsBus = null)
        {
            _stores = stores;
            _replicatedStoresGetter = replicatedStoresGetter;
            _commandsBus = commandsBus;

            NetworkServer.RegisterHandler<RtCommandMsg>(OnCommand, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreAckMsg>(OnStoreAck, requireAuthentication: true);
            NetworkServer.RegisterHandler<RtStoreResyncRequestMsg>(OnStoreResyncRequest, requireAuthentication: true);

            EnsureReplicatedStores();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", $"RuntimeStoreNetServer initialized commandsBus={(_commandsBus != null)}");
        }

        public void OnConnectionDisconnected(int connectionId)
        {
            _connectionStates.Remove(connectionId);
            _lastCommandSeqByConn.Remove(connectionId);

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", $"connection disconnected conn={connectionId}");
        }

        public void SendFullSnapshot(FixedString32Bytes storeKey, NetworkConnectionToClient conn)
        {
            if (conn == null)
                return;

            if (!TryEnsureStoreState(storeKey, out var state))
                return;

            var snapshotId = state.LastSnapshotId + 1;
            var payload = BuildFullSyncPayload(state, snapshotId, conn);
            var encoded = RuntimeNetSerialization.Serialize(payload);

            conn.Send(new RtStoreSyncMsg { Payload = encoded }, Channels.Reliable);

            state.LastSnapshotId = snapshotId;

            var connState = GetConnectionStoreState(conn.connectionId, state.StoreId, createIfMissing: true);
            connState.LastSentSnapshotId = snapshotId;

            if (RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Server("SNAP", $"send full store={state.StoreId} snap={snapshotId} conn={conn.connectionId} bytes={(encoded?.Length ?? 0)}");
        }

        public void BroadcastCommand(GameRuntimeCommand command, uint tick = 0, int sender = -1)
        {
            if (command == null)
                return;

            var payload = RuntimeNetSerialization.Serialize(command);
            var seq = ++_nextS2CCommandSeq;

            NetworkServer.SendToReady(new RtCommandMsg
            {
                StoreId = command.StoreId,
                Tick = tick,
                Seq = seq,
                Sender = sender,
                Payload = payload,
            }, Channels.Reliable);

            if (RuntimeNetTrace.LOG_COMMANDS)
                RuntimeNetTrace.Server("CMD", $"broadcast s2c seq={seq} store={command.StoreId} tick={tick} sender={sender} bytes={(payload?.Length ?? 0)}");
        }

        private void OnCommand(NetworkConnectionToClient conn, RtCommandMsg msg)
        {
            if (!NetworkServer.active)
                return;

            if (RuntimeNetTrace.LOG_COMMANDS)
            {
                RuntimeNetTrace.Server(
                    "CMD",
                    $"recv c2s seq={msg.Seq} store={msg.StoreId} tick={msg.Tick} sender={msg.Sender} conn={conn.connectionId} bytes={(msg.Payload?.Length ?? 0)} auth={conn.isAuthenticated} ready={conn.isReady}");
            }

            ExecuteCommandFromMessage(conn, in msg);
        }

        private void ExecuteCommandFromMessage(NetworkConnectionToClient conn, in RtCommandMsg msg)
        {
            if (conn == null || !conn.isAuthenticated)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                {
                    RuntimeNetTrace.Server(
                        "CMD",
                        $"drop c2s command seq={msg.Seq} reason=not_authenticated conn={(conn == null ? -1 : conn.connectionId)}");
                }

                return;
            }

            if (_lastCommandSeqByConn.TryGetValue(conn.connectionId, out var lastSeq) && msg.Seq <= lastSeq)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Server("CMD", $"drop c2s command seq={msg.Seq} reason=duplicate last={lastSeq} conn={conn.connectionId}");

                return;
            }

            _lastCommandSeqByConn[conn.connectionId] = msg.Seq;

            var command = RuntimeNetSerialization.Deserialize<GameRuntimeCommand>(msg.Payload);
            if (command == null)
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Server("CMD", $"drop c2s command seq={msg.Seq} reason=deserialize_failed conn={conn.connectionId}");

                return;
            }

            if (command.StoreId.Length == 0)
                command.StoreId = msg.StoreId;

            if (ValidateCommand != null && !ValidateCommand(conn, command))
            {
                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Server("CMD", $"drop c2s command seq={msg.Seq} reason=validation_failed conn={conn.connectionId}");

                return;
            }

            if (_commandsBus != null)
            {
                _commandsBus.Enqueue(command);

                if (RuntimeNetTrace.LOG_COMMANDS)
                    RuntimeNetTrace.Server("CMD", $"enqueue bus seq={msg.Seq} store={command.StoreId} conn={conn.connectionId}");

                return;
            }

            if (RuntimeNetTrace.LOG_COMMANDS)
                RuntimeNetTrace.Server("CMD", $"execute immediate seq={msg.Seq} store={command.StoreId} conn={conn.connectionId}");

            ExecuteCommandImmediate(command);
        }

        private void OnStoreAck(NetworkConnectionToClient conn, RtStoreAckMsg msg)
        {
            var state = GetConnectionStoreState(conn.connectionId, msg.StoreId, createIfMissing: true);
            if (state.LastAckSnapshotId < msg.SnapshotId)
                state.LastAckSnapshotId = msg.SnapshotId;

            if (RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Server("SNAP", $"recv ack store={msg.StoreId} snap={msg.SnapshotId} conn={conn.connectionId}");
        }

        private void OnStoreResyncRequest(NetworkConnectionToClient conn, RtStoreResyncRequestMsg msg)
        {
            if (RuntimeNetTrace.LOG_SNAPSHOTS)
                RuntimeNetTrace.Server("SNAP", $"recv resync request store={msg.StoreId} have={msg.HaveSnapshotId} conn={conn.connectionId}");

            SendFullSnapshot(msg.StoreId, conn);
        }

        private void EnsureFlushScheduled()
        {
            if (_scheduled)
                return;

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, Flush, RuntimeStore.UPDATE_ORDER + 1);
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
                FlushReplications();
            }
            finally
            {
                _scheduled = false;
                CoroutineParent.RemoveLateUpdater(this);

                if (HasDirtyStores())
                    EnsureFlushScheduled();
            }
        }

        private void FlushReplications()
        {
            foreach (var pair in _replicationByStore)
            {
                var state = pair.Value;
                if (!state.Dirty)
                    continue;

                if (state.PendingDelta.HasAny)
                    BroadcastStoreDelta(state);

                ClearPendingDelta(state);
                state.Dirty = false;
            }
        }

        private void BroadcastStoreDelta(StoreReplicationState state)
        {
            var snapshotId = state.LastSnapshotId + 1;
            var sentAny = false;

            foreach (var conn in NetworkServer.connections.Values)
            {
                if (conn == null || !conn.isReady)
                    continue;

                var connState = GetConnectionStoreState(conn.connectionId, state.StoreId, createIfMissing: true);

                if (connState.LastAckSnapshotId == 0)
                {
                    if (connState.LastSentSnapshotId == 0)
                    {
                        var fullEncoded = RuntimeNetSerialization.Serialize(BuildFullSyncPayload(state, snapshotId, conn));
                        conn.Send(new RtStoreSyncMsg { Payload = fullEncoded }, Channels.Reliable);
                        connState.LastSentSnapshotId = snapshotId;
                        sentAny = true;

                        if (RuntimeNetTrace.LOG_SNAPSHOTS)
                        {
                            RuntimeNetTrace.Server(
                                "SNAP",
                                $"send full store={state.StoreId} snap={snapshotId} conn={conn.connectionId} reason=await_initial_ack bytes={(fullEncoded?.Length ?? 0)}");
                        }
                    }

                    continue;
                }

                var payload = BuildDeltaSyncPayload(state, snapshotId, conn);
                if (!payload.HasAny)
                    continue;

                var encoded = RuntimeNetSerialization.Serialize(payload);
                conn.Send(new RtStoreSyncMsg { Payload = encoded }, Channels.Reliable);
                connState.LastSentSnapshotId = snapshotId;
                sentAny = true;

                if (RuntimeNetTrace.LOG_SNAPSHOTS)
                {
                    RuntimeNetTrace.Server(
                        "SNAP",
                        $"send delta store={state.StoreId} snap={snapshotId} conn={conn.connectionId} struct={payload.StructureChanges.Count} compStruct={payload.ObjectStructChanges.Count} compDelta={payload.ComponentDeltas.Count} bytes={(encoded?.Length ?? 0)}");
                }
            }

            if (sentAny)
                state.LastSnapshotId = snapshotId;
        }

        private static RtStoreSyncPayload BuildFullSyncPayload(StoreReplicationState state, uint snapshotId, NetworkConnectionToClient conn)
        {
            var snapshot = RuntimeStoreSnapshotCodec.BuildSnapshot(state.Store, conn);
            return RuntimeStoreSnapshotCodec.BuildFullSyncPayload(snapshot, state.StoreId, snapshotId);
        }

        private static RtStoreSyncPayload BuildDeltaSyncPayload(StoreReplicationState state, uint snapshotId, NetworkConnectionToClient conn)
        {
            var payload = new RtStoreSyncPayload
            {
                SnapshotId = snapshotId,
                StoreId = state.StoreId,
                Mode = RtStoreSyncMode.DeltaTick,
            };

            var pendingStructure = state.PendingDelta.StructureChanges;
            for (var i = 0; i < pendingStructure.Count; i++)
            {
                var change = pendingStructure[i];

                if (change.Kind == RuntimeStoreOpKind.Spawn)
                {
                    if (!state.Store.TryTakeRO(change.Id, out var obj) || obj == null)
                        continue;

                    if (!RuntimeReplicationFilter.ShouldReplicateObject(obj, ReplicationMask.Snapshot | ReplicationMask.Delta, conn))
                        continue;
                }

                payload.StructureChanges.Add(change);
            }

            var spawnedIds = new HashSet<long>();
            var removedIds = new HashSet<long>();

            for (var i = 0; i < payload.StructureChanges.Count; i++)
            {
                var change = payload.StructureChanges[i];

                if (change.Kind == RuntimeStoreOpKind.Spawn)
                {
                    spawnedIds.Add(change.Id);
                    removedIds.Remove(change.Id);
                }
                else if (change.Kind == RuntimeStoreOpKind.Remove)
                {
                    removedIds.Add(change.Id);
                    spawnedIds.Remove(change.Id);
                }
            }

            var componentStructByKey = new Dictionary<(long ObjectId, uint CompTypeId), RtStoreComponentStructDelta>();
            var pendingCompStruct = state.PendingDelta.ObjectStructChanges;

            for (var i = 0; i < pendingCompStruct.Count; i++)
            {
                var change = pendingCompStruct[i];
                if (removedIds.Contains(change.ObjectId) || spawnedIds.Contains(change.ObjectId))
                    continue;

                if (change.Kind == CompStructOpKind.Add)
                {
                    if (!state.Store.TryTakeRO(change.ObjectId, out var obj) || obj == null)
                        continue;

                    if (!RuntimeReplicationFilter.ShouldReplicateObject(obj, ReplicationMask.Delta, conn))
                        continue;

                    var component = obj.GetById(change.CompTypeId);
                    if (component == null)
                        continue;

                    if (!RuntimeReplicationFilter.ShouldReplicateComponent(obj, component, ReplicationMask.Delta, conn))
                        continue;
                }
                else
                {
                    if (!RuntimeReplicationFilter.ShouldReplicateComponentType(change.CompTypeId, ReplicationMask.Delta))
                        continue;
                }

                componentStructByKey[(change.ObjectId, change.CompTypeId)] = change;
            }

            if (componentStructByKey.Count > 0)
            {
                var keys = new List<(long, uint)>(componentStructByKey.Keys);
                keys.Sort((a, b) =>
                {
                    var c = a.Item1.CompareTo(b.Item1);
                    return c != 0 ? c : a.Item2.CompareTo(b.Item2);
                });

                for (var i = 0; i < keys.Count; i++)
                    payload.ObjectStructChanges.Add(componentStructByKey[keys[i]]);
            }

            var componentByKey = new Dictionary<(long ObjectId, uint CompTypeId), RtStoreComponentDelta>();
            var pendingCompDelta = state.PendingDelta.ComponentDeltas;

            for (var i = 0; i < pendingCompDelta.Count; i++)
            {
                var change = pendingCompDelta[i];
                if (removedIds.Contains(change.ObjectId) || spawnedIds.Contains(change.ObjectId))
                    continue;

                var key = (change.ObjectId, change.CompTypeId);
                if (componentStructByKey.ContainsKey(key))
                    continue;

                if (!state.Store.TryTakeRO(change.ObjectId, out var obj) || obj == null)
                    continue;

                if (!RuntimeReplicationFilter.ShouldReplicateObject(obj, ReplicationMask.Delta, conn))
                    continue;

                var component = obj.GetById(change.CompTypeId);
                if (component == null)
                    continue;

                if (!RuntimeReplicationFilter.ShouldReplicateComponent(obj, component, ReplicationMask.Delta, conn))
                    continue;

                componentByKey[key] = change;
            }

            if (componentByKey.Count > 0)
            {
                var keys = new List<(long, uint)>(componentByKey.Keys);
                keys.Sort((a, b) =>
                {
                    var c = a.Item1.CompareTo(b.Item1);
                    return c != 0 ? c : a.Item2.CompareTo(b.Item2);
                });

                for (var i = 0; i < keys.Count; i++)
                    payload.ComponentDeltas.Add(componentByKey[keys[i]]);
            }

            return payload;
        }

        private static void ClearPendingDelta(StoreReplicationState state)
        {
            state.PendingDelta.StructureChanges.Clear();
            state.PendingDelta.ObjectStructChanges.Clear();
            state.PendingDelta.ComponentDeltas.Clear();
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
                TryEnsureStoreState(storeId, out _);
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

            store.StructureChanges += changes => OnStoreStructureChanges(storeId, changes);
            store.ComponentStructureChanges += changes => OnStoreComponentStructureChanges(storeId, changes);
            store.ComponentChanges += changes => OnStoreComponentChanges(storeId, changes);

            return true;
        }

        private void OnStoreStructureChanges(FixedString32Bytes storeId, NativeArray<RuntimeStructureDirty> changes)
        {
            if (changes.Length == 0 || !TryEnsureStoreState(storeId, out var state))
                return;

            var added = 0;

            for (var i = 0; i < changes.Length; i++)
            {
                var change = changes[i];

                switch (change.Kind)
                {
                    case RuntimeStoreOpKind.Spawn:
                    {
                        if (!state.Store.TryTakeRO(change.Id, out var obj) || obj == null)
                            continue;

                        if (!RuntimeReplicationFilter.ShouldReplicateObject(obj, ReplicationMask.Snapshot | ReplicationMask.Delta))
                            continue;

                        state.PendingDelta.StructureChanges.Add(new RtStoreStructureDelta
                        {
                            Kind = RuntimeStoreOpKind.Spawn,
                            Id = change.Id,
                            ParentId = change.ParentId,
                            Index = change.Index,
                            RemoveMode = default,
                            SpawnData = RuntimeNetSerialization.SerializeRuntimeObject(obj),
                        });
                        added++;
                        break;
                    }

                    case RuntimeStoreOpKind.Reparent:
                    case RuntimeStoreOpKind.Move:
                    case RuntimeStoreOpKind.Remove:
                    {
                        state.PendingDelta.StructureChanges.Add(new RtStoreStructureDelta
                        {
                            Kind = change.Kind,
                            Id = change.Id,
                            ParentId = change.ParentId,
                            Index = change.Index,
                            RemoveMode = change.RemoveMode,
                            SpawnData = null,
                        });
                        added++;
                        break;
                    }
                }
            }

            if (added <= 0)
                return;

            if (RuntimeNetTrace.LOG_DIRTY)
                RuntimeNetTrace.Server("DIRTY", $"capture structure dirty store={storeId} count={added}");

            MarkStoreDirty(storeId);
        }

        private void OnStoreComponentStructureChanges(FixedString32Bytes storeId, NativeArray<ObjectStructDirty> changes)
        {
            if (changes.Length == 0 || !TryEnsureStoreState(storeId, out var state))
                return;

            var added = 0;

            for (var i = 0; i < changes.Length; i++)
            {
                var objectId = changes[i].Id;
                var compTypeId = changes[i].Dirty.CompTypeId;

                if (HasPendingStructureOp(state, objectId, RuntimeStoreOpKind.Remove) || HasPendingStructureOp(state, objectId, RuntimeStoreOpKind.Spawn))
                    continue;

                if (!RuntimeReplicationFilter.ShouldReplicateComponentType(compTypeId, ReplicationMask.Delta))
                    continue;

                if (changes[i].Dirty.Kind == CompStructOpKind.Remove)
                {
                    state.PendingDelta.ObjectStructChanges.Add(new RtStoreComponentStructDelta
                    {
                        ObjectId = objectId,
                        CompTypeId = compTypeId,
                        Kind = CompStructOpKind.Remove,
                        Payload = null,
                    });

                    added++;
                    continue;
                }

                if (!state.Store.TryTakeRO(objectId, out var obj) || obj == null)
                    continue;

                if (!RuntimeReplicationFilter.ShouldReplicateObject(obj, ReplicationMask.Delta))
                    continue;

                var component = obj.GetById(compTypeId);
                if (component == null)
                    continue;

                if (!RuntimeReplicationFilter.ShouldReplicateComponent(obj, component, ReplicationMask.Delta))
                    continue;

                state.PendingDelta.ObjectStructChanges.Add(new RtStoreComponentStructDelta
                {
                    ObjectId = objectId,
                    CompTypeId = compTypeId,
                    Kind = CompStructOpKind.Add,
                    Payload = RuntimeNetSerialization.SerializeRuntimeComponent(component),
                });

                added++;
            }

            if (added <= 0)
                return;

            if (RuntimeNetTrace.LOG_DIRTY)
                RuntimeNetTrace.Server("DIRTY", $"capture component-struct dirty store={storeId} count={added}");

            MarkStoreDirty(storeId);
        }

        private void OnStoreComponentChanges(FixedString32Bytes storeId, NativeArray<ObjectComponentDirty> changes)
        {
            if (changes.Length == 0 || !TryEnsureStoreState(storeId, out var state))
                return;

            var added = 0;

            for (var i = 0; i < changes.Length; i++)
            {
                var objectId = changes[i].Id;
                var compTypeId = changes[i].Dirty.CompTypeId;

                if (HasPendingStructureOp(state, objectId, RuntimeStoreOpKind.Remove) || HasPendingStructureOp(state, objectId, RuntimeStoreOpKind.Spawn))
                    continue;

                if (!state.Store.TryTakeRO(objectId, out var obj) || obj == null)
                    continue;

                if (!RuntimeReplicationFilter.ShouldReplicateObject(obj, ReplicationMask.Delta))
                    continue;

                var component = obj.GetById(compTypeId);
                if (component == null)
                    continue;

                if (!RuntimeReplicationFilter.ShouldReplicateComponent(obj, component, ReplicationMask.Delta))
                    continue;

                if (!TryCollectComponentDelta(component, out var payload, out var isDelta))
                    continue;

                state.PendingDelta.ComponentDeltas.Add(new RtStoreComponentDelta
                {
                    ObjectId = objectId,
                    CompTypeId = compTypeId,
                    IsDelta = isDelta,
                    Payload = payload,
                });

                added++;
            }

            if (added <= 0)
                return;

            if (RuntimeNetTrace.LOG_DIRTY)
                RuntimeNetTrace.Server("DIRTY", $"capture component dirty store={storeId} count={added}");

            MarkStoreDirty(storeId);
        }

        private static bool HasPendingStructureOp(StoreReplicationState state, long objectId, RuntimeStoreOpKind kind)
        {
            var changes = state.PendingDelta.StructureChanges;
            for (var i = changes.Count - 1; i >= 0; i--)
            {
                if (changes[i].Id == objectId && changes[i].Kind == kind)
                    return true;
            }

            return false;
        }

        private static bool TryCollectComponentDelta(GameRuntimeComponent component, out byte[] payload, out bool isDelta)
        {
            if (component is IDeltaComponent deltaComponent)
            {
                var delta = deltaComponent.CollectComponentDelta();
                if (delta != null && delta.Length > 0)
                {
                    payload = delta;
                    isDelta = true;
                    return true;
                }
            }

            payload = RuntimeNetSerialization.SerializeRuntimeComponent(component);
            isDelta = false;
            return payload != null;
        }

        private void MarkStoreDirty(FixedString32Bytes storeId)
        {
            if (!TryEnsureStoreState(storeId, out var state))
                return;

            state.Dirty = true;
            EnsureFlushScheduled();

            if (RuntimeNetTrace.LOG_DIRTY)
                RuntimeNetTrace.Server("DIRTY", $"store marked dirty store={storeId}");
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
            public readonly RtStoreSyncPayload PendingDelta = new RtStoreSyncPayload
            {
                Mode = RtStoreSyncMode.DeltaTick,
            };

            public bool Dirty;
            public uint LastSnapshotId;

            public StoreReplicationState(FixedString32Bytes storeId, RuntimeStore store)
            {
                StoreId = storeId;
                Store = store;
                PendingDelta.StoreId = storeId;
            }
        }

        private sealed class ConnectionStoreState
        {
            public uint LastAckSnapshotId;
            public uint LastSentSnapshotId;
        }
    }
}
#endif
