using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Mirror.Protocol
{
    public static class RuntimeStoreBaselineBuilder
    {
        public static RuntimeStoreBaselinePayload Build(
            RuntimeStore store,
            RuntimeSessionAssetCatalog assetCatalog,
            ulong baselineId,
            GameAssetTemplateCache templateCache,
            GameAssetLibraryLock assetLock,
            RuntimePatchCodecContext networkPatchContext,
            RuntimeReplicationPolicyRegistry replicationPolicies)
        {
            if (templateCache == null)
                throw new ArgumentNullException(nameof(templateCache));
            if (assetLock == null)
                throw new ArgumentNullException(nameof(assetLock));
            if (networkPatchContext == null)
                throw new ArgumentNullException(nameof(networkPatchContext));
            if (replicationPolicies == null || !replicationPolicies.IsSealed)
                throw new InvalidOperationException("A sealed replication policy registry is required to build a network baseline.");
            return Build(
                store,
                assetCatalog,
                baselineId,
                runtimeObject => BuildNetworkOverrides(
                    runtimeObject,
                    templateCache,
                    assetLock,
                    networkPatchContext,
                    replicationPolicies),
                root => root == null ? new RuntimeObjectPatch(templateCache.CodecRegistry.SchemaHash) : BuildNetworkRootPatch(root, templateCache.CodecRegistry, networkPatchContext, replicationPolicies));
        }

        public static RuntimeObjectPatch BuildNetworkRootPatch(GameRuntimeObject root, RuntimePatchCodecRegistry patchCodecs, RuntimePatchCodecContext networkPatchContext, RuntimeReplicationPolicyRegistry replicationPolicies)
        {
            if (root == null || root.InstanceId != RuntimeStore.STORE_ROOT_OBJECT_ID)
                throw new ArgumentException("Network root patch requires the store root object.", nameof(root));
            if (patchCodecs == null || networkPatchContext == null)
                throw new InvalidOperationException("Network root patch requires runtime patch codecs and context.");
            if (replicationPolicies == null || !replicationPolicies.IsSealed)
                throw new InvalidOperationException("A sealed replication policy registry is required to project the store root.");

            var components = new Dictionary<uint, GameRuntimeComponent>();
            foreach (var component in root.Components)
            {
                if (component == null || !RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var typeId) || !components.TryAdd(typeId, component))
                    throw new InvalidOperationException($"Store root {root.StoreId} contains a null, unregistered, or duplicate runtime component.");
                if (replicationPolicies.GetRequired(typeId) == RuntimeReplicationPolicy.UnreliableState)
                    throw new InvalidOperationException($"Store root {root.StoreId} cannot carry unreliable component {typeId}; root has no state-stream identity.");
            }

            var patchEngine = new RuntimeObjectPatchEngine(patchCodecs, networkPatchContext);
            return patchEngine.BuildProjectedPatch(Array.Empty<uint>(), components, _ => null, typeId => RuntimeComponentVisibilityProjection.GetPatchProjectionMode(replicationPolicies, typeId));
        }

        public static RuntimeObjectPatch BuildNetworkOverrides(
            GameRuntimeObject runtimeObject,
            GameAssetTemplateCache templateCache,
            GameAssetLibraryLock assetLock,
            RuntimePatchCodecContext networkPatchContext,
            RuntimeReplicationPolicyRegistry replicationPolicies)
        {
            if (runtimeObject == null)
                throw new ArgumentNullException(nameof(runtimeObject));
            if (templateCache == null)
                throw new ArgumentNullException(nameof(templateCache));
            if (assetLock == null)
                throw new ArgumentNullException(nameof(assetLock));
            if (networkPatchContext == null)
                throw new ArgumentNullException(nameof(networkPatchContext));
            if (replicationPolicies == null || !replicationPolicies.IsSealed)
                throw new InvalidOperationException("A sealed replication policy registry is required to project network overrides.");

            return RuntimeSpawnPatchProjector.Project(
                runtimeObject,
                templateCache,
                assetLock,
                networkPatchContext,
                replicationPolicies);
        }

        private static RuntimeStoreBaselinePayload Build(
            RuntimeStore store,
            RuntimeSessionAssetCatalog assetCatalog,
            ulong baselineId,
            Func<GameRuntimeObject, RuntimeObjectPatch> buildOverrides,
            Func<GameRuntimeObject, RuntimeObjectPatch> buildRootPatch)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            if (store.Retired)
                throw new InvalidOperationException($"Cannot build baseline from retired store '{store.Id}'.");
            if (store.StoreGeneration == 0)
                throw new InvalidOperationException($"RuntimeStore '{store.Id}' has no registered generation.");
            if (assetCatalog == null)
                throw new ArgumentNullException(nameof(assetCatalog));
            if (baselineId == 0)
                throw new ArgumentOutOfRangeException(nameof(baselineId), "Baseline id must be non-zero.");
            if (buildOverrides == null)
                throw new ArgumentNullException(nameof(buildOverrides));
            if (buildRootPatch == null)
                throw new ArgumentNullException(nameof(buildRootPatch));

            store.TryTakeRO(RuntimeStore.STORE_ROOT_OBJECT_ID, out var root);
            var result = new RuntimeStoreBaselinePayload
            {
                Store = new NetStoreRef(store.Id, store.StoreGeneration),
                BaselineId = baselineId,
                StoreRevision = store.StoreRevision,
                RootPatch = buildRootPatch(root),
            };

            var roots = new List<long>(store.Parents.V.Keys);
            roots.Sort();
            var parentlessSiblingIndex = 0;
            for (var i = 0; i < roots.Count; i++)
            {
                var rootId = roots[i];
                if (rootId != RuntimeStore.STORE_ROOT_OBJECT_ID)
                {
                    AppendSubtree(store, assetCatalog, buildOverrides, result.Spawns, rootId, RuntimeStoreStructureChange.NO_PARENT_ID, parentlessSiblingIndex++);
                    continue;
                }

                if (!store.TryTakeChildren(rootId, out var children) || children == null)
                    continue;
                for (var childIndex = 0; childIndex < children.Count; childIndex++)
                    AppendSubtree(store, assetCatalog, buildOverrides, result.Spawns, children[childIndex], RuntimeStore.STORE_ROOT_OBJECT_ID, childIndex);
            }

            RuntimeStoreBaselineCodec.Validate(result);
            return result;
        }

        private static void AppendSubtree(
            RuntimeStore store,
            RuntimeSessionAssetCatalog assetCatalog,
            Func<GameRuntimeObject, RuntimeObjectPatch> buildOverrides,
            List<RuntimeStoreBaselineSpawn> output,
            long objectId,
            long parentObjectId,
            int siblingIndex)
        {
            if (objectId == RuntimeStore.STORE_ROOT_OBJECT_ID)
                throw new InvalidOperationException("Store root must never be emitted in a network baseline.");
            if (!store.TryTakeRO(objectId, out var runtimeObject) || runtimeObject == null)
                throw new InvalidOperationException($"RuntimeStore '{store.Id}' hierarchy references missing object {objectId}.");

            var origin = runtimeObject.Origin;
            if (!origin.InstanceGuid.isValid || origin.InstanceGuid != runtimeObject.GUID)
                throw new InvalidOperationException($"Replicated runtime object {objectId} has no stable GA instance origin.");
            if (!origin.Asset.AssetGuid.isValid || string.IsNullOrWhiteSpace(origin.Asset.MaterializedContentHash))
                throw new InvalidOperationException($"Replicated runtime object {objectId} has no exact GA baseline.");

            output.Add(new RuntimeStoreBaselineSpawn
            {
                ObjectId = objectId,
                InstanceGuid = origin.InstanceGuid,
                ParentObjectId = parentObjectId,
                SiblingIndex = siblingIndex,
                AssetNetId = assetCatalog.GetRequiredNetId(origin.Asset),
                Overrides = buildOverrides(runtimeObject)
                            ?? throw new InvalidOperationException($"Override builder returned null for runtime object {objectId}."),
            });

            if (!store.TryTakeChildren(objectId, out var children) || children == null)
                return;
            for (var i = 0; i < children.Count; i++)
                AppendSubtree(store, assetCatalog, buildOverrides, output, children[i], objectId, i);
        }
    }
}
