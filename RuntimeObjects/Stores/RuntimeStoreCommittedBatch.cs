using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Stores
{
    public readonly struct RuntimeStoreStructureChange
    {
        public const long NO_PARENT_ID = -1;
        public const int NO_INDEX = -1;

        public readonly RuntimeStoreOpKind Kind;
        public readonly long Id;
        public readonly Hash128 ObjectGuid;
        public readonly long OldParentId;
        public readonly int OldIndex;
        public readonly long NewParentId;
        public readonly int NewIndex;
        public readonly RemoveMode RemoveMode;
        public readonly uint Order;

        private RuntimeStoreStructureChange(RuntimeStoreOpKind kind, long id, Hash128 objectGuid, long oldParentId, int oldIndex, long newParentId, int newIndex, RemoveMode removeMode, uint order)
        {
            Kind = kind;
            Id = id;
            ObjectGuid = objectGuid;
            OldParentId = oldParentId;
            OldIndex = oldIndex;
            NewParentId = newParentId;
            NewIndex = newIndex;
            RemoveMode = removeMode;
            Order = order;
        }

        public static RuntimeStoreStructureChange Spawn(long id, Hash128 objectGuid, long parentId, int index, uint order)
            => new(RuntimeStoreOpKind.Spawn, id, objectGuid, NO_PARENT_ID, NO_INDEX, parentId, index, default, order);

        public static RuntimeStoreStructureChange Reparent(long id, Hash128 objectGuid, long oldParentId, int oldIndex, long newParentId, int newIndex, uint order)
            => new(RuntimeStoreOpKind.Reparent, id, objectGuid, oldParentId, oldIndex, newParentId, newIndex, default, order);

        public static RuntimeStoreStructureChange Move(long id, Hash128 objectGuid, long parentId, int oldIndex, int newIndex, uint order)
            => new(RuntimeStoreOpKind.Move, id, objectGuid, parentId, oldIndex, parentId, newIndex, default, order);

        public static RuntimeStoreStructureChange Remove(long id, Hash128 objectGuid, long oldParentId, int oldIndex, RemoveMode mode, uint order)
            => new(RuntimeStoreOpKind.Remove, id, objectGuid, oldParentId, oldIndex, NO_PARENT_ID, NO_INDEX, mode, order);

        public RuntimeStructureDirty ToRuntimeStructureDirty()
        {
            return Kind switch
            {
                RuntimeStoreOpKind.Spawn => RuntimeStructureDirty.Spawn(Id, NewParentId, NewIndex, Order),
                RuntimeStoreOpKind.Reparent => RuntimeStructureDirty.Reparent(Id, NewParentId, NewIndex, Order),
                RuntimeStoreOpKind.Move => RuntimeStructureDirty.Move(Id, NewParentId, NewIndex, Order),
                RuntimeStoreOpKind.Remove => RuntimeStructureDirty.Remove(Id, RemoveMode, Order),
                _ => throw new ArgumentOutOfRangeException(nameof(Kind), Kind, null)
            };
        }
    }

    public readonly struct RuntimeStoreStructureChangeComparer : IComparer<RuntimeStoreStructureChange>
    {
        public int Compare(RuntimeStoreStructureChange a, RuntimeStoreStructureChange b) => a.Order.CompareTo(b.Order);
    }

    // NativeArray fields are valid only for the duration of the CommittedBatch callback.
    public readonly struct RuntimeStoreCommittedBatch
    {
        public readonly FixedString32Bytes StoreId;
        public readonly StoreRealm Realm;
        public readonly uint StoreGeneration;
        public readonly ulong StoreRevision;
        public readonly bool ReplicationSuppressed;
        public readonly NativeArray<RuntimeStoreStructureChange> StructureChanges;
        public readonly NativeArray<ObjectStructDirty> ComponentStructureChanges;
        public readonly NativeArray<ObjectComponentDirty> ComponentChanges;

        public RuntimeStoreCommittedBatch(FixedString32Bytes storeId, StoreRealm realm, uint storeGeneration, ulong storeRevision, bool replicationSuppressed, NativeArray<RuntimeStoreStructureChange> structureChanges, NativeArray<ObjectStructDirty> componentStructureChanges, NativeArray<ObjectComponentDirty> componentChanges)
        {
            StoreId = storeId;
            Realm = realm;
            StoreGeneration = storeGeneration;
            StoreRevision = storeRevision;
            ReplicationSuppressed = replicationSuppressed;
            StructureChanges = structureChanges;
            ComponentStructureChanges = componentStructureChanges;
            ComponentChanges = componentChanges;
        }
    }
}
