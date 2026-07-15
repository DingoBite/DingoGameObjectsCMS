using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Mirror.V2
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
                    replicationPolicies));
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

            var sourceOverrides = templateCache.BuildOverrides(runtimeObject, assetLock, networkPatchContext);
            var blueprint = templateCache.ResolveStrict(
                new GameAssetReference(runtimeObject.Origin.Asset.ExactKey),
                assetLock);
            var baseline = new RuntimeGameAssetBaselineComponent[blueprint.ComponentTypeIds.Count];
            for (var i = 0; i < baseline.Length; i++)
            {
                var componentTypeId = blueprint.ComponentTypeIds[i];
                baseline[i] = new RuntimeGameAssetBaselineComponent(
                    componentTypeId,
                    templateCache.CodecRegistry.Get(componentTypeId).ComponentTypeKey);
            }

            return RuntimeSpawnPatchProjector.Project(
                templateCache.CodecRegistry.SchemaHash,
                replicationPolicies,
                baseline,
                sourceOverrides);
        }

        public static RuntimeStoreBaselinePayload Build(
            RuntimeStore store,
            RuntimeSessionAssetCatalog assetCatalog,
            ulong baselineId,
            Func<GameRuntimeObject, RuntimeObjectPatch> buildOverrides)
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

            var result = new RuntimeStoreBaselinePayload
            {
                Store = new NetStoreRef(store.Id, store.StoreGeneration),
                BaselineId = baselineId,
                StoreRevision = store.StoreRevision,
            };

            var topLevel = CollectTopLevel(store);
            for (var i = 0; i < topLevel.Count; i++)
                AppendSubtree(store, assetCatalog, buildOverrides, result.Spawns, topLevel[i], -1, i);

            RuntimeStoreBaselineCodec.Validate(result);
            return result;
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
                    result.Add(storeRootChildren[childIndex]);
            }
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
