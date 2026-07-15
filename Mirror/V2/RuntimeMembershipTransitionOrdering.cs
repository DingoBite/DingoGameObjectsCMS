using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public readonly struct RuntimeMembershipOrderItem
    {
        public readonly NetObjectRef Object;
        public readonly int HierarchyDepth;
        public readonly long StoreOrder;

        public RuntimeMembershipOrderItem(NetObjectRef value, int hierarchyDepth, long storeOrder)
        {
            if (!value.IsValid)
                throw new ArgumentException("Membership order item requires a valid object reference.", nameof(value));
            if (hierarchyDepth < 0)
                throw new ArgumentOutOfRangeException(nameof(hierarchyDepth));

            Object = value;
            HierarchyDepth = hierarchyDepth;
            StoreOrder = storeOrder;
        }
    }

    public static class RuntimeMembershipTransitionOrdering
    {
        public static IReadOnlyList<NetObjectRef> OrderEntersParentFirst(IEnumerable<RuntimeMembershipOrderItem> items)
        {
            return Order(items, childFirst: false);
        }

        public static IReadOnlyList<NetObjectRef> OrderLeavesChildFirst(IEnumerable<RuntimeMembershipOrderItem> items)
        {
            return Order(items, childFirst: true);
        }

        private static IReadOnlyList<NetObjectRef> Order(IEnumerable<RuntimeMembershipOrderItem> items, bool childFirst)
        {
            if (items == null)
                throw new ArgumentNullException(nameof(items));

            var ordered = new List<RuntimeMembershipOrderItem>(items);
            ordered.Sort((left, right) => Compare(left, right, childFirst));

            var seen = new HashSet<NetObjectRef>();
            var result = new NetObjectRef[ordered.Count];
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!seen.Add(ordered[i].Object))
                    throw new InvalidOperationException($"Membership transition contains duplicate object '{ordered[i].Object}'.");

                result[i] = ordered[i].Object;
            }

            return Array.AsReadOnly(result);
        }

        private static int Compare(RuntimeMembershipOrderItem left, RuntimeMembershipOrderItem right, bool childFirst)
        {
            var depth = childFirst
                ? right.HierarchyDepth.CompareTo(left.HierarchyDepth)
                : left.HierarchyDepth.CompareTo(right.HierarchyDepth);
            if (depth != 0)
                return depth;

            var storeOrder = left.StoreOrder.CompareTo(right.StoreOrder);
            if (storeOrder != 0)
                return storeOrder;

            return left.Object.ObjectId.CompareTo(right.Object.ObjectId);
        }
    }
}
