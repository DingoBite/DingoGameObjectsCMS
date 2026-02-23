#if MIRROR
using System;
using System.Collections.Generic;
using System.Text;
using Mirror;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror
{
    [Serializable, Preserve]
    public struct RtCommandMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint Tick;
        public uint Seq;
        public int Sender;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtStoreSyncMsg : NetworkMessage
    {
        public byte[] Payload;
    }

    public enum RtStoreSyncMode : byte
    {
        FullSnapshot = 1,
        DeltaTick = 2,
    }

    [Serializable, Preserve]
    public struct RtStoreAckMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint SnapshotId;
    }

    [Serializable, Preserve]
    public struct RtStoreResyncRequestMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint HaveSnapshotId;
    }

    [Flags]
    public enum ReplicationMask : byte
    {
        None = 0,
        Snapshot = 1,
        Delta = 2,
        All = Snapshot | Delta,
    }

    public interface IReplicatedObject
    {
        public ReplicationMask GetMask();
    }

    public interface ISnapshotComponent { }
    public interface IDeltaComponent
    {
        byte[] CollectComponentDelta();
        bool ApplyDelta(byte[] payload);
    }

    public interface IReplicationProfile
    {
        int ReplicationProfileId { get; }
    }

    public interface IOwnerConnectionIdProvider
    {
        int OwnerConnectionId { get; }
    }

    public interface IOwnerOnly { }
    public interface IReliableOnly { }
    public interface IUnreliableOk { }

    [Serializable, Preserve]
    public struct RtStoreStructureDelta
    {
        public RuntimeStoreOpKind Kind;
        public long Id;
        public long ParentId;
        public int Index;
        public RemoveMode RemoveMode;
        public byte[] SpawnData;
    }

    [Serializable, Preserve]
    public struct RtStoreComponentStructDelta
    {
        public long ObjectId;
        public uint CompTypeId;
        public CompStructOpKind Kind;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtStoreComponentDelta
    {
        public long ObjectId;
        public uint CompTypeId;
        public bool IsDelta;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public sealed class RtStoreSyncPayload
    {
        public uint SnapshotId;
        public FixedString32Bytes StoreId;
        public RtStoreSyncMode Mode;
        public List<RtStoreStructureDelta> StructureChanges = new();
        public List<RtStoreComponentStructDelta> ObjectStructChanges = new();
        public List<RtStoreComponentDelta> ComponentDeltas = new();

        public bool HasAny =>
            (StructureChanges != null && StructureChanges.Count > 0) ||
            (ObjectStructChanges != null && ObjectStructChanges.Count > 0) ||
            (ComponentDeltas != null && ComponentDeltas.Count > 0);
    }

    public static class RuntimeNetSerialization
    {
        public static byte[] Serialize<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            var json = JsonConvert.SerializeObject(value, Formatting.None, GameRuntimeComponentJson.Settings);
            return Encoding.UTF8.GetBytes(json);
        }

        public static T Deserialize<T>(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return default;

            var json = Encoding.UTF8.GetString(payload);
            return JsonConvert.DeserializeObject<T>(json, GameRuntimeComponentJson.Settings);
        }

        public static byte[] SerializeRuntimeObject(GameRuntimeObject value) => Serialize(value);

        public static GameRuntimeObject DeserializeRuntimeObject(byte[] payload) => Deserialize<GameRuntimeObject>(payload);

        public static byte[] SerializeRuntimeComponent(GameRuntimeComponent value)
        {
            if (value == null)
                return Array.Empty<byte>();

            var json = JsonConvert.SerializeObject(value, value.GetType(), Formatting.None, GameRuntimeComponentJson.Settings);
            return Encoding.UTF8.GetBytes(json);
        }

        public static GameRuntimeComponent DeserializeRuntimeComponent(uint compTypeId, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return null;

            if (!RuntimeComponentTypeRegistry.TryGetType(compTypeId, out var compType) || compType == null)
                return null;

            var json = Encoding.UTF8.GetString(payload);
            return JsonConvert.DeserializeObject(json, compType, GameRuntimeComponentJson.Settings) as GameRuntimeComponent;
        }
    }

    public sealed class RuntimeStoreSnapshot
    {
        public readonly Dictionary<long, RuntimeStoreSnapshotNode> NodesById = new();
    }

    public sealed class RuntimeStoreSnapshotNode
    {
        public long Id;
        public long ParentId;
        public int Index;
        public byte[] ObjectData;
    }

    public static class RuntimeReplicationFilter
    {
        public static bool ShouldReplicateObject(GameRuntimeObject obj, ReplicationMask mask, NetworkConnectionToClient connection = null, int replicationProfileId = 0)
        {
            if (obj == null)
                return false;

            if (!IsMaskAllowed(ResolveObjectMask(obj), mask))
                return false;

            if (!MatchesProfile(obj, replicationProfileId))
                return false;

            if (!IsOwnerVisible(obj, null, connection))
                return false;

            return true;
        }

        public static bool ShouldReplicateComponent(GameRuntimeObject owner, GameRuntimeComponent component, ReplicationMask mask, NetworkConnectionToClient connection = null, int replicationProfileId = 0)
        {
            if (component == null)
                return false;

            if (!ShouldReplicateComponentType(component.GetType(), mask))
                return false;

            if (!MatchesProfile(component, replicationProfileId))
                return false;

            if (!IsOwnerVisible(owner, component, connection))
                return false;

            return true;
        }

        public static bool ShouldReplicateComponentType(uint compTypeId, ReplicationMask mask)
        {
            if (!RuntimeComponentTypeRegistry.TryGetType(compTypeId, out var compType) || compType == null)
                return true;

            return ShouldReplicateComponentType(compType, mask);
        }

        private static bool ShouldReplicateComponentType(Type compType, ReplicationMask mask)
        {
            var hasSnapshotTag = typeof(ISnapshotComponent).IsAssignableFrom(compType);
            var hasDeltaTag = typeof(IDeltaComponent).IsAssignableFrom(compType);

            if (!hasSnapshotTag && !hasDeltaTag)
                return true;

            var needSnapshot = (mask & ReplicationMask.Snapshot) != 0;
            var needDelta = (mask & ReplicationMask.Delta) != 0;

            if (needSnapshot && needDelta)
                return hasSnapshotTag || hasDeltaTag;

            if (needSnapshot)
                return hasSnapshotTag;

            if (needDelta)
                return hasDeltaTag;

            return false;
        }

        private static ReplicationMask ResolveObjectMask(GameRuntimeObject obj)
        {
            var resolvedMask = ReplicationMask.All;
            var components = obj.Components;
            if (components == null)
                return resolvedMask;

            for (var i = 0; i < components.Count; i++)
            {
                if (components[i] is IReplicatedObject replicated)
                    resolvedMask &= replicated.GetMask();
            }

            return resolvedMask;
        }

        private static bool IsMaskAllowed(ReplicationMask resolvedMask, ReplicationMask requestedMask)
        {
            return (resolvedMask & requestedMask) != 0;
        }

        private static bool MatchesProfile(object value, int profileId)
        {
            if (profileId == 0)
                return true;

            if (value is not IReplicationProfile provider)
                return true;

            return provider.ReplicationProfileId == 0 || provider.ReplicationProfileId == profileId;
        }

        private static bool IsOwnerVisible(GameRuntimeObject owner, GameRuntimeComponent component, NetworkConnectionToClient connection)
        {
            var ownerOnly = (component is IOwnerOnly) || (owner is IOwnerOnly);
            if (!ownerOnly)
                return true;

            if (connection == null)
                return false;

            var ownerConnectionId = ResolveOwnerConnectionId(owner, component);
            if (ownerConnectionId < 0)
                return false;

            return ownerConnectionId == connection.connectionId;
        }

        private static int ResolveOwnerConnectionId(GameRuntimeObject owner, GameRuntimeComponent component)
        {
            if (component is IOwnerConnectionIdProvider componentOwner)
                return componentOwner.OwnerConnectionId;

            if (owner is IOwnerConnectionIdProvider objectOwner)
                return objectOwner.OwnerConnectionId;

            return -1;
        }
    }

    public static class RuntimeReplicationPolicy
    {
        public static bool ShouldReplicateObject(GameRuntimeObject obj, ReplicationMask mask)
        {
            return RuntimeReplicationFilter.ShouldReplicateObject(obj, mask);
        }

        public static bool ShouldReplicateComponent(GameRuntimeObject owner, GameRuntimeComponent component, ReplicationMask mask)
        {
            return RuntimeReplicationFilter.ShouldReplicateComponent(owner, component, mask);
        }

        public static bool ShouldReplicateComponentType(uint compTypeId, ReplicationMask mask)
        {
            return RuntimeReplicationFilter.ShouldReplicateComponentType(compTypeId, mask);
        }
    }

    public static class RuntimeStoreSnapshotCodec
    {
        public static RuntimeStoreSnapshot BuildSnapshot(RuntimeStore store, NetworkConnectionToClient connection = null, int replicationProfileId = 0)
        {
            var snapshot = new RuntimeStoreSnapshot();
            if (store == null)
                return snapshot;

            var roots = new List<long>(store.Parents.V.Keys);
            roots.Sort();

            for (var i = 0; i < roots.Count; i++)
            {
                BuildSnapshotRecursive(store, snapshot, roots[i], parentId: -1, index: i, connection, replicationProfileId);
            }

            return snapshot;
        }

        public static RtStoreSyncPayload BuildFullSyncPayload(RuntimeStoreSnapshot snapshot, FixedString32Bytes storeId, uint snapshotId)
        {
            var payload = new RtStoreSyncPayload
            {
                SnapshotId = snapshotId,
                StoreId = storeId,
                Mode = RtStoreSyncMode.FullSnapshot,
            };

            if (snapshot == null || snapshot.NodesById.Count == 0)
                return payload;

            var ordered = TopologicalOrder(snapshot);

            for (var i = 0; i < ordered.Count; i++)
            {
                var entry = ordered[i];
                payload.StructureChanges.Add(new RtStoreStructureDelta
                {
                    Kind = RuntimeStoreOpKind.Spawn,
                    Id = entry.Id,
                    ParentId = entry.ParentId,
                    Index = entry.Index,
                    RemoveMode = default,
                    SpawnData = entry.ObjectData,
                });
            }

            return payload;
        }

        public static bool ApplySync(RuntimeStore store, RtStoreSyncPayload payload)
        {
            if (store == null || payload == null)
                return false;

            if (payload.Mode == RtStoreSyncMode.FullSnapshot)
                ClearStore(store);

            if (payload.StructureChanges != null)
            {
                for (var i = 0; i < payload.StructureChanges.Count; i++)
                {
                    var change = payload.StructureChanges[i];
                    if (!ApplyStructureChange(store, in change))
                        return false;
                }
            }

            if (payload.ObjectStructChanges != null)
            {
                for (var i = 0; i < payload.ObjectStructChanges.Count; i++)
                {
                    var change = payload.ObjectStructChanges[i];
                    if (!ApplyComponentStructChange(store, in change))
                        return false;
                }
            }

            if (payload.ComponentDeltas != null)
            {
                for (var i = 0; i < payload.ComponentDeltas.Count; i++)
                {
                    var change = payload.ComponentDeltas[i];
                    if (!ApplyComponentChange(store, in change))
                        return false;
                }
            }

            return true;
        }

        private static void BuildSnapshotRecursive(RuntimeStore store, RuntimeStoreSnapshot snapshot, long id, long parentId, int index, NetworkConnectionToClient connection, int replicationProfileId)
        {
            if (!store.TryTakeRO(id, out var obj) || obj == null)
                return;

            if (!RuntimeReplicationFilter.ShouldReplicateObject(obj, ReplicationMask.Snapshot | ReplicationMask.Delta, connection, replicationProfileId))
                return;

            var node = new RuntimeStoreSnapshotNode
            {
                Id = id,
                ParentId = parentId,
                Index = index,
                ObjectData = RuntimeNetSerialization.SerializeRuntimeObject(obj),
            };

            snapshot.NodesById[id] = node;

            if (!store.TryTakeChildren(id, out var children) || children == null)
                return;

            for (var i = 0; i < children.Count; i++)
            {
                BuildSnapshotRecursive(store, snapshot, children[i], id, i, connection, replicationProfileId);
            }
        }

        private static List<RuntimeStoreSnapshotNode> TopologicalOrder(RuntimeStoreSnapshot snapshot)
        {
            var byParent = new Dictionary<long, List<RuntimeStoreSnapshotNode>>();
            var roots = new List<RuntimeStoreSnapshotNode>();

            foreach (var node in snapshot.NodesById.Values)
            {
                if (node.ParentId < 0 || !snapshot.NodesById.ContainsKey(node.ParentId))
                {
                    roots.Add(node);
                    continue;
                }

                if (!byParent.TryGetValue(node.ParentId, out var children))
                {
                    children = new List<RuntimeStoreSnapshotNode>();
                    byParent[node.ParentId] = children;
                }

                children.Add(node);
            }

            roots.Sort(CompareByIndexThenId);
            foreach (var pair in byParent)
            {
                pair.Value.Sort(CompareByIndexThenId);
            }

            var outList = new List<RuntimeStoreSnapshotNode>(snapshot.NodesById.Count);
            for (var i = 0; i < roots.Count; i++)
            {
                AppendSubtree(roots[i], byParent, outList);
            }

            return outList;
        }

        private static void AppendSubtree(RuntimeStoreSnapshotNode node, Dictionary<long, List<RuntimeStoreSnapshotNode>> byParent, List<RuntimeStoreSnapshotNode> outList)
        {
            outList.Add(node);

            if (!byParent.TryGetValue(node.Id, out var children) || children == null)
                return;

            for (var i = 0; i < children.Count; i++)
            {
                AppendSubtree(children[i], byParent, outList);
            }
        }

        private static bool ApplyStructureChange(RuntimeStore store, in RtStoreStructureDelta change)
        {
            switch (change.Kind)
            {
                case RuntimeStoreOpKind.Spawn:
                    return ApplySpawn(store, change.Id, change.ParentId, change.Index, change.SpawnData);

                case RuntimeStoreOpKind.Reparent:
                    if (change.ParentId < 0)
                    {
                        if (store.TryTakeParentRO(change.Id, out _))
                            return store.DetachChild(change.Id);

                        store.PublishRootExisting(change.Id);
                        return true;
                    }

                    return store.AttachChild(change.ParentId, change.Id, change.Index);

                case RuntimeStoreOpKind.Move:
                    return store.MoveChild(change.ParentId, change.Id, change.Index);

                case RuntimeStoreOpKind.Remove:
                    store.Remove(change.Id, change.RemoveMode, out _);
                    return true;

                default:
                    return false;
            }
        }

        private static bool ApplySpawn(RuntimeStore store, long id, long parentId, int index, byte[] data)
        {
            var obj = RuntimeNetSerialization.DeserializeRuntimeObject(data);
            if (obj == null)
                return false;

            obj.InstanceId = id;
            obj.StoreId = store.Id;

            store.TryUpsertNetObject(obj);

            if (parentId < 0)
            {
                store.PublishRootExisting(id);
                return true;
            }

            return store.AttachChild(parentId, id, index);
        }

        private static bool ApplyComponentStructChange(RuntimeStore store, in RtStoreComponentStructDelta change)
        {
            if (!store.TryTakeRW(change.ObjectId, out var obj) || obj == null)
                return false;

            switch (change.Kind)
            {
                case CompStructOpKind.Add:
                {
                    var component = RuntimeNetSerialization.DeserializeRuntimeComponent(change.CompTypeId, change.Payload);
                    if (component == null)
                        return false;

                    obj.AddOrReplace(component);
                    return true;
                }

                case CompStructOpKind.Remove:
                    obj.RemoveByTypeId(change.CompTypeId);
                    return true;

                default:
                    return false;
            }
        }

        private static bool ApplyComponentChange(RuntimeStore store, in RtStoreComponentDelta change)
        {
            if (!store.TryTakeRW(change.ObjectId, out var obj) || obj == null)
                return false;

            if (change.IsDelta)
            {
                var existing = obj.GetById(change.CompTypeId);
                if (existing is not IDeltaComponent delta)
                    return false;

                return delta.ApplyDelta(change.Payload);
            }

            var component = RuntimeNetSerialization.DeserializeRuntimeComponent(change.CompTypeId, change.Payload);
            if (component == null)
                return false;

            obj.AddOrReplace(component);
            return true;
        }

        private static void ClearStore(RuntimeStore store)
        {
            var roots = new List<long>(store.Parents.V.Keys);
            for (var i = 0; i < roots.Count; i++)
            {
                store.Remove(roots[i], RemoveMode.Subtree, out _);
            }
        }

        private static int CompareByIndexThenId(RuntimeStoreSnapshotNode a, RuntimeStoreSnapshotNode b)
        {
            var c = a.Index.CompareTo(b.Index);
            if (c != 0)
                return c;
            return a.Id.CompareTo(b.Id);
        }

    }
}
#endif
