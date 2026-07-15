using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror.V2
{
    [Serializable, Preserve]
    public struct NetStoreRef : IEquatable<NetStoreRef>
    {
        public FixedString32Bytes StoreId;
        public uint StoreGeneration;

        public NetStoreRef(FixedString32Bytes storeId, uint storeGeneration)
        {
            StoreId = storeId;
            StoreGeneration = storeGeneration;
        }

        public bool IsValid => StoreId.Length > 0 && StoreGeneration != 0;

        public bool Equals(NetStoreRef other) => StoreId.Equals(other.StoreId) && StoreGeneration == other.StoreGeneration;
        public override bool Equals(object obj) => obj is NetStoreRef other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(StoreId, StoreGeneration);
        public override string ToString() => $"{StoreId}@{StoreGeneration}";

        public static bool operator ==(NetStoreRef left, NetStoreRef right) => left.Equals(right);
        public static bool operator !=(NetStoreRef left, NetStoreRef right) => !left.Equals(right);
    }

    [Serializable, Preserve]
    public struct NetObjectRef : IEquatable<NetObjectRef>
    {
        public NetStoreRef Store;
        public long ObjectId;

        public NetObjectRef(NetStoreRef store, long objectId)
        {
            Store = store;
            ObjectId = objectId;
        }

        public bool IsValid => Store.IsValid && ObjectId > RuntimeStore.STORE_ROOT_OBJECT_ID;

        public RuntimeInstance ToRuntimeInstance(uint localEpoch)
        {
            if (!IsValid)
                throw new InvalidOperationException($"Cannot resolve invalid network object reference '{this}'.");
            if (localEpoch == 0)
                throw new ArgumentOutOfRangeException(nameof(localEpoch), "Local RuntimeInstance epoch must be non-zero.");

            return new RuntimeInstance
            {
                StoreId = Store.StoreId,
                Id = ObjectId,
                Epoch = localEpoch,
            };
        }

        public static NetObjectRef FromRuntimeInstance(RuntimeInstance instance, uint storeGeneration)
        {
            var result = new NetObjectRef(new NetStoreRef(instance.StoreId, storeGeneration), instance.Id);
            if (!result.IsValid)
                throw new ArgumentException("RuntimeInstance cannot be represented as a network object reference.", nameof(instance));
            return result;
        }

        public bool Equals(NetObjectRef other) => Store.Equals(other.Store) && ObjectId == other.ObjectId;
        public override bool Equals(object obj) => obj is NetObjectRef other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Store, ObjectId);
        public override string ToString() => $"{Store}/{ObjectId}";

        public static bool operator ==(NetObjectRef left, NetObjectRef right) => left.Equals(right);
        public static bool operator !=(NetObjectRef left, NetObjectRef right) => !left.Equals(right);
    }
}
