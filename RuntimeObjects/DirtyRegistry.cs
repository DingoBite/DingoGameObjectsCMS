using System;
using System.Collections.Generic;
using DingoUnityExtensions;
using Unity.Collections;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public interface IDirtyCollectable
    {
        public void PrepareForDelta();
        public DirtyItem ConstructDelta(DirtyKey dirtyKey);
    }
    
    public static class DirtyRegistry
    {
        private static readonly object UpdateObject = new();

        private static readonly Dictionary<FixedString32Bytes, List<DirtyKey>> DirtyByStore = new();
        private static readonly Dictionary<FixedString32Bytes, List<CompStructOp>> DirtyStructureByStore = new();

        private static readonly List<FixedString32Bytes> DirtyStoreOrder = new(16);
        private static readonly List<FixedString32Bytes> DirtyStructureStoreOrder = new(16);

        private static readonly List<DirtyItem> DirtyChangesBuffer = new(256);
        private static readonly List<CompStructOp> DirtyStructureChangesBuffer = new(256);

        private static bool _scheduled;

        public static event Action<IReadOnlyList<DirtyItem>> DirtyChanges;
        public static event Action<IReadOnlyList<CompStructOp>> DirtyStructureChanges;

        public static void MarkDirtyStructure(GameRuntimeObject obj, CompStructOpKind kind, uint compTypeId)
        {
            var storeId = obj.StoreId;
            if (!DirtyStructureByStore.TryGetValue(storeId, out var bucket))
            {
                bucket = new List<CompStructOp>();
                DirtyStructureByStore[storeId] = bucket;
            }

            if (!DirtyStructureStoreOrder.Contains(storeId))
                DirtyStructureStoreOrder.Add(storeId);
            
            bucket.Add(new CompStructOp(obj.StoreId, obj.InstanceId, compTypeId, kind));
            
            ScheduleFlush();
        }
        
        public static void MarkDirty(GameRuntimeObject obj, uint compTypeId)
        {
            var storeId = obj.StoreId;
            if (!DirtyByStore.TryGetValue(storeId, out var bucket))
            {
                bucket = new List<DirtyKey>();
                DirtyByStore[storeId] = bucket;
            }

            if (!DirtyStoreOrder.Contains(storeId))
                DirtyStoreOrder.Add(storeId);

            bucket.Add(new DirtyKey(obj, compTypeId));

            ScheduleFlush();
        }

        public static void Clear()
        {
            DirtyByStore.Clear();
            DirtyStructureByStore.Clear();
            DirtyStoreOrder.Clear();
            DirtyStructureStoreOrder.Clear();
            DirtyChangesBuffer.Clear();
            DirtyStructureChangesBuffer.Clear();
            _scheduled = false;
            CoroutineParent.RemoveLateUpdater(UpdateObject);
        }

        public static void FlushNow()
        {
            if (_scheduled)
            {
                _scheduled = false;
                CoroutineParent.RemoveLateUpdater(UpdateObject);
            }

            Flush();
        }

        private static void ScheduleFlush()
        {
            if (_scheduled)
                return;

            _scheduled = true;
            CoroutineParent.AddLateUpdater(UpdateObject, Flush, RuntimeStore.UPDATE_ORDER + 1);
        }

        private static void Flush()
        {
            DirtyChangesBuffer.Clear();
            DirtyStructureChangesBuffer.Clear();

            foreach (var storeId in DirtyStructureStoreOrder)
            {
                if (!DirtyStructureByStore.TryGetValue(storeId, out var bucket))
                    continue;

                foreach (var compStructOp in bucket)
                {
                    DirtyStructureChangesBuffer.Add(compStructOp);
                }

                if (bucket.Count == 0)
                    DirtyStructureByStore.Remove(storeId);
                bucket.Clear();
            }

            DirtyStructureStoreOrder.Clear();

            if (DirtyStructureChangesBuffer.Count > 0)
                DirtyStructureChanges?.Invoke(DirtyStructureChangesBuffer);

            DirtyStructureChangesBuffer.Clear();
            
            foreach (var storeId in DirtyStoreOrder)
            {
                if (!DirtyByStore.TryGetValue(storeId, out var bucket))
                    continue;

                foreach (var dirtyKey in bucket)
                {
                    var component = dirtyKey.Obj.GetById(dirtyKey.CompTypeId);
                    if (component is not IDirtyCollectable dirtyCollectable)
                        continue;
                    var dirtyItem = dirtyCollectable.ConstructDelta(dirtyKey);
                    if (dirtyItem.Delta.Length > 0)
                        DirtyChangesBuffer.Add(dirtyItem);
                }

                if (bucket.Count == 0)
                    DirtyByStore.Remove(storeId);
                bucket.Clear();
            }

            DirtyStoreOrder.Clear();

            if (DirtyChangesBuffer.Count > 0)
                DirtyChanges?.Invoke(DirtyChangesBuffer);

            DirtyChangesBuffer.Clear();

            _scheduled = false;
            CoroutineParent.RemoveLateUpdater(UpdateObject);
        }
    }
}