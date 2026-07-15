using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Validates spawns and patched semantic values on detached instances, then
    /// commits them to the active RuntimeStore in one replication-suppressed
    /// dirty batch. Untouched component references (including hot state) may be
    /// shallow-preserved in the validation plan but are never committed. A
    /// commit exception retires and hides the failed generation before the
    /// receiver requests one rebaseline.
    /// </summary>
    public class RuntimeReplicaDeltaTransaction
    {
        private readonly RuntimeProtocolV2Context _context;
        private readonly RuntimeStoreDeltaCodec _deltaCodec;
        private readonly RuntimeReplicaBaselineSpawnFactory _spawnFactory;
        private readonly RuntimeReplicaStagingRealms _stagingRealms;
        private readonly RuntimeInboundNetworkPatchPolicyValidator _inboundPatchValidator;
        private readonly Func<uint, RuntimeComponentPatchProjectionMode> _selectPatchMode;

        public Exception LastFailure { get; private set; }

        public RuntimeReplicaDeltaTransaction(
            RuntimeProtocolV2Context context,
            RuntimeReplicaBaselineStager stager,
            RuntimeReplicaBaselineSpawnFactory spawnFactory,
            RuntimeReplicaStagingRealms stagingRealms)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _spawnFactory = spawnFactory ?? throw new ArgumentNullException(nameof(spawnFactory));
            _stagingRealms = stagingRealms ?? throw new ArgumentNullException(nameof(stagingRealms));
            _deltaCodec = new RuntimeStoreDeltaCodec(context.PatchCodecs);
            _inboundPatchValidator = new RuntimeInboundNetworkPatchPolicyValidator(context.ReplicationPolicies);
            _selectPatchMode = SelectPatchMode;
        }

        public bool TryApply(in RuntimeClientDeltaEnvelope envelope)
        {
            LastFailure = null;
            RuntimeStore active = null;
            var commitStarted = false;
            try
            {
                var delta = _deltaCodec.Decode(envelope.Payload);
                ValidateEnvelope(delta, envelope);
                if (!RuntimeStores.TryGetRuntimeStore(
                        delta.Store.StoreId,
                        delta.Store.StoreGeneration,
                        StoreRealm.Client,
                        out active)
                    || active.StoreRevision != delta.FromRevision)
                {
                    return false;
                }

                var plan = ValidateAndBuildPlan(active, delta, usePreparedRealm: false);
                Commit(active, delta, plan, ref commitStarted);
                return true;
            }
            catch (Exception exception)
            {
                LastFailure = exception;
                // Decode and detached-plan validation failures must not hide a
                // healthy active generation. Only a failure after mutation of
                // the active store begins makes that generation unsafe to
                // expose and requires retirement before rebaseline.
                if (active != null && commitStarted)
                {
                    active.AbortNetApply();
                    RuntimeStores.RemoveRuntimeStore(active.Id, active.StoreGeneration, StoreRealm.Client);
                }
                return false;
            }
        }

        public bool TryApplyPrepared(RuntimeStore preparedStore, in RuntimeClientDeltaEnvelope envelope)
        {
            LastFailure = null;
            var commitStarted = false;
            try
            {
                if (preparedStore == null)
                    throw new ArgumentNullException(nameof(preparedStore));
                if (!_stagingRealms.ContainsPreparedStore(preparedStore))
                    throw new InvalidOperationException($"RuntimeStore '{preparedStore.Id}' is not an active prepared replica realm.");

                var delta = _deltaCodec.Decode(envelope.Payload);
                ValidateEnvelope(delta, envelope);
                var preparedReference = new NetStoreRef(preparedStore.Id, preparedStore.StoreGeneration);
                if (delta.Store != preparedReference || preparedStore.StoreRevision != delta.FromRevision)
                {
                    throw new InvalidOperationException(
                        $"Prepared replica '{preparedReference}' at revision {preparedStore.StoreRevision} cannot apply delta " +
                        $"for '{delta.Store}' from revision {delta.FromRevision}.");
                }

                var plan = ValidateAndBuildPlan(preparedStore, delta, usePreparedRealm: true);
                Commit(preparedStore, delta, plan, ref commitStarted);
                return true;
            }
            catch (Exception exception)
            {
                LastFailure = exception;
                if (commitStarted)
                    preparedStore?.AbortNetApply();
                return false;
            }
        }

        private ValidatedDeltaPlan ValidateAndBuildPlan(
            RuntimeStore store,
            RuntimeStoreDeltaPayload delta,
            bool usePreparedRealm)
        {
            var spawnedIds = new HashSet<long>();
            for (var i = 0; i < delta.Operations.Count; i++)
            {
                if (delta.Operations[i].Kind == RuntimeStoreDeltaOperationKind.Spawn)
                    spawnedIds.Add(delta.Operations[i].ObjectId);
            }

            var patchContext = usePreparedRealm
                ? CreatePreparedValidationPatchContext(store, spawnedIds)
                : CreateValidationPatchContext(store, spawnedIds);
            var patchEngine = new RuntimeObjectPatchEngine(_context.PatchCodecs, patchContext);
            var topology = MutableTopology.Create(store);
            var plan = new ValidatedDeltaPlan();
            var plannedGuids = new HashSet<UnityEngine.Hash128>();
            for (var i = 0; i < delta.Operations.Count; i++)
            {
                var operation = delta.Operations[i];
                switch (operation.Kind)
                {
                    case RuntimeStoreDeltaOperationKind.Spawn:
                        if (topology.Contains(operation.ObjectId))
                            throw new InvalidOperationException($"Replica delta spawns existing object {operation.ObjectId}.");
                        if (operation.ParentObjectId != RuntimeStoreStructureChange.NO_PARENT_ID
                            && !topology.Contains(operation.ParentObjectId))
                        {
                            throw new InvalidOperationException($"Replica delta spawn {operation.ObjectId} references missing parent {operation.ParentObjectId}.");
                        }
                        var resolved = _spawnFactory.ResolveRequiredAsset(operation.AssetNetId);
                        _spawnFactory.ValidateInboundSpawn(
                            operation.ObjectId,
                            operation.Patch,
                            resolved);
                        var instance = new GameAssetInstance(
                            operation.InstanceGuid,
                            new GameAssetReference(resolved.ExactKey),
                            operation.Patch);
                        var runtimeObject = _context.TemplateCache.MaterializeProjected(
                            instance,
                            _context.AssetLock,
                            patchContext,
                            _selectPatchMode);
                        runtimeObject.InstanceId = operation.ObjectId;
                        if (runtimeObject.Origin.Asset.AssetGuid != resolved.AssetGuid
                            || !string.Equals(runtimeObject.Origin.Asset.MaterializedContentHash, resolved.MaterializedContentHash, StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException($"Replica delta spawn {operation.ObjectId} materialized the wrong GameAsset baseline.");
                        }
                        if (store.TryGetId(runtimeObject.GUID, out _)
                            || !plannedGuids.Add(runtimeObject.GUID))
                        {
                            throw new InvalidOperationException($"Replica delta spawn {operation.ObjectId} has duplicate instance GUID {runtimeObject.GUID}.");
                        }
                        topology.Spawn(operation.ObjectId, operation.ParentObjectId, operation.SiblingIndex);
                        plan.Spawns.Add(operation.ObjectId, runtimeObject);
                        break;

                    case RuntimeStoreDeltaOperationKind.Remove:
                        topology.Remove(operation.ObjectId, operation.RemoveSubtree != 0);
                        break;

                    case RuntimeStoreDeltaOperationKind.Reparent:
                        topology.Reparent(operation.ObjectId, operation.ParentObjectId, operation.SiblingIndex);
                        break;

                    case RuntimeStoreDeltaOperationKind.Move:
                        topology.Move(operation.ObjectId, operation.ParentObjectId, operation.SiblingIndex);
                        break;

                    case RuntimeStoreDeltaOperationKind.Patch:
                        if (!store.TryTakeRO(operation.ObjectId, out var existing))
                            throw new InvalidOperationException($"Replica delta patches missing object {operation.ObjectId}.");
                        _inboundPatchValidator.ValidateDelta(operation.Patch);
                        var current = SnapshotComponents(existing);
                        plan.ComponentTargets.Add(
                            operation.ObjectId,
                            new ValidatedComponentTarget(
                                patchEngine.ApplyProjectedPatch(
                                    current,
                                    operation.Patch,
                                    _selectPatchMode),
                                CollectPatchedComponentIds(operation.Patch)));
                        break;

                    default:
                        throw new InvalidOperationException($"Replica delta contains unsupported operation {operation.Kind}.");
                }
            }
            return plan;
        }

        private RuntimeComponentPatchProjectionMode SelectPatchMode(uint componentTypeId)
        {
            return RuntimeComponentVisibilityProjection.GetPatchProjectionMode(
                _context.ReplicationPolicies,
                componentTypeId);
        }

        private void Commit(
            RuntimeStore store,
            RuntimeStoreDeltaPayload delta,
            ValidatedDeltaPlan plan,
            ref bool commitStarted)
        {
            if (delta.Kind == RuntimeStoreDeltaKind.Interest)
                store.BeginNetProjectionApply(delta.ToRevision);
            else
                store.BeginNetApply(delta.ToRevision);
            commitStarted = true;
            var spawned = new List<long>();
            for (var i = 0; i < delta.Operations.Count; i++)
            {
                var operation = delta.Operations[i];
                switch (operation.Kind)
                {
                    case RuntimeStoreDeltaOperationKind.Spawn:
                        var runtimeObject = plan.Spawns[operation.ObjectId];
                        if (!store.TryAddExternalObject(runtimeObject))
                            throw new InvalidOperationException($"Replica delta cannot register validated spawn {operation.ObjectId}.");
                        if (operation.ParentObjectId == RuntimeStoreStructureChange.NO_PARENT_ID)
                            store.PublishRootExisting(operation.ObjectId);
                        else if (!store.AttachChild(operation.ParentObjectId, operation.ObjectId, operation.SiblingIndex))
                            throw new InvalidOperationException($"Replica delta cannot attach validated spawn {operation.ObjectId}.");
                        spawned.Add(operation.ObjectId);
                        break;

                    case RuntimeStoreDeltaOperationKind.Remove:
                        var removeMode = operation.RemoveSubtree != 0
                            ? RemoveMode.Subtree
                            : RemoveMode.NodeOnly_DetachChildrenToRoot;
                        if (!store.Remove(operation.ObjectId, removeMode, out _))
                            throw new InvalidOperationException($"Replica delta cannot remove validated object {operation.ObjectId}.");
                        break;

                    case RuntimeStoreDeltaOperationKind.Reparent:
                        CommitReparent(store, operation);
                        break;

                    case RuntimeStoreDeltaOperationKind.Move:
                        if (!store.MoveChild(operation.ParentObjectId, operation.ObjectId, operation.SiblingIndex))
                            throw new InvalidOperationException($"Replica delta cannot move validated object {operation.ObjectId}.");
                        break;

                    case RuntimeStoreDeltaOperationKind.Patch:
                        CommitComponentTarget(store, operation.ObjectId, plan.ComponentTargets[operation.ObjectId]);
                        break;
                }
            }

            for (var i = 0; i < spawned.Count; i++)
            {
                if (store.TryTakeRO(spawned[i], out var runtimeObject))
                    runtimeObject.CreateEntity();
            }
            store.CommitNetApply();
        }

        private RuntimeNetworkPatchCodecContext CreateValidationPatchContext(
            RuntimeStore store,
            ISet<long> spawnedIds)
        {
            return new RuntimeNetworkPatchCodecContext(
                value =>
                {
                    if (!RuntimeStores.TryGetRuntimeStore(value.StoreId, StoreRealm.Client, out var target)
                        || !target.IsRuntimeInstanceActive(value)
                        || !target.TryTakeRO(value.Id, out _))
                    {
                        throw new InvalidOperationException($"Replica RuntimeInstance '{value.StoreId}/{value.Id}' is not active.");
                    }
                    return NetObjectRef.FromRuntimeInstance(value, target.StoreGeneration);
                },
                value =>
                {
                    if (value.Store == new NetStoreRef(store.Id, store.StoreGeneration)
                        && spawnedIds.Contains(value.ObjectId))
                    {
                        return value.ToRuntimeInstance(store.Epoch);
                    }
                    if (!RuntimeStores.TryGetRuntimeStore(
                            value.Store.StoreId,
                            value.Store.StoreGeneration,
                            StoreRealm.Client,
                            out var target)
                        || !target.TryTakeRO(value.ObjectId, out _))
                    {
                        throw new InvalidOperationException($"Replica network reference '{value}' is not active or entering in this transaction.");
                    }
                    return value.ToRuntimeInstance(target.Epoch);
                });
        }

        private RuntimeNetworkPatchCodecContext CreatePreparedValidationPatchContext(
            RuntimeStore store,
            ISet<long> spawnedIds)
        {
            var currentStore = new NetStoreRef(store.Id, store.StoreGeneration);
            return new RuntimeNetworkPatchCodecContext(
                value =>
                {
                    if (_stagingRealms.TryGetPreparedStore(value, out var reference, out var prepared))
                    {
                        if ((ReferenceEquals(prepared, store) && spawnedIds.Contains(value.Id))
                            || prepared.TryTakeRO(value.Id, out _))
                        {
                            return new NetObjectRef(reference, value.Id);
                        }

                        throw new InvalidOperationException(
                            $"Prepared replica RuntimeInstance '{value.StoreId}/{value.Id}' is not present in staging realm '{reference}'.");
                    }

                    if (!RuntimeStores.TryGetRuntimeStore(value.StoreId, StoreRealm.Client, out var active)
                        || !active.IsRuntimeInstanceActive(value)
                        || !active.TryTakeRO(value.Id, out _))
                    {
                        throw new InvalidOperationException(
                            $"Replica RuntimeInstance '{value.StoreId}/{value.Id}' is neither prepared nor active.");
                    }

                    return NetObjectRef.FromRuntimeInstance(value, active.StoreGeneration);
                },
                value =>
                {
                    if (value.Store == currentStore && spawnedIds.Contains(value.ObjectId))
                        return value.ToRuntimeInstance(store.Epoch);
                    if (_stagingRealms.TryGetPreparedStore(value.Store, out var prepared))
                    {
                        if (!prepared.TryTakeRO(value.ObjectId, out _))
                        {
                            throw new InvalidOperationException(
                                $"Prepared network reference '{value}' is not present in its staging realm.");
                        }

                        return value.ToRuntimeInstance(prepared.Epoch);
                    }
                    if (!RuntimeStores.TryGetRuntimeStore(
                            value.Store.StoreId,
                            value.Store.StoreGeneration,
                            StoreRealm.Client,
                            out var active)
                        || !active.TryTakeRO(value.ObjectId, out _))
                    {
                        throw new InvalidOperationException(
                            $"Replica network reference '{value}' is neither prepared nor active.");
                    }

                    return value.ToRuntimeInstance(active.Epoch);
                });
        }

        private static Dictionary<uint, GameRuntimeComponent> SnapshotComponents(GameRuntimeObject runtimeObject)
        {
            var result = new Dictionary<uint, GameRuntimeComponent>();
            var components = runtimeObject.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i]
                                ?? throw new InvalidOperationException($"Replica object {runtimeObject.InstanceId} contains a null component.");
                if (!RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var componentTypeId))
                    throw new InvalidOperationException($"Replica object {runtimeObject.InstanceId} contains an unregistered component.");
                result.Add(componentTypeId, component);
            }
            return result;
        }

        private static void CommitComponentTarget(
            RuntimeStore store,
            long objectId,
            ValidatedComponentTarget validated)
        {
            if (!store.TryTakeRW(objectId, out var runtimeObject))
                throw new InvalidOperationException($"Replica delta cannot commit patch for missing object {objectId}.");
            for (var i = 0; i < validated.ComponentTypeIds.Count; i++)
            {
                var componentTypeId = validated.ComponentTypeIds[i];
                if (!validated.Target.TryGetValue(componentTypeId, out var component))
                    runtimeObject.RemoveByTypeId(componentTypeId);
                else
                    runtimeObject.AddOrReplaceById(componentTypeId, component);
            }
        }

        private static IReadOnlyList<uint> CollectPatchedComponentIds(RuntimeObjectPatch patch)
        {
            var result = new uint[patch.Components.Count];
            for (var i = 0; i < patch.Components.Count; i++)
            {
                result[i] = patch.Components[i].ComponentTypeId;
            }
            Array.Sort(result);
            return Array.AsReadOnly(result);
        }

        private static void CommitReparent(RuntimeStore store, RuntimeStoreDeltaOperation operation)
        {
            if (operation.ParentObjectId == RuntimeStoreStructureChange.NO_PARENT_ID)
            {
                if (store.TryTakeParentRO(operation.ObjectId, out _)
                    && !store.DetachChild(operation.ObjectId))
                {
                    throw new InvalidOperationException($"Replica delta cannot detach validated object {operation.ObjectId}.");
                }
                return;
            }
            if (!store.AttachChild(operation.ParentObjectId, operation.ObjectId, operation.SiblingIndex))
                throw new InvalidOperationException($"Replica delta cannot reparent validated object {operation.ObjectId}.");
        }

        private static void ValidateEnvelope(RuntimeStoreDeltaPayload delta, in RuntimeClientDeltaEnvelope envelope)
        {
            if (delta.Kind != envelope.Kind
                || delta.Store != envelope.Store
                || delta.BaselineId != envelope.BaselineId
                || delta.DeliverySequence != envelope.DeliverySequence
                || delta.FromRevision != envelope.FromRevision
                || delta.ToRevision != envelope.ToRevision)
            {
                throw new InvalidOperationException("Reliable delta wire header does not match its canonical payload.");
            }
        }

        private class ValidatedDeltaPlan
        {
            public readonly Dictionary<long, GameRuntimeObject> Spawns = new();
            public readonly Dictionary<long, ValidatedComponentTarget> ComponentTargets = new();
        }

        private class ValidatedComponentTarget
        {
            public readonly IReadOnlyDictionary<uint, GameRuntimeComponent> Target;
            public readonly IReadOnlyList<uint> ComponentTypeIds;

            public ValidatedComponentTarget(
                IReadOnlyDictionary<uint, GameRuntimeComponent> target,
                IReadOnlyList<uint> componentTypeIds)
            {
                Target = target;
                ComponentTypeIds = componentTypeIds;
            }
        }

        private class MutableTopology
        {
            private readonly Dictionary<long, long> _parentById = new();
            private readonly Dictionary<long, List<long>> _childrenByParent = new();

            public static MutableTopology Create(RuntimeStore store)
            {
                var result = new MutableTopology();
                foreach (var pair in store.All.V)
                {
                    if (pair.Key != RuntimeStore.STORE_ROOT_OBJECT_ID)
                        result._parentById.Add(pair.Key, RuntimeStoreStructureChange.NO_PARENT_ID);
                }
                foreach (var pair in store.All.V)
                {
                    if (!store.TryTakeChildren(pair.Key, out var children) || children.Count == 0)
                        continue;
                    if (pair.Key == RuntimeStore.STORE_ROOT_OBJECT_ID)
                    {
                        for (var i = 0; i < children.Count; i++)
                        {
                            result._parentById[children[i]] = RuntimeStoreStructureChange.NO_PARENT_ID;
                        }
                        continue;
                    }
                    var copy = new List<long>(children);
                    result._childrenByParent[pair.Key] = copy;
                    for (var i = 0; i < copy.Count; i++)
                    {
                        if (copy[i] != RuntimeStore.STORE_ROOT_OBJECT_ID)
                            result._parentById[copy[i]] = pair.Key;
                    }
                }
                return result;
            }

            public bool Contains(long objectId) => _parentById.ContainsKey(objectId);

            public void Spawn(long objectId, long parentId, int index)
            {
                _parentById.Add(objectId, RuntimeStoreStructureChange.NO_PARENT_ID);
                if (parentId != RuntimeStoreStructureChange.NO_PARENT_ID)
                    Reparent(objectId, parentId, index);
            }

            public void Remove(long objectId, bool subtree)
            {
                Require(objectId);
                if (_childrenByParent.TryGetValue(objectId, out var children) && children.Count > 0)
                {
                    var copy = new List<long>(children);
                    if (subtree)
                    {
                        for (var i = 0; i < copy.Count; i++)
                            Remove(copy[i], true);
                    }
                    else
                    {
                        for (var i = 0; i < copy.Count; i++)
                            _parentById[copy[i]] = RuntimeStoreStructureChange.NO_PARENT_ID;
                    }
                }
                Detach(objectId);
                _childrenByParent.Remove(objectId);
                _parentById.Remove(objectId);
            }

            public void Reparent(long objectId, long parentId, int index)
            {
                Require(objectId);
                if (parentId == RuntimeStoreStructureChange.NO_PARENT_ID)
                {
                    Detach(objectId);
                    _parentById[objectId] = parentId;
                    return;
                }
                Require(parentId);
                for (var cursor = parentId; cursor != RuntimeStoreStructureChange.NO_PARENT_ID; cursor = _parentById[cursor])
                {
                    if (cursor == objectId)
                        throw new InvalidOperationException($"Replica delta reparent creates a cycle at {objectId}.");
                }
                Detach(objectId);
                if (!_childrenByParent.TryGetValue(parentId, out var children))
                {
                    children = new List<long>();
                    _childrenByParent[parentId] = children;
                }
                if (index < 0 || index > children.Count)
                    throw new InvalidOperationException($"Replica delta sibling index {index} is invalid for parent {parentId}.");
                children.Insert(index, objectId);
                _parentById[objectId] = parentId;
            }

            public void Move(long objectId, long parentId, int index)
            {
                Require(objectId);
                if (!_parentById.TryGetValue(objectId, out var currentParent) || currentParent != parentId)
                    throw new InvalidOperationException($"Replica delta move parent mismatch for object {objectId}.");
                if (!_childrenByParent.TryGetValue(parentId, out var children))
                    throw new InvalidOperationException($"Replica delta move parent {parentId} has no children.");
                var oldIndex = children.IndexOf(objectId);
                if (oldIndex < 0 || index < 0 || index >= children.Count)
                    throw new InvalidOperationException($"Replica delta move index is invalid for object {objectId}.");
                children.RemoveAt(oldIndex);
                children.Insert(index, objectId);
            }

            private void Detach(long objectId)
            {
                if (!_parentById.TryGetValue(objectId, out var parentId)
                    || parentId == RuntimeStoreStructureChange.NO_PARENT_ID)
                    return;
                if (_childrenByParent.TryGetValue(parentId, out var siblings))
                    siblings.Remove(objectId);
                _parentById[objectId] = RuntimeStoreStructureChange.NO_PARENT_ID;
            }

            private void Require(long objectId)
            {
                if (!Contains(objectId))
                    throw new InvalidOperationException($"Replica delta references missing object {objectId}.");
            }
        }
    }
}
