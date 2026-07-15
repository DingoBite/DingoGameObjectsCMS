using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public class RuntimeProjectedStoreNode
    {
        public readonly long ObjectId;
        public readonly long ParentObjectId;
        public readonly int SiblingIndex;
        public readonly int Depth;
        public readonly long StoreOrder;

        public RuntimeProjectedStoreNode(long objectId, long parentObjectId, int siblingIndex, int depth, long storeOrder)
        {
            ObjectId = objectId;
            ParentObjectId = parentObjectId;
            SiblingIndex = siblingIndex;
            Depth = depth;
            StoreOrder = storeOrder;
        }
    }

    public class RuntimeProjectedStoreSnapshot
    {
        private readonly Dictionary<long, RuntimeProjectedStoreNode> _nodesById;

        public readonly IReadOnlyList<RuntimeProjectedStoreNode> Nodes;

        public int Count => Nodes.Count;

        public RuntimeProjectedStoreSnapshot(IReadOnlyList<RuntimeProjectedStoreNode> nodes)
        {
            Nodes = nodes ?? throw new ArgumentNullException(nameof(nodes));
            _nodesById = new Dictionary<long, RuntimeProjectedStoreNode>(nodes.Count);
            for (var i = 0; i < nodes.Count; i++)
            {
                if (!_nodesById.TryAdd(nodes[i].ObjectId, nodes[i]))
                    throw new InvalidOperationException($"Projected store contains duplicate object {nodes[i].ObjectId}.");
            }
        }

        public bool TryGet(long objectId, out RuntimeProjectedStoreNode node)
        {
            return _nodesById.TryGetValue(objectId, out node);
        }
    }

    public static class RuntimeProjectedStoreSnapshotBuilder
    {
        public static RuntimeProjectedStoreSnapshot Build(
            RuntimeStore store,
            int connectionId,
            RuntimeObjectVisibility visibility)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (visibility == null)
                throw new ArgumentNullException(nameof(visibility));

            var nodes = new List<RuntimeProjectedStoreNode>();
            var order = 0L;
            var roots = CollectTopLevel(store);
            var rootSibling = 0;
            for (var i = 0; i < roots.Count; i++)
            {
                var objectId = roots[i];
                if (!visibility(connectionId, store, objectId))
                    continue;

                AppendVisibleSubtree(
                    store,
                    connectionId,
                    visibility,
                    objectId,
                    RuntimeStoreStructureChange.NO_PARENT_ID,
                    rootSibling++,
                    depth: 0,
                    nodes,
                    ref order);
            }

            return new RuntimeProjectedStoreSnapshot(Array.AsReadOnly(nodes.ToArray()));
        }

        private static List<long> CollectTopLevel(RuntimeStore store)
        {
            var result = new List<long>();
            var roots = new List<long>(store.Parents.V.Keys);
            roots.Sort();
            for (var i = 0; i < roots.Count; i++)
            {
                var rootId = roots[i];
                if (rootId != RuntimeStore.STORE_ROOT_OBJECT_ID)
                {
                    result.Add(rootId);
                    continue;
                }

                if (!store.TryTakeChildren(rootId, out var storeRootChildren) || storeRootChildren == null)
                    continue;
                for (var childIndex = 0; childIndex < storeRootChildren.Count; childIndex++)
                {
                    result.Add(storeRootChildren[childIndex]);
                }
            }

            return result;
        }

        private static void AppendVisibleSubtree(
            RuntimeStore store,
            int connectionId,
            RuntimeObjectVisibility visibility,
            long objectId,
            long parentObjectId,
            int siblingIndex,
            int depth,
            List<RuntimeProjectedStoreNode> output,
            ref long order)
        {
            if (!store.TryTakeRO(objectId, out _))
                throw new InvalidOperationException($"RuntimeStore '{store.Id}' hierarchy references missing object {objectId}.");

            output.Add(new RuntimeProjectedStoreNode(objectId, parentObjectId, siblingIndex, depth, order++));
            if (!store.TryTakeChildren(objectId, out var children) || children == null)
                return;

            var projectedSibling = 0;
            for (var i = 0; i < children.Count; i++)
            {
                var childId = children[i];
                if (!visibility(connectionId, store, childId))
                    continue;

                AppendVisibleSubtree(
                    store,
                    connectionId,
                    visibility,
                    childId,
                    objectId,
                    projectedSibling++,
                    checked(depth + 1),
                    output,
                    ref order);
            }
        }
    }

    public class RuntimeConnectionObjectShadow
    {
        public long ParentObjectId;
        public int SiblingIndex;
        public int Depth;
        public long StoreOrder;
        public Dictionary<uint, GameRuntimeComponent> ReliableComponents;
        public Dictionary<uint, GameRuntimeComponent> SpawnVisibleComponents;
    }

    public class RuntimeConnectionStoreShadow
    {
        private readonly Dictionary<long, RuntimeConnectionObjectShadow> _objects = new();

        public int Count => _objects.Count;
        public IEnumerable<long> ObjectIds => _objects.Keys;

        public bool TryGet(long objectId, out RuntimeConnectionObjectShadow value)
        {
            return _objects.TryGetValue(objectId, out value);
        }

        public void Set(long objectId, RuntimeConnectionObjectShadow value)
        {
            _objects[objectId] = value ?? throw new ArgumentNullException(nameof(value));
        }

        public bool Remove(long objectId)
        {
            return _objects.Remove(objectId);
        }

        public void Clear()
        {
            _objects.Clear();
        }

        public RuntimeConnectionStoreShadow Clone()
        {
            var result = new RuntimeConnectionStoreShadow();
            result.ReplaceWith(this);
            return result;
        }

        public void ReplaceWith(RuntimeConnectionStoreShadow source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (ReferenceEquals(this, source))
                return;

            _objects.Clear();
            foreach (var pair in source._objects)
            {
                var value = pair.Value
                            ?? throw new InvalidOperationException($"Connection shadow contains null object {pair.Key}.");
                _objects.Add(pair.Key, new RuntimeConnectionObjectShadow
                {
                    ParentObjectId = value.ParentObjectId,
                    SiblingIndex = value.SiblingIndex,
                    Depth = value.Depth,
                    StoreOrder = value.StoreOrder,
                    ReliableComponents = value.ReliableComponents == null
                        ? new Dictionary<uint, GameRuntimeComponent>()
                        : new Dictionary<uint, GameRuntimeComponent>(value.ReliableComponents),
                    SpawnVisibleComponents = value.SpawnVisibleComponents == null
                        ? new Dictionary<uint, GameRuntimeComponent>()
                        : new Dictionary<uint, GameRuntimeComponent>(value.SpawnVisibleComponents),
                });
            }
        }
    }

    public class RuntimeServerStoreProjection
    {
        private readonly RuntimeProtocolV2Context _context;

        public RuntimeServerStoreProjection(RuntimeProtocolV2Context context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public RuntimeStoreBaselinePayload BuildBaseline(
            RuntimeStore store,
            int connectionId,
            ulong baselineId,
            RuntimeConnectionStoreShadow shadow,
            out IReadOnlyList<NetObjectRef> membership)
        {
            if (shadow == null)
                throw new ArgumentNullException(nameof(shadow));

            var projectedTopology = RuntimeProjectedStoreSnapshotBuilder.Build(
                store,
                connectionId,
                _context.IsObjectVisible);
            var networkContext = CreateRestrictedNetworkContext(connectionId, store, projectedTopology);
            var storeReference = new NetStoreRef(store.Id, store.StoreGeneration);
            var projected = new RuntimeStoreBaselinePayload
            {
                Store = storeReference,
                BaselineId = baselineId,
                StoreRevision = store.StoreRevision,
            };
            var refs = new NetObjectRef[projectedTopology.Nodes.Count];
            shadow.Clear();
            for (var i = 0; i < projectedTopology.Nodes.Count; i++)
            {
                var node = projectedTopology.Nodes[i];
                if (!store.TryTakeRO(node.ObjectId, out var runtimeObject))
                    throw new InvalidOperationException($"Projected object {node.ObjectId} disappeared while building the baseline.");

                var origin = runtimeObject.Origin;
                if (!origin.InstanceGuid.isValid || origin.InstanceGuid != runtimeObject.GUID)
                    throw new InvalidOperationException($"Replicated runtime object {runtimeObject.InstanceId} has no stable GA instance origin.");

                projected.Spawns.Add(new RuntimeStoreBaselineSpawn
                {
                    ObjectId = runtimeObject.InstanceId,
                    InstanceGuid = origin.InstanceGuid,
                    ParentObjectId = node.ParentObjectId,
                    SiblingIndex = node.SiblingIndex,
                    AssetNetId = _context.AssetCatalog.GetRequiredNetId(origin.Asset),
                    Overrides = RuntimeStoreBaselineBuilder.BuildNetworkOverrides(
                        runtimeObject,
                        _context.TemplateCache,
                        _context.AssetLock,
                        networkContext,
                        _context.ReplicationPolicies),
                });
                refs[i] = new NetObjectRef(projected.Store, node.ObjectId);
                shadow.Set(node.ObjectId, CreateShadow(runtimeObject, node));
            }

            RuntimeStoreBaselineCodec.Validate(projected);
            membership = Array.AsReadOnly(refs);
            return projected;
        }

        public RuntimeStoreDeltaPayload BuildDelta(
            RuntimeStore store,
            RuntimeStoreRevisionRecord revision,
            int connectionId,
            RuntimeConnectionStoreReplicationState state,
            RuntimeConnectionStoreShadow shadow,
            ulong deliverySequence,
            out IReadOnlyList<NetObjectRef> enters,
            out IReadOnlyList<NetObjectRef> leaves)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (revision == null)
                throw new ArgumentNullException(nameof(revision));
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (shadow == null)
                throw new ArgumentNullException(nameof(shadow));

            var topology = RuntimeProjectedStoreSnapshotBuilder.Build(store, connectionId, _context.IsObjectVisible);
            var networkContext = CreateRestrictedNetworkContext(connectionId, store, topology);
            var patchEngine = new RuntimeObjectPatchEngine(_context.PatchCodecs, networkContext);
            var desiredIds = new HashSet<long>();
            for (var i = 0; i < topology.Nodes.Count; i++)
            {
                desiredIds.Add(topology.Nodes[i].ObjectId);
            }

            // Membership changes can invalidate RuntimeInstance fields on an
            // otherwise non-dirty surviving object (for example A still
            // points at B while B leaves interest). Validate every surviving
            // reliable component against the new projection before touching
            // the connection shadow. An escaping reference is a projection
            // configuration error and must never become a silent stale client
            // reference.
            if (!desiredIds.SetEquals(shadow.ObjectIds))
                ValidateReliableReferences(store, topology, patchEngine);

            var leavingNodes = new List<(long Id, RuntimeConnectionObjectShadow Shadow)>();
            foreach (var objectId in shadow.ObjectIds.ToArray())
            {
                if (!desiredIds.Contains(objectId) && shadow.TryGet(objectId, out var existing))
                    leavingNodes.Add((objectId, existing));
            }
            leavingNodes.Sort((left, right) =>
            {
                var depth = right.Shadow.Depth.CompareTo(left.Shadow.Depth);
                return depth != 0 ? depth : left.Shadow.StoreOrder.CompareTo(right.Shadow.StoreOrder);
            });

            var enterRefs = new List<NetObjectRef>();
            var leaveRefs = new List<NetObjectRef>();
            var operations = new List<RuntimeStoreDeltaOperation>();
            for (var i = 0; i < leavingNodes.Count; i++)
            {
                var objectId = leavingNodes[i].Id;
                operations.Add(new RuntimeStoreDeltaOperation
                {
                    Kind = RuntimeStoreDeltaOperationKind.Remove,
                    ObjectId = objectId,
                    RemoveSubtree = 0,
                });
                leaveRefs.Add(new NetObjectRef(state.Store, objectId));
                shadow.Remove(objectId);
            }

            var dirtyIds = CollectDirtyObjectIds(revision);
            for (var i = 0; i < topology.Nodes.Count; i++)
            {
                var node = topology.Nodes[i];
                if (!store.TryTakeRO(node.ObjectId, out var runtimeObject))
                    throw new InvalidOperationException($"Projected object {node.ObjectId} disappeared while building revision {revision.StoreRevision}.");

                if (!shadow.TryGet(node.ObjectId, out var existing))
                {
                    operations.Add(BuildSpawn(runtimeObject, node, networkContext));
                    enterRefs.Add(new NetObjectRef(state.Store, node.ObjectId));
                    shadow.Set(node.ObjectId, CreateShadow(runtimeObject, node));
                    continue;
                }

                if (existing.ParentObjectId != node.ParentObjectId)
                {
                    operations.Add(new RuntimeStoreDeltaOperation
                    {
                        Kind = RuntimeStoreDeltaOperationKind.Reparent,
                        ObjectId = node.ObjectId,
                        ParentObjectId = node.ParentObjectId,
                        SiblingIndex = node.SiblingIndex,
                    });
                }
                else if (existing.SiblingIndex != node.SiblingIndex)
                {
                    operations.Add(new RuntimeStoreDeltaOperation
                    {
                        Kind = node.ParentObjectId == RuntimeStoreStructureChange.NO_PARENT_ID
                            ? RuntimeStoreDeltaOperationKind.Reparent
                            : RuntimeStoreDeltaOperationKind.Move,
                        ObjectId = node.ObjectId,
                        ParentObjectId = node.ParentObjectId,
                        SiblingIndex = node.SiblingIndex,
                    });
                }

                if (dirtyIds.Contains(node.ObjectId))
                {
                    var currentReliable = SnapshotReliableComponents(runtimeObject);
                    var patch = patchEngine.BuildPatch(existing.ReliableComponents, currentReliable);
                    var currentSpawnVisible = SnapshotSpawnVisibleComponents(runtimeObject);
                    AppendUnreliableStructurePatches(
                        patchEngine,
                        existing.SpawnVisibleComponents,
                        currentSpawnVisible,
                        patch);
                    if (!patch.IsEmpty)
                    {
                        operations.Add(new RuntimeStoreDeltaOperation
                        {
                            Kind = RuntimeStoreDeltaOperationKind.Patch,
                            ObjectId = node.ObjectId,
                            Patch = patch,
                        });
                    }

                    existing.ReliableComponents = currentReliable;
                    existing.SpawnVisibleComponents = currentSpawnVisible;
                }

                existing.ParentObjectId = node.ParentObjectId;
                existing.SiblingIndex = node.SiblingIndex;
                existing.Depth = node.Depth;
                existing.StoreOrder = node.StoreOrder;
            }

            enters = Array.AsReadOnly(enterRefs.ToArray());
            leaves = Array.AsReadOnly(leaveRefs.ToArray());
            if (operations.Count == 0)
                return null;

            return new RuntimeStoreDeltaPayload
            {
                Kind = RuntimeStoreDeltaKind.Mutation,
                Store = state.Store,
                BaselineId = state.BaselineId,
                DeliverySequence = deliverySequence,
                FromRevision = state.ProjectedRevision,
                ToRevision = revision.StoreRevision,
                Operations = operations,
            };
        }

        public RuntimeStoreDeltaPayload BuildInterestDelta(
            RuntimeStore store,
            int connectionId,
            RuntimeConnectionStoreReplicationState state,
            RuntimeConnectionStoreShadow shadow,
            ulong deliverySequence,
            out IReadOnlyList<NetObjectRef> enters,
            out IReadOnlyList<NetObjectRef> leaves)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (shadow == null)
                throw new ArgumentNullException(nameof(shadow));

            var storeReference = new NetStoreRef(store.Id, store.StoreGeneration);
            if (state.Store != storeReference)
            {
                throw new InvalidOperationException(
                    $"Interest projection state '{state.Store}' does not match authoritative store '{storeReference}'.");
            }

            if (state.ProjectedRevision > state.StoreRevision)
            {
                throw new InvalidOperationException(
                    $"Interest projection cannot start at projected revision {state.ProjectedRevision} " +
                    $"after observed revision {state.StoreRevision}.");
            }

            var topology = RuntimeProjectedStoreSnapshotBuilder.Build(store, connectionId, _context.IsObjectVisible);
            var networkContext = CreateRestrictedNetworkContext(connectionId, store, topology);
            var patchEngine = new RuntimeObjectPatchEngine(_context.PatchCodecs, networkContext);
            var desiredIds = new HashSet<long>();
            for (var i = 0; i < topology.Nodes.Count; i++)
            {
                desiredIds.Add(topology.Nodes[i].ObjectId);
            }

            // Validate the complete proposed projection before mutating even
            // this staged shadow. A surviving reliable component may still
            // reference an object that is about to leave interest.
            ValidateReliableReferences(store, topology, patchEngine);

            var leavingNodes = new List<(long Id, RuntimeConnectionObjectShadow Shadow)>();
            foreach (var objectId in shadow.ObjectIds.ToArray())
            {
                if (!desiredIds.Contains(objectId) && shadow.TryGet(objectId, out var existing))
                    leavingNodes.Add((objectId, existing));
            }
            leavingNodes.Sort((left, right) =>
            {
                var depth = right.Shadow.Depth.CompareTo(left.Shadow.Depth);
                return depth != 0 ? depth : left.Shadow.StoreOrder.CompareTo(right.Shadow.StoreOrder);
            });

            var enterRefs = new List<NetObjectRef>();
            var leaveRefs = new List<NetObjectRef>();
            var operations = new List<RuntimeStoreDeltaOperation>();
            for (var i = 0; i < leavingNodes.Count; i++)
            {
                var objectId = leavingNodes[i].Id;
                operations.Add(new RuntimeStoreDeltaOperation
                {
                    Kind = RuntimeStoreDeltaOperationKind.Remove,
                    ObjectId = objectId,
                    RemoveSubtree = 0,
                });
                leaveRefs.Add(new NetObjectRef(state.Store, objectId));
                shadow.Remove(objectId);
            }

            // The topology is parent-first, so newly visible objects are
            // spawned in an immediately applicable order.
            for (var i = 0; i < topology.Nodes.Count; i++)
            {
                var node = topology.Nodes[i];
                if (!store.TryTakeRO(node.ObjectId, out var runtimeObject))
                {
                    throw new InvalidOperationException(
                        $"Projected object {node.ObjectId} disappeared while refreshing connection {connectionId} interest.");
                }

                if (!shadow.TryGet(node.ObjectId, out var existing))
                {
                    operations.Add(BuildSpawn(runtimeObject, node, networkContext));
                    enterRefs.Add(new NetObjectRef(state.Store, node.ObjectId));
                    shadow.Set(node.ObjectId, CreateShadow(runtimeObject, node));
                    continue;
                }

                if (existing.ParentObjectId != node.ParentObjectId)
                {
                    operations.Add(new RuntimeStoreDeltaOperation
                    {
                        Kind = RuntimeStoreDeltaOperationKind.Reparent,
                        ObjectId = node.ObjectId,
                        ParentObjectId = node.ParentObjectId,
                        SiblingIndex = node.SiblingIndex,
                    });
                }
                else if (existing.SiblingIndex != node.SiblingIndex)
                {
                    operations.Add(new RuntimeStoreDeltaOperation
                    {
                        Kind = node.ParentObjectId == RuntimeStoreStructureChange.NO_PARENT_ID
                            ? RuntimeStoreDeltaOperationKind.Reparent
                            : RuntimeStoreDeltaOperationKind.Move,
                        ObjectId = node.ObjectId,
                        ParentObjectId = node.ParentObjectId,
                        SiblingIndex = node.SiblingIndex,
                    });
                }

                existing.ParentObjectId = node.ParentObjectId;
                existing.SiblingIndex = node.SiblingIndex;
                existing.Depth = node.Depth;
                existing.StoreOrder = node.StoreOrder;
            }

            enters = Array.AsReadOnly(enterRefs.ToArray());
            leaves = Array.AsReadOnly(leaveRefs.ToArray());
            if (operations.Count == 0)
                return null;

            return new RuntimeStoreDeltaPayload
            {
                Kind = RuntimeStoreDeltaKind.Interest,
                Store = state.Store,
                BaselineId = state.BaselineId,
                DeliverySequence = deliverySequence,
                FromRevision = state.ProjectedRevision,
                ToRevision = state.StoreRevision,
                Operations = operations,
            };
        }

        private RuntimeStoreDeltaOperation BuildSpawn(
            GameRuntimeObject runtimeObject,
            RuntimeProjectedStoreNode node,
            RuntimePatchCodecContext networkContext)
        {
            var origin = runtimeObject.Origin;
            if (!origin.InstanceGuid.isValid || origin.InstanceGuid != runtimeObject.GUID)
                throw new InvalidOperationException($"Replicated runtime object {runtimeObject.InstanceId} has no stable GA instance origin.");

            return new RuntimeStoreDeltaOperation
            {
                Kind = RuntimeStoreDeltaOperationKind.Spawn,
                ObjectId = runtimeObject.InstanceId,
                InstanceGuid = origin.InstanceGuid,
                ParentObjectId = node.ParentObjectId,
                SiblingIndex = node.SiblingIndex,
                AssetNetId = _context.AssetCatalog.GetRequiredNetId(origin.Asset),
                Patch = RuntimeStoreBaselineBuilder.BuildNetworkOverrides(
                    runtimeObject,
                    _context.TemplateCache,
                    _context.AssetLock,
                    networkContext,
                    _context.ReplicationPolicies),
            };
        }

        private RuntimeNetworkPatchCodecContext CreateRestrictedNetworkContext(
            int connectionId,
            RuntimeStore projectedStore,
            RuntimeProjectedStoreSnapshot projectedTopology)
        {
            var allowed = new HashSet<NetObjectRef>();
            var stores = _context.GetManifestStores();
            for (var i = 0; i < stores.Count; i++)
            {
                var storeReference = stores[i];
                var store = _context.GetRequiredAuthoritativeStore(storeReference);
                var snapshot = ReferenceEquals(store, projectedStore)
                    ? projectedTopology
                    : RuntimeProjectedStoreSnapshotBuilder.Build(
                        store,
                        connectionId,
                        _context.IsObjectVisible);
                for (var nodeIndex = 0; nodeIndex < snapshot.Nodes.Count; nodeIndex++)
                {
                    allowed.Add(new NetObjectRef(storeReference, snapshot.Nodes[nodeIndex].ObjectId));
                }
            }

            return new RuntimeNetworkPatchCodecContext(
                value =>
                {
                    if (!RuntimeStores.TryGetRuntimeStore(value.StoreId, StoreRealm.Server, out var targetStore)
                        || !targetStore.IsRuntimeInstanceActive(value)
                        || !targetStore.TryTakeRO(value.Id, out _))
                    {
                        throw new InvalidOperationException(
                            $"Runtime reference '{value.StoreId}/{value.Id}' epoch {value.Epoch} is not active in the authoritative realm.");
                    }

                    var reference = NetObjectRef.FromRuntimeInstance(value, targetStore.StoreGeneration);
                    if (!allowed.Contains(reference))
                    {
                        throw new InvalidOperationException(
                            $"Runtime reference '{reference}' escapes connection {connectionId} projected membership. " +
                            "Hide the referring component/field or include the referenced object through ancestor-closed interest.");
                    }

                    return reference;
                },
                value => throw new NotSupportedException(
                    $"Authoritative network patch context cannot decode replica reference '{value}'."));
        }

        private RuntimeConnectionObjectShadow CreateShadow(
            GameRuntimeObject runtimeObject,
            RuntimeProjectedStoreNode node)
        {
            return new RuntimeConnectionObjectShadow
            {
                ParentObjectId = node.ParentObjectId,
                SiblingIndex = node.SiblingIndex,
                Depth = node.Depth,
                StoreOrder = node.StoreOrder,
                ReliableComponents = SnapshotReliableComponents(runtimeObject),
                SpawnVisibleComponents = SnapshotSpawnVisibleComponents(runtimeObject),
            };
        }

        private Dictionary<uint, GameRuntimeComponent> SnapshotReliableComponents(GameRuntimeObject runtimeObject)
        {
            var result = new Dictionary<uint, GameRuntimeComponent>();
            var components = runtimeObject.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i]
                                ?? throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} contains a null component.");
                if (!RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var componentTypeId))
                    throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} contains an unregistered component '{component.GetType().FullName}'.");
                if (!RuntimeComponentVisibilityProjection.IsVisible(
                        _context.ReplicationPolicies,
                        componentTypeId,
                        RuntimeComponentProjectionChannel.ReliableOverride))
                {
                    continue;
                }

                result.Add(componentTypeId, _context.PatchCodecs.Get(componentTypeId).Clone(component));
            }

            return result;
        }

        private Dictionary<uint, GameRuntimeComponent> SnapshotSpawnVisibleComponents(
            GameRuntimeObject runtimeObject)
        {
            var result = new Dictionary<uint, GameRuntimeComponent>();
            var components = runtimeObject.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i]
                                ?? throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} contains a null component.");
                if (!RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var componentTypeId))
                    throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} contains an unregistered component '{component.GetType().FullName}'.");
                if (!RuntimeComponentVisibilityProjection.IsVisible(
                        _context.ReplicationPolicies,
                        componentTypeId,
                        RuntimeComponentProjectionChannel.Spawn))
                {
                    continue;
                }

                result.Add(componentTypeId, _context.PatchCodecs.Get(componentTypeId).Clone(component));
            }

            return result;
        }

        private void AppendUnreliableStructurePatches(
            RuntimeObjectPatchEngine patchEngine,
            IReadOnlyDictionary<uint, GameRuntimeComponent> previous,
            IReadOnlyDictionary<uint, GameRuntimeComponent> current,
            RuntimeObjectPatch target)
        {
            if (patchEngine == null)
                throw new ArgumentNullException(nameof(patchEngine));
            if (target == null)
                throw new ArgumentNullException(nameof(target));

            var previousChanged = new Dictionary<uint, GameRuntimeComponent>();
            var currentChanged = new Dictionary<uint, GameRuntimeComponent>();
            var componentTypeIds = new HashSet<uint>();
            if (previous != null)
            {
                foreach (var pair in previous)
                    componentTypeIds.Add(pair.Key);
            }
            if (current != null)
            {
                foreach (var pair in current)
                    componentTypeIds.Add(pair.Key);
            }

            foreach (var componentTypeId in componentTypeIds)
            {
                if (_context.ReplicationPolicies.GetRequired(componentTypeId)
                    != RuntimeReplicationPolicy.UnreliableState)
                {
                    continue;
                }

                GameRuntimeComponent previousComponent = null;
                GameRuntimeComponent currentComponent = null;
                var hadPrevious = previous != null
                                  && previous.TryGetValue(componentTypeId, out previousComponent);
                var hasCurrent = current != null
                                 && current.TryGetValue(componentTypeId, out currentComponent);
                if (hadPrevious == hasCurrent)
                    continue;
                if (hadPrevious)
                    previousChanged.Add(componentTypeId, previousComponent);
                if (hasCurrent)
                    currentChanged.Add(componentTypeId, currentComponent);
            }

            if (previousChanged.Count == 0 && currentChanged.Count == 0)
                return;

            var structurePatch = patchEngine.BuildPatch(previousChanged, currentChanged);
            for (var i = 0; i < structurePatch.Components.Count; i++)
            {
                var componentPatch = structurePatch.Components[i];
                if (componentPatch.Kind != ComponentPatchKind.Add
                    && componentPatch.Kind != ComponentPatchKind.Remove)
                {
                    throw new InvalidOperationException(
                        $"Unreliable component {componentPatch.ComponentTypeId} produced non-structural reliable patch {componentPatch.Kind}.");
                }
                target.Components.Add(componentPatch);
            }
            target.Components.Sort((left, right) => left.ComponentTypeId.CompareTo(right.ComponentTypeId));
        }

        private void ValidateReliableReferences(
            RuntimeStore store,
            RuntimeProjectedStoreSnapshot topology,
            RuntimeObjectPatchEngine patchEngine)
        {
            var empty = new Dictionary<uint, GameRuntimeComponent>();
            for (var i = 0; i < topology.Nodes.Count; i++)
            {
                var objectId = topology.Nodes[i].ObjectId;
                if (!store.TryTakeRO(objectId, out var runtimeObject))
                    throw new InvalidOperationException($"Projected object {objectId} disappeared during reference validation.");

                // Building Add patches serializes each complete reliable
                // component through the restricted network codec context.
                // The patch itself is intentionally discarded.
                patchEngine.BuildPatch(empty, SnapshotReliableComponents(runtimeObject));
            }
        }

        private static HashSet<long> CollectDirtyObjectIds(RuntimeStoreRevisionRecord revision)
        {
            var result = new HashSet<long>();
            for (var i = 0; i < revision.ComponentStructureChanges.Count; i++)
            {
                result.Add(revision.ComponentStructureChanges[i].Id);
            }

            for (var i = 0; i < revision.ComponentChanges.Count; i++)
            {
                result.Add(revision.ComponentChanges[i].Id);
            }

            return result;
        }
    }
}
