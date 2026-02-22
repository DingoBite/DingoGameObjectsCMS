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
    public struct RtStoreFullSnapshotMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint SnapshotId;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtStoreDeltaMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint SnapshotId;
        public uint BaselineId;
        public byte[] Payload;
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

    [Serializable, Preserve]
    public struct RtSpawnMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public long Id;
        public long ParentId;
        public int InsertIndex;
        public byte[] Data;
        public uint ClientSeq;
    }

    [Serializable, Preserve]
    public struct RtAttachMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public long ParentId;
        public long ChildId;
        public int InsertIndex;
    }

    [Serializable, Preserve]
    public struct RtMoveMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public long ParentId;
        public long ChildId;
        public int NewIndex;
    }

    [Serializable, Preserve]
    public struct RtRemoveMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public long Id;
        public RemoveMode Mode;
    }

    [Serializable, Preserve]
    public struct RtMutateMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint Seq;
        public long TargetId;
        public uint CompTypeId;
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public struct RtAppliedMsg : NetworkMessage
    {
        public FixedString32Bytes StoreId;
        public uint Revision;
        public long TargetId;
        public uint CompTypeId;
        public byte[] Payload;
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
    public interface IDeltaComponent { }

    public interface IOwnerOnly { }
    public interface IReliableOnly { }
    public interface IUnreliableOk { }

    [Serializable, Preserve]
    public struct RtStoreSnapshotObjectData
    {
        public long Id;
        public long ParentId;
        public int Index;
        public byte[] Data;
    }

    [Serializable, Preserve]
    public sealed class RtStoreFullSnapshotPayload
    {
        public List<RtStoreSnapshotObjectData> Objects = new();
    }

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
        public byte[] Payload;
    }

    [Serializable, Preserve]
    public sealed class RtStoreDeltaPayload
    {
        public List<RtStoreStructureDelta> StructureChanges = new();
        public List<RtStoreComponentStructDelta> ComponentStructureChanges = new();
        public List<RtStoreComponentDelta> ComponentChanges = new();

        public bool HasAny =>
            (StructureChanges != null && StructureChanges.Count > 0) ||
            (ComponentStructureChanges != null && ComponentStructureChanges.Count > 0) ||
            (ComponentChanges != null && ComponentChanges.Count > 0);
    }

    internal static class RuntimeNetSerialization
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

    internal sealed class RuntimeStoreSnapshot
    {
        public readonly Dictionary<long, RuntimeStoreSnapshotNode> NodesById = new();
    }

    internal sealed class RuntimeStoreSnapshotNode
    {
        public long Id;
        public long ParentId;
        public int Index;
        public byte[] ObjectData;
        public readonly Dictionary<uint, byte[]> ComponentsByType = new();
    }

    internal static class RuntimeReplicationPolicy
    {
        public static bool ShouldReplicateObject(GameRuntimeObject obj, ReplicationMask mask)
        {
            if (obj == null)
                return false;

            var resolvedMask = ReplicationMask.All;
            var components = obj.Components;
            if (components == null)
                return true;

            for (var i = 0; i < components.Count; i++)
            {
                if (components[i] is IReplicatedObject replicated)
                    resolvedMask &= replicated.GetMask();
            }

            return (resolvedMask & mask) != 0;
        }

        public static bool ShouldReplicateComponent(GameRuntimeComponent component, ReplicationMask mask)
        {
            if (component == null)
                return false;

            var hasSnapshotTag = component is ISnapshotComponent;
            var hasDeltaTag = component is IDeltaComponent;

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
    }

    internal static class RuntimeStoreSnapshotCodec
    {
        public static RuntimeStoreSnapshot BuildSnapshot(RuntimeStore store)
        {
            var snapshot = new RuntimeStoreSnapshot();
            if (store == null)
                return snapshot;

            var roots = new List<long>(store.Parents.V.Keys);
            roots.Sort();

            for (var i = 0; i < roots.Count; i++)
            {
                BuildSnapshotRecursive(store, snapshot, roots[i], parentId: -1, index: i);
            }

            return snapshot;
        }

        public static RtStoreFullSnapshotPayload BuildFullPayload(RuntimeStoreSnapshot snapshot)
        {
            var payload = new RtStoreFullSnapshotPayload();
            if (snapshot == null || snapshot.NodesById.Count == 0)
                return payload;

            var ordered = TopologicalOrder(snapshot);
            for (var i = 0; i < ordered.Count; i++)
            {
                var node = ordered[i];
                payload.Objects.Add(new RtStoreSnapshotObjectData
                {
                    Id = node.Id,
                    ParentId = node.ParentId,
                    Index = node.Index,
                    Data = node.ObjectData,
                });
            }

            return payload;
        }

        public static RtStoreDeltaPayload BuildDeltaPayload(RuntimeStoreSnapshot baseline, RuntimeStoreSnapshot current)
        {
            var payload = new RtStoreDeltaPayload();
            if (current == null)
                return payload;

            baseline ??= new RuntimeStoreSnapshot();

            var removedIds = new HashSet<long>();
            foreach (var id in baseline.NodesById.Keys)
            {
                if (!current.NodesById.ContainsKey(id))
                    removedIds.Add(id);
            }

            var spawnedIds = new HashSet<long>();
            foreach (var id in current.NodesById.Keys)
            {
                if (!baseline.NodesById.ContainsKey(id))
                    spawnedIds.Add(id);
            }

            AppendRemoved(payload, baseline, removedIds);
            AppendSpawned(payload, current, spawnedIds);
            AppendReparentOrMove(payload, baseline, current, removedIds, spawnedIds);
            AppendComponentChanges(payload, baseline, current, removedIds, spawnedIds);

            return payload;
        }

        public static bool ApplyFullSnapshot(RuntimeStore store, RtStoreFullSnapshotPayload payload)
        {
            if (store == null)
                return false;

            ClearStore(store);

            if (payload?.Objects == null || payload.Objects.Count == 0)
                return true;

            var pending = new List<RtStoreSnapshotObjectData>(payload.Objects);

            var maxPasses = pending.Count * 2 + 2;
            for (var pass = 0; pass < maxPasses && pending.Count > 0; pass++)
            {
                var progressed = false;

                for (var i = pending.Count - 1; i >= 0; i--)
                {
                    var entry = pending[i];
                    if (entry.ParentId >= 0 && !store.TryTakeRO(entry.ParentId, out _))
                        continue;

                    if (ApplySpawn(store, entry.Id, entry.ParentId, entry.Index, entry.Data))
                    {
                        pending.RemoveAt(i);
                        progressed = true;
                    }
                }

                if (!progressed)
                    break;
            }

            return pending.Count == 0;
        }

        public static bool ApplyDelta(RuntimeStore store, RtStoreDeltaPayload payload)
        {
            if (store == null || payload == null)
                return false;

            if (payload.StructureChanges != null)
            {
                for (var i = 0; i < payload.StructureChanges.Count; i++)
                {
                    var change = payload.StructureChanges[i];
                    if (!ApplyStructureChange(store, in change))
                        return false;
                }
            }

            if (payload.ComponentStructureChanges != null)
            {
                for (var i = 0; i < payload.ComponentStructureChanges.Count; i++)
                {
                    var change = payload.ComponentStructureChanges[i];
                    if (!ApplyComponentStructChange(store, in change))
                        return false;
                }
            }

            if (payload.ComponentChanges != null)
            {
                for (var i = 0; i < payload.ComponentChanges.Count; i++)
                {
                    var change = payload.ComponentChanges[i];
                    if (!ApplyComponentChange(store, in change))
                        return false;
                }
            }

            return true;
        }

        private static void BuildSnapshotRecursive(RuntimeStore store, RuntimeStoreSnapshot snapshot, long id, long parentId, int index)
        {
            if (!store.TryTakeRO(id, out var obj) || obj == null)
                return;

            if (!RuntimeReplicationPolicy.ShouldReplicateObject(obj, ReplicationMask.Snapshot | ReplicationMask.Delta))
                return;

            var node = new RuntimeStoreSnapshotNode
            {
                Id = id,
                ParentId = parentId,
                Index = index,
                ObjectData = RuntimeNetSerialization.SerializeRuntimeObject(obj),
            };

            var components = obj.Components;
            if (components != null)
            {
                for (var i = 0; i < components.Count; i++)
                {
                    var component = components[i];
                    if (component == null)
                        continue;

                    if (!RuntimeReplicationPolicy.ShouldReplicateComponent(component, ReplicationMask.Snapshot | ReplicationMask.Delta))
                        continue;

                    if (!RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var compTypeId))
                        continue;

                    node.ComponentsByType[compTypeId] = RuntimeNetSerialization.SerializeRuntimeComponent(component);
                }
            }

            snapshot.NodesById[id] = node;

            if (!store.TryTakeChildren(id, out var children) || children == null)
                return;

            for (var i = 0; i < children.Count; i++)
            {
                BuildSnapshotRecursive(store, snapshot, children[i], id, i);
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

        private static void AppendRemoved(RtStoreDeltaPayload payload, RuntimeStoreSnapshot baseline, HashSet<long> removedIds)
        {
            if (removedIds.Count == 0)
                return;

            var removed = new List<long>(removedIds.Count);
            foreach (var id in removedIds)
            {
                if (!baseline.NodesById.TryGetValue(id, out var node))
                    continue;

                if (node.ParentId >= 0 && removedIds.Contains(node.ParentId))
                    continue;

                removed.Add(id);
            }

            var depthById = BuildDepthMap(baseline);
            removed.Sort((a, b) =>
            {
                depthById.TryGetValue(a, out var da);
                depthById.TryGetValue(b, out var db);
                var c = db.CompareTo(da);
                return c != 0 ? c : a.CompareTo(b);
            });

            for (var i = 0; i < removed.Count; i++)
            {
                payload.StructureChanges.Add(new RtStoreStructureDelta
                {
                    Kind = RuntimeStoreOpKind.Remove,
                    Id = removed[i],
                    ParentId = 0,
                    Index = -1,
                    RemoveMode = RemoveMode.Subtree,
                    SpawnData = null,
                });
            }
        }

        private static void AppendSpawned(RtStoreDeltaPayload payload, RuntimeStoreSnapshot current, HashSet<long> spawnedIds)
        {
            if (spawnedIds.Count == 0)
                return;

            var spawned = new List<long>(spawnedIds);
            var depthById = BuildDepthMap(current);
            spawned.Sort((a, b) =>
            {
                depthById.TryGetValue(a, out var da);
                depthById.TryGetValue(b, out var db);
                var c = da.CompareTo(db);
                return c != 0 ? c : a.CompareTo(b);
            });

            for (var i = 0; i < spawned.Count; i++)
            {
                var id = spawned[i];
                if (!current.NodesById.TryGetValue(id, out var node))
                    continue;

                payload.StructureChanges.Add(new RtStoreStructureDelta
                {
                    Kind = RuntimeStoreOpKind.Spawn,
                    Id = id,
                    ParentId = node.ParentId,
                    Index = node.Index,
                    RemoveMode = default,
                    SpawnData = node.ObjectData,
                });
            }
        }

        private static void AppendReparentOrMove(RtStoreDeltaPayload payload, RuntimeStoreSnapshot baseline, RuntimeStoreSnapshot current, HashSet<long> removedIds, HashSet<long> spawnedIds)
        {
            var ids = new List<long>();
            foreach (var id in current.NodesById.Keys)
            {
                if (!baseline.NodesById.ContainsKey(id))
                    continue;
                if (removedIds.Contains(id) || spawnedIds.Contains(id))
                    continue;
                ids.Add(id);
            }

            ids.Sort();

            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var prev = baseline.NodesById[id];
                var now = current.NodesById[id];

                if (prev.ParentId != now.ParentId)
                {
                    payload.StructureChanges.Add(new RtStoreStructureDelta
                    {
                        Kind = RuntimeStoreOpKind.Reparent,
                        Id = id,
                        ParentId = now.ParentId,
                        Index = now.Index,
                        RemoveMode = default,
                        SpawnData = null,
                    });
                    continue;
                }

                if (now.ParentId >= 0 && prev.Index != now.Index)
                {
                    payload.StructureChanges.Add(new RtStoreStructureDelta
                    {
                        Kind = RuntimeStoreOpKind.Move,
                        Id = id,
                        ParentId = now.ParentId,
                        Index = now.Index,
                        RemoveMode = default,
                        SpawnData = null,
                    });
                }
            }
        }

        private static void AppendComponentChanges(RtStoreDeltaPayload payload, RuntimeStoreSnapshot baseline, RuntimeStoreSnapshot current, HashSet<long> removedIds, HashSet<long> spawnedIds)
        {
            var ids = new List<long>();
            foreach (var id in current.NodesById.Keys)
            {
                if (!baseline.NodesById.ContainsKey(id))
                    continue;
                if (removedIds.Contains(id) || spawnedIds.Contains(id))
                    continue;
                ids.Add(id);
            }

            ids.Sort();

            for (var i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var prev = baseline.NodesById[id];
                var now = current.NodesById[id];

                AppendComponentDiffForObject(payload, id, prev.ComponentsByType, now.ComponentsByType);
            }
        }

        private static void AppendComponentDiffForObject(RtStoreDeltaPayload payload, long objectId, Dictionary<uint, byte[]> prev, Dictionary<uint, byte[]> now)
        {
            var removedCompIds = new List<uint>();
            foreach (var compId in prev.Keys)
            {
                if (!ShouldReplicateComponentType(compId, ReplicationMask.Delta))
                    continue;

                if (!now.ContainsKey(compId))
                    removedCompIds.Add(compId);
            }

            removedCompIds.Sort();
            for (var i = 0; i < removedCompIds.Count; i++)
            {
                payload.ComponentStructureChanges.Add(new RtStoreComponentStructDelta
                {
                    ObjectId = objectId,
                    CompTypeId = removedCompIds[i],
                    Kind = CompStructOpKind.Remove,
                    Payload = null,
                });
            }

            var addedCompIds = new List<uint>();
            foreach (var compId in now.Keys)
            {
                if (!ShouldReplicateComponentType(compId, ReplicationMask.Delta))
                    continue;

                if (!prev.ContainsKey(compId))
                    addedCompIds.Add(compId);
            }

            addedCompIds.Sort();
            for (var i = 0; i < addedCompIds.Count; i++)
            {
                var compId = addedCompIds[i];
                payload.ComponentStructureChanges.Add(new RtStoreComponentStructDelta
                {
                    ObjectId = objectId,
                    CompTypeId = compId,
                    Kind = CompStructOpKind.Add,
                    Payload = now[compId],
                });
            }

            var commonCompIds = new List<uint>();
            foreach (var compId in now.Keys)
            {
                if (!ShouldReplicateComponentType(compId, ReplicationMask.Delta))
                    continue;

                if (prev.ContainsKey(compId))
                    commonCompIds.Add(compId);
            }

            commonCompIds.Sort();
            for (var i = 0; i < commonCompIds.Count; i++)
            {
                var compId = commonCompIds[i];
                var prevPayload = prev[compId];
                var nowPayload = now[compId];

                if (BytesEqual(prevPayload, nowPayload))
                    continue;

                payload.ComponentChanges.Add(new RtStoreComponentDelta
                {
                    ObjectId = objectId,
                    CompTypeId = compId,
                    Payload = nowPayload,
                });
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

        private static Dictionary<long, int> BuildDepthMap(RuntimeStoreSnapshot snapshot)
        {
            var depthById = new Dictionary<long, int>(snapshot.NodesById.Count);
            foreach (var id in snapshot.NodesById.Keys)
            {
                ResolveDepth(snapshot, id, depthById);
            }

            return depthById;
        }

        private static int ResolveDepth(RuntimeStoreSnapshot snapshot, long id, Dictionary<long, int> depthById)
        {
            if (depthById.TryGetValue(id, out var depth))
                return depth;

            if (!snapshot.NodesById.TryGetValue(id, out var node) || node.ParentId < 0 || !snapshot.NodesById.ContainsKey(node.ParentId))
            {
                depthById[id] = 0;
                return 0;
            }

            depth = ResolveDepth(snapshot, node.ParentId, depthById) + 1;
            depthById[id] = depth;
            return depth;
        }

        private static int CompareByIndexThenId(RuntimeStoreSnapshotNode a, RuntimeStoreSnapshotNode b)
        {
            var c = a.Index.CompareTo(b.Index);
            if (c != 0)
                return c;
            return a.Id.CompareTo(b.Id);
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b))
                return true;

            if (a == null || b == null || a.Length != b.Length)
                return false;

            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i])
                    return false;
            }

            return true;
        }

        private static bool ShouldReplicateComponentType(uint compTypeId, ReplicationMask mask)
        {
            if (!RuntimeComponentTypeRegistry.TryGetType(compTypeId, out var compType) || compType == null)
                return true;

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
    }
}
#endif
