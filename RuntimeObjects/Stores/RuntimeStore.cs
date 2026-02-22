using System;
using System.Collections.Generic;
using System.Linq;
using Bind;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoProjectAppStructure.Core.Model;
using DingoUnityExtensions;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Stores
{
    public class RuntimeStore : AppModelBase
    {
        public const int UPDATE_ORDER = 1_000_000;

        public readonly FixedString32Bytes Id;

        private long _lastId;
        private uint _order;

        private readonly BindDict<long, GameRuntimeObject> _parents = new();
        private readonly Dictionary<long, GameRuntimeObject> _all = new();

        private readonly Dictionary<long, Entity> _entityById = new();

        private readonly Dictionary<long, long> _parentByChild = new();
        private readonly Dictionary<long, List<long>> _childrenByParent = new();

        private readonly HashSet<long> _touched = new();
        private bool _scheduled;
        private bool _flushInProgress;
        private bool _rescheduleRequested;

        private readonly HashSet<long> _cycleVisited = new();

        private readonly List<RuntimeStructureDirty> _structureChanges = new();
        private readonly List<ObjectComponentDirty> _objectComponentsChanges = new();
        private readonly List<ObjectStructDirty> _objectStructureChanges = new();

        public RuntimeStore(FixedString32Bytes id) => Id = id;

        public IReadonlyBind<IReadOnlyDictionary<long, GameRuntimeObject>> Parents => _parents;

        public event Action<NativeArray<RuntimeStructureDirty>> StructureChanges;
        public event Action<NativeArray<ObjectStructDirty>> ComponentStructureChanges;
        public event Action<NativeArray<ObjectComponentDirty>> ComponentChanges;

        private uint NextOrder() => ++_order;

        private GameRuntimeObject CreateDetached()
        {
            var id = _lastId++;
            var obj = new GameRuntimeObject
            {
                InstanceId = id,
                StoreId = Id
            };

            _all[id] = obj;
            MarkTouchedUpToRoot(id);
            return obj;
        }

        public GameRuntimeObject Create()
        {
            var obj = CreateDetached();
            AddToRoot(obj.InstanceId);
            return obj;
        }

        public GameRuntimeObject CreateChild(long parentId, int insertIndex = -1)
        {
            var child = CreateDetached();
            AttachChild(parentId, child.InstanceId, insertIndex);
            return child;
        }

        public bool TryUpsertNetObject(GameRuntimeObject value)
        {
            if (value == null || value.InstanceId < 0)
                return false;

            var id = value.InstanceId;
            value.StoreId = Id;

            if (_all.TryGetValue(id, out var prev) && prev != null && !ReferenceEquals(prev, value))
                prev.Destroy();

            _all[id] = value;
            if (_lastId <= id)
                _lastId = id + 1;

            MarkTouchedUpToRoot(id);
            ScheduleFlush();
            return true;
        }

        public void PublishRootExisting(long id) => AddToRoot(id);

        public bool TryTakeRW(long id, out GameRuntimeObject gameRuntimeObject)
        {
            if (!_all.TryGetValue(id, out gameRuntimeObject))
                return false;

            MarkTouchedUpToRoot(id);
            return true;
        }

        public bool TryTakeRO(long id, out GameRuntimeObject gameRuntimeObject) => _all.TryGetValue(id, out gameRuntimeObject);

        public bool Remove(long id) => Remove(id, RemoveMode.Subtree, out _);
        public bool Remove(long id, out Entity entity) => Remove(id, RemoveMode.Subtree, out entity);

        public bool Remove(long id, RemoveMode mode, out Entity entity) => RemoveInternal(id, mode, out entity, recordOp: true);

        private bool RemoveInternal(long id, RemoveMode mode, out Entity entity, bool recordOp)
        {
            if (recordOp)
                RecordOp(RuntimeStructureDirty.Remove(id, mode, NextOrder()));

            _entityById.Remove(id, out entity);

            MarkTouchedUpToRoot(id);

            if (!_all.TryGetValue(id, out var obj))
            {
                DetachChildInternal(id);
                RemoveChildrenIndexOnly(id);
                return false;
            }

            _parents.V.Remove(id);

            _parentByChild.TryGetValue(id, out var parentId);
            DetachChildInternal(id);

            if (parentId != 0)
                MarkTouchedUpToRoot(parentId);

            if (_childrenByParent.TryGetValue(id, out var children) && children.Count > 0)
            {
                var toProcess = new List<long>(children);
                _childrenByParent.Remove(id);

                foreach (var child in toProcess)
                {
                    _parentByChild.Remove(child);
                }

                switch (mode)
                {
                    case RemoveMode.Subtree:
                        foreach (var child in toProcess)
                        {
                            RemoveInternal(child, RemoveMode.Subtree, out _, recordOp: false);
                        }

                        break;

                    case RemoveMode.NodeOnly_DetachChildrenToRoot:
                        foreach (var child in toProcess)
                        {
                            AddToRoot(child);
                        }

                        break;

                    case RemoveMode.NodeOnly_ReparentChildrenToParent when parentId != 0 && _all.ContainsKey(parentId):
                        foreach (var child in toProcess)
                        {
                            AttachChild(parentId, child, insertIndex: -1);
                        }

                        break;

                    case RemoveMode.NodeOnly_ReparentChildrenToParent:
                        foreach (var child in toProcess)
                        {
                            AddToRoot(child);
                        }

                        break;

                    default: throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
                }
            }

            obj.Destroy();
            _all.Remove(id);

            ScheduleFlush();
            return true;
        }

        public void LinkEntity(long id, Entity entity) => _entityById[id] = entity;
        public bool TryGetEntity(long id, out Entity e) => _entityById.TryGetValue(id, out e);

        public bool TryTakeParentRW(long childId, out GameRuntimeObject parent)
        {
            if (!_parentByChild.TryGetValue(childId, out var parentId) || !_parents.V.TryGetValue(parentId, out parent))
            {
                parent = null;
                return false;
            }

            MarkTouchedUpToRoot(parentId);
            return true;
        }

        public bool TryTakeParentRO(long childId, out GameRuntimeObject parent)
        {
            if (!_parentByChild.TryGetValue(childId, out var parentId) || !_parents.V.TryGetValue(parentId, out parent))
            {
                parent = null;
                return false;
            }

            return true;
        }

        public bool TryTakeChildren(long parentId, out IReadOnlyList<long> children)
        {
            if (_childrenByParent.TryGetValue(parentId, out var list))
            {
                children = list;
                return true;
            }

            children = Array.Empty<long>();
            return false;
        }

        public bool AttachChild(long parentId, long childId, int insertIndex = -1)
        {
            if (parentId == childId)
                return false;

            if (!_all.ContainsKey(parentId) || !_all.ContainsKey(childId))
                return false;

            if (WouldCreateCycle(parentId, childId))
                return false;

            var wasPublished = IsPublished(childId);

            _parents.V.Remove(childId);

            if (_parentByChild.TryGetValue(childId, out var oldParentId))
            {
                if (oldParentId == parentId)
                {
                    if (insertIndex >= 0)
                        return MoveChild(parentId, childId, insertIndex);

                    return true;
                }

                DetachChildInternal(childId);
                if (oldParentId != 0)
                    MarkTouchedUpToRoot(oldParentId);
            }

            _parentByChild[childId] = parentId;

            if (!_childrenByParent.TryGetValue(parentId, out var list))
            {
                list = new List<long>(capacity: 4);
                _childrenByParent[parentId] = list;
            }

            if (insertIndex < 0 || insertIndex > list.Count)
                insertIndex = list.Count;

            list.Insert(insertIndex, childId);

            RecordOp(wasPublished ? RuntimeStructureDirty.Reparent(childId, parentId, insertIndex, NextOrder()) : RuntimeStructureDirty.Spawn(childId, parentId, insertIndex, NextOrder()));

            MarkTouchedUpToRoot(parentId);
            ScheduleFlush();
            return true;
        }

        public bool DetachChild(long childId)
        {
            if (!DetachChildInternal(childId))
                return false;

            AddToRoot(childId);
            return true;
        }

        public bool MoveChild(long parentId, long childId, int newIndex)
        {
            if (!_childrenByParent.TryGetValue(parentId, out var list))
                return false;

            var oldIndex = list.IndexOf(childId);
            if (oldIndex < 0)
                return false;

            if (newIndex < 0)
                newIndex = 0;
            if (newIndex >= list.Count)
                newIndex = list.Count - 1;

            if (oldIndex == newIndex)
                return true;

            list.RemoveAt(oldIndex);
            if (newIndex > list.Count)
                newIndex = list.Count;
            list.Insert(newIndex, childId);

            RecordOp(RuntimeStructureDirty.Move(childId, parentId, newIndex, NextOrder()));

            MarkTouchedUpToRoot(parentId);
            ScheduleFlush();
            return true;
        }

        private bool IsPublished(long id) => _parents.V.ContainsKey(id) || _parentByChild.ContainsKey(id);

        private void RecordOp(in RuntimeStructureDirty op)
        {
            _structureChanges.Add(op);
            ScheduleFlush();
        }

        private bool WouldCreateCycle(long newParentId, long childId)
        {
            _cycleVisited.Clear();

            var cur = newParentId;
            while (true)
            {
                if (cur == childId)
                    return true;

                if (!_cycleVisited.Add(cur))
                    return true;

                if (!_parentByChild.TryGetValue(cur, out var p))
                    return false;

                if (p == cur)
                    return true;

                cur = p;
            }
        }

        private bool DetachChildInternal(long childId)
        {
            if (!_parentByChild.Remove(childId, out var parentId))
                return false;

            if (_childrenByParent.TryGetValue(parentId, out var list))
            {
                var idx = list.IndexOf(childId);
                if (idx >= 0)
                    list.RemoveAt(idx);

                if (list.Count == 0)
                    _childrenByParent.Remove(parentId);
            }

            if (parentId != 0)
                MarkTouchedUpToRoot(parentId);
            MarkTouchedUpToRoot(childId);
            return true;
        }

        private void RemoveChildrenIndexOnly(long parentId)
        {
            if (!_childrenByParent.Remove(parentId, out var list))
                return;

            foreach (var c in list)
            {
                _parentByChild.Remove(c);
            }
        }

        private void AddToRoot(long id)
        {
            if (!_all.TryGetValue(id, out var obj))
                return;

            var wasPublished = IsPublished(id);

            if (!_parents.V.TryAdd(id, obj))
                return;

            RecordOp(wasPublished ? RuntimeStructureDirty.Reparent(id, -1, -1, NextOrder()) : RuntimeStructureDirty.Spawn(id, -1, -1, NextOrder()));

            MarkTouchedUpToRoot(id);
            ScheduleFlush();
        }

        private long GetRootId(long id)
        {
            var cur = id;

            for (var i = 0; i < 1024; i++)
            {
                if (!_parentByChild.TryGetValue(cur, out var p))
                    return cur;

                if (p == cur)
                    return cur;

                cur = p;
            }

            return cur;
        }

        private void MarkTouchedUpToRoot(long id)
        {
            var rootId = GetRootId(id);
            if (rootId != id)
                _touched.Add(id);

            _touched.Add(rootId);
            ScheduleFlush();
        }

        private void ScheduleFlush()
        {
            if (_scheduled)
            {
                if (_flushInProgress)
                    _rescheduleRequested = true;
                return;
            }

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, Flush, UPDATE_ORDER);
        }

        private void Flush()
        {
            _flushInProgress = true;
            try
            {
                _parents.V = _parents.V;

                NativeList<RuntimeStructureDirty> emitStructure = default;
                if (_structureChanges.Count > 0)
                {
                    emitStructure = new NativeList<RuntimeStructureDirty>(_structureChanges.Count, Allocator.Temp);
                    foreach (var c in _structureChanges)
                    {
                        emitStructure.Add(c);
                    }

                    _structureChanges.Clear();

                    if (emitStructure.Length > 1)
                        emitStructure.AsArray().Sort(new RuntimeStructureDirtyComparer());
                }

                NativeList<long> touchedIds = default;
                if (_touched.Count > 0)
                {
                    touchedIds = new NativeList<long>(_touched.Count, Allocator.Temp);
                    foreach (var id in _touched)
                    {
                        touchedIds.Add(id);
                    }

                    _touched.Clear();

                    touchedIds.AsArray().Sort(new LongComparer());
                }

                NativeList<ObjectStructDirty> emitCompStruct = default;
                NativeList<ObjectComponentDirty> emitComp = default;

                if (touchedIds.IsCreated && touchedIds.Length > 0)
                {
                    emitCompStruct = new NativeList<ObjectStructDirty>(touchedIds.Length * 2, Allocator.Temp);
                    emitComp = new NativeList<ObjectComponentDirty>(touchedIds.Length * 2, Allocator.Temp);

                    foreach (var id in touchedIds)
                    {
                        if (!_all.TryGetValue(id, out var obj))
                            continue;

                        var structChanges = obj.StructureChanges;
                        var compChanges = obj.ComponentsChanges;

                        var hadAny = false;

                        if (structChanges != null && structChanges.Count > 0)
                        {
                            foreach (var kv in structChanges)
                            {
                                emitCompStruct.Add(new ObjectStructDirty(id, kv.Value));
                            }

                            hadAny = true;
                        }

                        if (compChanges != null && compChanges.Count > 0)
                        {
                            foreach (var kv in compChanges)
                            {
                                var compTypeId = kv.Key;

                                if (structChanges != null && structChanges.TryGetValue(compTypeId, out var sd) && sd.Kind == CompStructOpKind.Remove)
                                    continue;

                                emitComp.Add(new ObjectComponentDirty(id, kv.Value));
                            }

                            hadAny = true;
                        }

                        if (hadAny)
                            obj.ClearDirty();
                    }

                    if (emitCompStruct.Length > 1)
                        emitCompStruct.AsArray().Sort(new ObjectStructDirtyComparer());

                    if (emitComp.Length > 1)
                        emitComp.AsArray().Sort(new ObjectComponentDirtyComparer());
                }

                if (emitStructure.IsCreated && emitStructure.Length > 0)
                    StructureChanges?.Invoke(emitStructure.AsArray());

                if (emitCompStruct.IsCreated && emitCompStruct.Length > 0)
                    ComponentStructureChanges?.Invoke(emitCompStruct.AsArray());

                if (emitComp.IsCreated && emitComp.Length > 0)
                    ComponentChanges?.Invoke(emitComp.AsArray());

                if (emitStructure.IsCreated)
                    emitStructure.Dispose();
                if (touchedIds.IsCreated)
                    touchedIds.Dispose();
                if (emitCompStruct.IsCreated)
                    emitCompStruct.Dispose();
                if (emitComp.IsCreated)
                    emitComp.Dispose();
            }
            finally
            {
                _flushInProgress = false;
                var needReschedule = _rescheduleRequested;
                _rescheduleRequested = false;

                _scheduled = false;
                CoroutineParent.RemoveLateUpdater(this);

                if (needReschedule)
                    ScheduleFlush();
            }
        }
    }
}
