using System;
using System.Collections.Generic;
using Bind;
using DingoProjectAppStructure.Core.Model;
using DingoUnityExtensions;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public class RuntimeStore : AppModelBase
    {
        public const int UPDATE_ORDER = 1_000_000;
        
        public readonly FixedString32Bytes Id;

        private long _lastId;

        private readonly BindDict<long, GameRuntimeObject> _parents = new();
        private readonly Dictionary<long, GameRuntimeObject> _all = new();

        private readonly Dictionary<long, Entity> _entityById = new();

        private readonly Dictionary<long, long> _parentByChild = new();
        private readonly Dictionary<long, List<long>> _childrenByParent = new();

        private readonly HashSet<long> _added = new();
        private readonly HashSet<long> _removed = new();
        private readonly HashSet<long> _changed = new();

        private readonly HashSet<long> _cycleVisited = new();

        private readonly List<RuntimeStoreOp> _ops = new(capacity: 32);

        private bool _scheduled;

        public RuntimeStore(FixedString32Bytes id) => Id = id;

        public IReadonlyBind<IReadOnlyDictionary<long, GameRuntimeObject>> Parents => _parents;

        public event Action<IReadOnlyCollection<long>> Added;
        public event Action<IReadOnlyCollection<long>> Removed;
        public event Action<IReadOnlyCollection<long>> Changed;

        public event Action<IReadOnlyList<RuntimeStoreOp>> OpsFlushed;

        public GameRuntimeObject Create()
        {
            var obj = CreateDetached();
            AddToRoot(obj.InstanceId);
            return obj;
        }

        public GameRuntimeObject CreateDetached()
        {
            var id = _lastId++;
            var obj = new GameRuntimeObject
            {
                InstanceId = id,
                StoreId = Id
            };

            _all[id] = obj;
            return obj;
        }

        public GameRuntimeObject CreateChild(long parentId, int insertIndex = -1)
        {
            var child = CreateDetached();
            AttachChild(parentId, child.InstanceId, insertIndex);
            return child;
        }

        public GameRuntimeObject CreateNet(long serverId)
        {
            if (_all.TryGetValue(serverId, out var net))
                return net;

            if (serverId >= _lastId)
                _lastId = serverId + 1;

            var obj = new GameRuntimeObject
            {
                InstanceId = serverId,
                StoreId = Id
            };
            _all[serverId] = obj;
            return obj;
        }

        public void PublishRootExisting(long id) => AddToRoot(id);

        public bool TryTakeRW(long id, out GameRuntimeObject gameRuntimeObject)
        {
            if (!_all.TryGetValue(id, out gameRuntimeObject))
                return false;

            MarkChangedUpToRoot(id);
            return true;
        }

        public bool TryTakeRO(long id, out GameRuntimeObject gameRuntimeObject) => _all.TryGetValue(id, out gameRuntimeObject);

        public bool Remove(long id) => Remove(id, RemoveMode.Subtree, out _);
        public bool Remove(long id, out Entity entity) => Remove(id, RemoveMode.Subtree, out entity);

        public bool Remove(long id, RemoveMode mode, out Entity entity) => RemoveInternal(id, mode, out entity, recordOp: true);

        private bool RemoveInternal(long id, RemoveMode mode, out Entity entity, bool recordOp)
        {
            if (recordOp)
                RecordOp(RuntimeStoreOp.Remove(id, mode));

            _entityById.Remove(id, out entity);

            if (!_all.TryGetValue(id, out var obj))
            {
                DetachChildInternal(id);
                RemoveChildrenIndexOnly(id);
                return false;
            }

            if (_parents.V.ContainsKey(id))
            {
                RemoveFromRoot(id);
            }
            else
            {
                _added.Remove(id);
                _removed.Remove(id);
                _changed.Remove(id);
            }

            _parentByChild.TryGetValue(id, out var parentId);
            DetachChildInternal(id);

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

            MarkChangedUpToRoot(parentId);
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

            if (_parents.V.ContainsKey(childId))
                RemoveFromRoot(childId);

            if (_parentByChild.TryGetValue(childId, out var oldParentId))
            {
                if (oldParentId == parentId)
                {
                    if (insertIndex >= 0)
                        return MoveChild(parentId, childId, insertIndex);

                    return true;
                }

                DetachChildInternal(childId);
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

            RecordOp(wasPublished ? RuntimeStoreOp.Reparent(childId, parentId, insertIndex) : RuntimeStoreOp.Spawn(childId, parentId, insertIndex));

            MarkChangedUpToRoot(parentId);
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

            RecordOp(RuntimeStoreOp.Move(childId, parentId, newIndex));
            MarkChangedUpToRoot(parentId);
            return true;
        }

        private bool IsPublished(long id) => _parents.V.ContainsKey(id) || _parentByChild.ContainsKey(id);

        private void RecordOp(in RuntimeStoreOp op)
        {
            _ops.Add(op);

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

            MarkChangedUpToRoot(parentId);
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

            if (!_removed.Remove(id))
                _added.Add(id);

            _changed.Remove(id);

            RecordOp(wasPublished ? RuntimeStoreOp.Reparent(id, parentId: -1, insertIndex: -1) : RuntimeStoreOp.Spawn(id, parentId: -1, insertIndex: -1));

            ScheduleFlush();
        }

        private void RemoveFromRoot(long id)
        {
            if (!_parents.V.Remove(id))
                return;

            if (!_added.Remove(id))
                _removed.Add(id);

            _changed.Remove(id);

            ScheduleFlush();
        }

        private void MarkChangedUpToRoot(long id)
        {
            var rootId = GetRootId(id);
            MarkChangedRootOnly(rootId);
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

        private void MarkChangedRootOnly(long id)
        {
            if (!_parents.V.ContainsKey(id))
                return;

            if (_removed.Contains(id) || _added.Contains(id))
                return;

            _changed.Add(id);
            ScheduleFlush();
        }

        private void ScheduleFlush()
        {
            if (_scheduled)
                return;

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, Flush, UPDATE_ORDER);
        }

        private void Flush()
        {
            _parents.V = _parents.V;

            if (_added.Count > 0)
                Added?.Invoke(_added);
            if (_removed.Count > 0)
                Removed?.Invoke(_removed);
            if (_changed.Count > 0)
                Changed?.Invoke(_changed);

            _added.Clear();
            _removed.Clear();
            _changed.Clear();

            if (_ops.Count > 0)
                OpsFlushed?.Invoke(_ops);

            _ops.Clear();

            _scheduled = false;
            CoroutineParent.RemoveLateUpdater(this);
        }
    }
}