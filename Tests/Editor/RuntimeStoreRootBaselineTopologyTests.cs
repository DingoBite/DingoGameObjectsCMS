#if UNITY_EDITOR && MIRROR

using System;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeStoreRootBaselineTopologyTests
    {
        private World _world;
        private GameObject _ownedCoroutineParent;

        [SetUp]
        public void SetUp()
        {
            if (CoroutineParent.GetNoCheck() == null)
            {
                _ownedCoroutineParent = new GameObject(nameof(RuntimeStoreRootBaselineTopologyTests));
                _ownedCoroutineParent.AddComponent<CoroutineParent>();
            }
            RuntimeStores.ResetState();
            _world = new World(nameof(RuntimeStoreRootBaselineTopologyTests));
            RuntimeStores.SetupWorld(_world);
            _world.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeStores.ResetState();
            if (_world != null && _world.IsCreated)
                _world.Dispose();
            if (_ownedCoroutineParent != null)
                UnityEngine.Object.DestroyImmediate(_ownedCoroutineParent);
        }

        [Test]
        public void Baseline_PreservesRootChildAndIndependentParentlessObject()
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            try
            {
                var assetKey = new GameAssetKey("test", "runtime", "network_root_child", "1.0.0");
                asset.ResetToDefault(assetKey, Hash128.Compute("network-root-child-asset"));
                var patchCodecs = new RuntimePatchCodecRegistry("network-root-child-test");
                var templates = new GameAssetTemplateCache(patchCodecs, RuntimeTemplatePatchCodecContext.Instance);
                var blueprint = templates.GetOrCreate(new GameAssetReference(assetKey), asset);
                var assetLock = new GameAssetLibraryLock();
                assetLock.Set(assetKey, new GameAssetLibraryLockEntry(assetKey, asset.GUID, blueprint.Asset.MaterializedContentHash, asset));
                assetLock.Seal();
                var assetCatalog = RuntimeSessionAssetCatalog.FromLock(assetLock);
                var policies = new RuntimeReplicationPolicyRegistry();
                policies.Seal(Array.Empty<uint>());

                var store = RuntimeStores.GetOrAddRuntimeStore(new FixedString32Bytes("network-root-child"));
                store.TakeRootRW();
                var rootChild = store.Spawn(new GameAssetInstance(Hash128.Compute("network-root-child-instance"), new GameAssetReference(assetKey), null), assetLock, templates, parentId: RuntimeStore.STORE_ROOT_OBJECT_ID);
                var parentless = store.Spawn(new GameAssetInstance(Hash128.Compute("network-parentless-instance"), new GameAssetReference(assetKey), null), assetLock, templates);
                store.FlushToQuiescence();

                var baseline = RuntimeStoreBaselineBuilder.Build(store, assetCatalog, 1, templates, assetLock, RuntimeTemplatePatchCodecContext.Instance, policies);
                var codec = new RuntimeStoreBaselineCodec(patchCodecs);
                var decoded = codec.Decode(codec.Encode(baseline));
                Assert.That(decoded.Spawns.Single(spawn => spawn.ObjectId == rootChild.InstanceId).ParentObjectId, Is.EqualTo(RuntimeStore.STORE_ROOT_OBJECT_ID));
                Assert.That(decoded.Spawns.Single(spawn => spawn.ObjectId == rootChild.InstanceId).SiblingIndex, Is.EqualTo(0));
                Assert.That(decoded.Spawns.Single(spawn => spawn.ObjectId == parentless.InstanceId).ParentObjectId, Is.EqualTo(RuntimeStoreStructureChange.NO_PARENT_ID));
                Assert.That(decoded.Spawns.Single(spawn => spawn.ObjectId == parentless.InstanceId).SiblingIndex, Is.EqualTo(0));

                var factory = new RuntimeReplicaBaselineSpawnFactory(assetCatalog, assetLock, templates, policies);
                var staging = new RuntimeReplicaStagingRealms();
                var stager = new RuntimeReplicaBaselineStager(_world, codec, factory, staging);
                var stage = stager.Prepare(codec.Encode(baseline));
                stage.Build();
                var replica = stage.Publish();
                Assert.That(replica.TryTakeParentRO(rootChild.InstanceId, out var replicaRoot), Is.True);
                Assert.That(replicaRoot.InstanceId, Is.EqualTo(RuntimeStore.STORE_ROOT_OBJECT_ID));
                Assert.That(replicaRoot.HasEntityProjection, Is.False);
                Assert.That(replica.TryTakeParentRO(parentless.InstanceId, out _), Is.False);
                Assert.That(replica.TakeRO(rootChild.InstanceId).HasEntityProjection, Is.True);
                Assert.That(replica.TakeRO(parentless.InstanceId).HasEntityProjection, Is.True);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }
    }
}

#endif
