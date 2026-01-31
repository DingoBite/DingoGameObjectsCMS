using System.Collections.Generic;
using Bind;
using DingoProjectAppStructure.Core.Model;
using DingoUnityExtensions;
using Unity.Entities;
using Hash128 = UnityEngine.Hash128;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public class RuntimeStore : AppModelBase
    {
        public readonly Hash128 Id;

        private long _lastId;

        private readonly BindDict<long, GameRuntimeObject> _items = new();

        private readonly Dictionary<long, GameRuntimeObject> _all = new();

        private readonly Dictionary<long, Entity> _entityById = new();

        private readonly Dictionary<long, long> _parentByChild = new();
        private readonly Dictionary<long, List<long>> _childrenByParent = new();

        private readonly HashSet<long> _added = new();
        private readonly HashSet<long> _removed = new();
        private readonly HashSet<long> _changed = new();
        
        private readonly HashSet<long> _cycleVisited = new();
        
        private bool _scheduled;

        public RuntimeStore(Hash128 id)
        {
            Id = id;
        }

        public IReadonlyBind<IReadOnlyDictionary<long, GameRuntimeObject>> Items => _items;

        public event System.Action<IReadOnlyCollection<long>> Added;
        public event System.Action<IReadOnlyCollection<long>> Removed;
        public event System.Action<IReadOnlyCollection<long>> Changed;

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

        public bool TryTakeRW(long id, out GameRuntimeObject gameRuntimeObject)
        {
            if (!_all.TryGetValue(id, out gameRuntimeObject))
                return false;

            MarkChangedUpToRoot(id);

            return true;
        }

        public bool TryTakeRO(long id, out GameRuntimeObject gameRuntimeObject) => _all.TryGetValue(id, out gameRuntimeObject);

        public bool Remove(long id) => Remove(id, out _);

        public bool Remove(long id, out Entity entity)
        {
            _entityById.Remove(id, out entity);

            if (!_all.TryGetValue(id, out var obj))
            {
                DetachChildInternal(id);
                RemoveChildrenIndexOnly(id);
                return false;
            }

            if (_items.V.ContainsKey(id))
            {
                RemoveFromRoot(id);
            }
            else
            {
                _added.Remove(id);
                _removed.Remove(id);
                _changed.Remove(id);
            }

            DetachChildInternal(id);

            if (_childrenByParent.TryGetValue(id, out var children) && children.Count > 0)
            {
                var toRemove = new List<long>(children);

                _childrenByParent.Remove(id);

                foreach (var t in toRemove)
                {
                    _parentByChild.Remove(t);
                }

                foreach (var t in toRemove)
                {
                    Remove(t);
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
            if (!_parentByChild.TryGetValue(childId, out var parentId) || !_items.V.TryGetValue(parentId, out parent))
            {
                parent = null;
                return false;
            }

            MarkChangedUpToRoot(parentId);

            return true;
        }
        
        public bool TryTakeParentRO(long childId, out GameRuntimeObject parent)
        {
            if (!_parentByChild.TryGetValue(childId, out var parentId) || !_items.V.TryGetValue(parentId, out parent))
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

            children = System.Array.Empty<long>();
            return false;
        }

        public bool AttachChild(long parentId, long childId, int insertIndex = -1)
        {
            if (parentId == childId)
                return false;

            if (!_all.ContainsKey(parentId))
                return false;

            if (!_all.ContainsKey(childId))
                return false;
            
            if (WouldCreateCycle(parentId, childId))
                return false; 
            
            if (_items.V.ContainsKey(childId))
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

            MarkChangedUpToRoot(parentId);

            return true;
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

            if (!_items.V.TryAdd(id, obj))
                return;

            if (!_removed.Remove(id))
            {
                _added.Add(id);
            }

            _changed.Remove(id);

            ScheduleFlush();
        }

        private void RemoveFromRoot(long id)
        {
            if (!_items.V.Remove(id))
                return;

            if (!_added.Remove(id))
            {
                _removed.Add(id);
            }

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
            if (!_items.V.ContainsKey(id))
                return;

            if (_removed.Contains(id))
                return;
            if (_added.Contains(id))
                return;

            _changed.Add(id);

            ScheduleFlush();
        }

        private void ScheduleFlush()
        {
            if (_scheduled)
                return;

            _scheduled = true;
            CoroutineParent.AddLateUpdater(this, Flush);
        }

        private void Flush()
        {
            _items.V = _items.V;

            if (_added.Count > 0)
                Added?.Invoke(_added);
            if (_removed.Count > 0)
                Removed?.Invoke(_removed);
            if (_changed.Count > 0)
                Changed?.Invoke(_changed);

            _added.Clear();
            _removed.Clear();
            _changed.Clear();

            _scheduled = false;
            CoroutineParent.RemoveLateUpdater(this);
        }
    }
}