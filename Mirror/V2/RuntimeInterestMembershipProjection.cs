using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public readonly struct RuntimeInterestNode
    {
        public readonly NetObjectRef Object;
        public readonly bool HasParent;
        public readonly NetObjectRef Parent;
        public readonly long StoreOrder;
        public readonly bool DirectlyVisible;

        public RuntimeInterestNode(NetObjectRef value, long storeOrder, bool directlyVisible)
        {
            if (!value.IsValid)
                throw new ArgumentException("Interest node requires a valid object reference.", nameof(value));

            Object = value;
            HasParent = false;
            Parent = default;
            StoreOrder = storeOrder;
            DirectlyVisible = directlyVisible;
        }

        public RuntimeInterestNode(NetObjectRef value, NetObjectRef parent, long storeOrder, bool directlyVisible)
        {
            if (!value.IsValid)
                throw new ArgumentException("Interest node requires a valid object reference.", nameof(value));
            if (!parent.IsValid)
                throw new ArgumentException("Child interest node requires a valid parent reference.", nameof(parent));
            if (value == parent)
                throw new ArgumentException("Interest node cannot be its own parent.", nameof(parent));

            Object = value;
            HasParent = true;
            Parent = parent;
            StoreOrder = storeOrder;
            DirectlyVisible = directlyVisible;
        }
    }

    public class RuntimeInterestMembershipProjection
    {
        public readonly NetStoreRef Store;
        public readonly IReadOnlyList<NetObjectRef> DesiredMembership;
        public readonly IReadOnlyList<NetObjectRef> Enters;
        public readonly IReadOnlyList<NetObjectRef> Leaves;

        public RuntimeInterestMembershipProjection(
            NetStoreRef store,
            IReadOnlyList<NetObjectRef> desiredMembership,
            IReadOnlyList<NetObjectRef> enters,
            IReadOnlyList<NetObjectRef> leaves)
        {
            if (!store.IsValid)
                throw new ArgumentException("Interest projection requires a valid store reference.", nameof(store));

            Store = store;
            DesiredMembership = CopyImmutable(desiredMembership);
            Enters = CopyImmutable(enters);
            Leaves = CopyImmutable(leaves);
        }

        public RuntimeActiveBaselineTransfer BeginBaseline(RuntimeConnectionStoreReplicationState state, byte[] payload)
        {
            ValidateState(state);
            return state.BeginBaseline(payload, DesiredMembership);
        }

        public RuntimeConnectionDeltaEnqueueResult TryEnqueueDelta(
            RuntimeConnectionStoreReplicationState state,
            ulong fromRevision,
            ulong toRevision,
            byte[] payload,
            out RuntimeReliableEnvelope envelope)
        {
            ValidateState(state);
            return state.TryEnqueueDelta(fromRevision, toRevision, payload, Enters, Leaves, out envelope);
        }

        public RuntimeConnectionDeltaEnqueueResult TryEnqueueInterestDelta(
            RuntimeConnectionStoreReplicationState state,
            byte[] payload,
            out RuntimeReliableEnvelope envelope)
        {
            ValidateState(state);
            return state.TryEnqueueInterestDelta(payload, Enters, Leaves, out envelope);
        }

        private void ValidateState(RuntimeConnectionStoreReplicationState state)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (state.Store != Store)
                throw new InvalidOperationException($"Interest projection for store '{Store}' cannot be applied to connection store '{state.Store}'.");
        }

        private static IReadOnlyList<NetObjectRef> CopyImmutable(IReadOnlyList<NetObjectRef> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<NetObjectRef>();

            var copy = new NetObjectRef[values.Count];
            for (var i = 0; i < values.Count; i++)
            {
                copy[i] = values[i];
            }

            return Array.AsReadOnly(copy);
        }
    }

    public static class RuntimeInterestMembershipProjector
    {
        private const byte VISITING = 1;
        private const byte VISITED = 2;

        public static RuntimeInterestMembershipProjection Project(
            RuntimeConnectionStoreReplicationState state,
            IReadOnlyList<RuntimeInterestNode> nodes)
        {
            return Project(state, nodes, Array.Empty<RuntimeMembershipOrderItem>());
        }

        public static RuntimeInterestMembershipProjection Project(
            RuntimeConnectionStoreReplicationState state,
            IReadOnlyList<RuntimeInterestNode> nodes,
            IReadOnlyList<RuntimeMembershipOrderItem> departedProjectedObjects)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (nodes == null)
                throw new ArgumentNullException(nameof(nodes));
            if (departedProjectedObjects == null)
                throw new ArgumentNullException(nameof(departedProjectedObjects));

            var nodesByObject = BuildNodeLookup(state.Store, nodes);
            var depthByObject = CalculateDepths(nodesByObject);
            var orderedLiveObjects = OrderLiveObjects(nodes, depthByObject);
            var desired = CalculateAncestorClosedMembership(nodesByObject, orderedLiveObjects);
            var desiredLookup = new HashSet<NetObjectRef>(desired);
            var enters = CalculateEnters(state, desired, depthByObject, nodesByObject);
            var leaves = CalculateLeaves(
                state,
                nodes,
                desiredLookup,
                depthByObject,
                departedProjectedObjects,
                out var accountedProjectedObjects);

            if (accountedProjectedObjects != state.ProjectedMembershipCount)
            {
                throw new InvalidOperationException(
                    $"Interest projection accounted for {accountedProjectedObjects} of {state.ProjectedMembershipCount} projected objects. " +
                    "Pass the full live store topology and explicit order items for projected objects that departed the store.");
            }

            return new RuntimeInterestMembershipProjection(state.Store, desired, enters, leaves);
        }

        private static Dictionary<NetObjectRef, RuntimeInterestNode> BuildNodeLookup(
            NetStoreRef store,
            IReadOnlyList<RuntimeInterestNode> nodes)
        {
            var result = new Dictionary<NetObjectRef, RuntimeInterestNode>(nodes.Count);
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (!node.Object.IsValid || node.Object.Store != store)
                    throw new InvalidOperationException($"Interest node '{node.Object}' does not belong to connection store '{store}'.");
                if (node.HasParent && (!node.Parent.IsValid || node.Parent.Store != store))
                    throw new InvalidOperationException($"Parent '{node.Parent}' of interest node '{node.Object}' does not belong to connection store '{store}'.");
                if (!result.TryAdd(node.Object, node))
                    throw new InvalidOperationException($"Interest input contains duplicate object '{node.Object}'.");
            }

            foreach (var pair in result)
            {
                if (pair.Value.HasParent && !result.ContainsKey(pair.Value.Parent))
                    throw new InvalidOperationException($"Interest node '{pair.Key}' references missing parent '{pair.Value.Parent}'.");
            }

            return result;
        }

        private static Dictionary<NetObjectRef, int> CalculateDepths(
            IReadOnlyDictionary<NetObjectRef, RuntimeInterestNode> nodesByObject)
        {
            var result = new Dictionary<NetObjectRef, int>(nodesByObject.Count);
            var visitState = new Dictionary<NetObjectRef, byte>(nodesByObject.Count);
            foreach (var pair in nodesByObject)
            {
                CalculateDepth(pair.Key, nodesByObject, result, visitState);
            }

            return result;
        }

        private static int CalculateDepth(
            NetObjectRef value,
            IReadOnlyDictionary<NetObjectRef, RuntimeInterestNode> nodesByObject,
            IDictionary<NetObjectRef, int> depthByObject,
            IDictionary<NetObjectRef, byte> visitState)
        {
            if (depthByObject.TryGetValue(value, out var existingDepth))
                return existingDepth;
            if (visitState.TryGetValue(value, out var state) && state == VISITING)
                throw new InvalidOperationException($"Interest hierarchy contains a cycle at object '{value}'.");

            visitState[value] = VISITING;
            var node = nodesByObject[value];
            var depth = node.HasParent
                ? checked(CalculateDepth(node.Parent, nodesByObject, depthByObject, visitState) + 1)
                : 0;
            depthByObject[value] = depth;
            visitState[value] = VISITED;
            return depth;
        }

        private static IReadOnlyList<NetObjectRef> OrderLiveObjects(
            IReadOnlyList<RuntimeInterestNode> nodes,
            IReadOnlyDictionary<NetObjectRef, int> depthByObject)
        {
            var orderItems = new RuntimeMembershipOrderItem[nodes.Count];
            for (var i = 0; i < nodes.Count; i++)
            {
                orderItems[i] = new RuntimeMembershipOrderItem(
                    nodes[i].Object,
                    depthByObject[nodes[i].Object],
                    nodes[i].StoreOrder);
            }

            return RuntimeMembershipTransitionOrdering.OrderEntersParentFirst(orderItems);
        }

        private static IReadOnlyList<NetObjectRef> CalculateAncestorClosedMembership(
            IReadOnlyDictionary<NetObjectRef, RuntimeInterestNode> nodesByObject,
            IReadOnlyList<NetObjectRef> orderedLiveObjects)
        {
            var membership = new List<NetObjectRef>();
            var included = new HashSet<NetObjectRef>();
            for (var i = 0; i < orderedLiveObjects.Count; i++)
            {
                var node = nodesByObject[orderedLiveObjects[i]];
                if (!node.DirectlyVisible)
                    continue;
                if (node.HasParent && !included.Contains(node.Parent))
                    continue;

                included.Add(node.Object);
                membership.Add(node.Object);
            }

            return Array.AsReadOnly(membership.ToArray());
        }

        private static IReadOnlyList<NetObjectRef> CalculateEnters(
            RuntimeConnectionStoreReplicationState state,
            IReadOnlyList<NetObjectRef> desired,
            IReadOnlyDictionary<NetObjectRef, int> depthByObject,
            IReadOnlyDictionary<NetObjectRef, RuntimeInterestNode> nodesByObject)
        {
            var items = new List<RuntimeMembershipOrderItem>();
            for (var i = 0; i < desired.Count; i++)
            {
                var value = desired[i];
                if (state.IsProjected(value))
                    continue;

                items.Add(new RuntimeMembershipOrderItem(value, depthByObject[value], nodesByObject[value].StoreOrder));
            }

            return RuntimeMembershipTransitionOrdering.OrderEntersParentFirst(items);
        }

        private static IReadOnlyList<NetObjectRef> CalculateLeaves(
            RuntimeConnectionStoreReplicationState state,
            IReadOnlyList<RuntimeInterestNode> nodes,
            ISet<NetObjectRef> desired,
            IReadOnlyDictionary<NetObjectRef, int> depthByObject,
            IReadOnlyList<RuntimeMembershipOrderItem> departedProjectedObjects,
            out int accountedProjectedObjects)
        {
            var items = new List<RuntimeMembershipOrderItem>();
            var accounted = new HashSet<NetObjectRef>();
            var liveObjects = new HashSet<NetObjectRef>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                liveObjects.Add(node.Object);
                if (!state.IsProjected(node.Object))
                    continue;

                accounted.Add(node.Object);
                if (!desired.Contains(node.Object))
                    items.Add(new RuntimeMembershipOrderItem(node.Object, depthByObject[node.Object], node.StoreOrder));
            }

            for (var i = 0; i < departedProjectedObjects.Count; i++)
            {
                var item = departedProjectedObjects[i];
                if (item.Object.Store != state.Store)
                    throw new InvalidOperationException($"Departed object '{item.Object}' does not belong to connection store '{state.Store}'.");
                if (liveObjects.Contains(item.Object))
                    throw new InvalidOperationException($"Object '{item.Object}' is present in both live and departed interest inputs.");
                if (!state.IsProjected(item.Object))
                    continue;
                if (!accounted.Add(item.Object))
                    throw new InvalidOperationException($"Departed interest input contains duplicate projected object '{item.Object}'.");

                items.Add(item);
            }

            accountedProjectedObjects = accounted.Count;
            return RuntimeMembershipTransitionOrdering.OrderLeavesChildFirst(items);
        }
    }
}
