#if UNITY_EDITOR && MIRROR

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Mirror.Protocol;
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
    public class RuntimeRootNetworkOwnerCodec : RuntimeCheckpointNetworkOwnerCodec
    {
        public RuntimeRootNetworkOwnerCodec(uint componentTypeId) : base(componentTypeId) { }

        public override bool TryGetFieldInfo(uint fieldId, out FieldInfo field)
        {
            field = fieldId == 1u ? typeof(NetworkOwner_GRC).GetField(nameof(NetworkOwner_GRC.ConnectionId)) : null;
            return field != null;
        }
    }

    public class RuntimeStoreRootReplicationTests
    {
        private World _world;
        private GameObject _ownedCoroutineParent;

        [SetUp]
        public void SetUp()
        {
            if (CoroutineParent.GetNoCheck() == null)
            {
                _ownedCoroutineParent = new GameObject(nameof(RuntimeStoreRootReplicationTests));
                _ownedCoroutineParent.AddComponent<CoroutineParent>();
            }
            RuntimeStores.ResetState();
            _world = new World(nameof(RuntimeStoreRootReplicationTests));
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
        public void ExistingRootComponent_ReplicatesThroughBaselineAndDelta()
        {
            SynchronizeRoot(initialConnectionId: 41);
        }

        [Test]
        public void EmptyBaseline_ReplicatesRootComponentAddedLater()
        {
            SynchronizeRoot(initialConnectionId: null);
        }

        [Test]
        public void ProjectedRootChildren_KeepParentAndVisibleSiblingIndexes()
        {
            var store = RuntimeStores.GetOrAddRuntimeStore(new FixedString32Bytes("root-topology-test"));
            store.TakeRootRW();
            var hidden = store.Create();
            var first = store.Create();
            var second = store.Create();
            var independent = store.Create();
            Assert.That(store.AttachChild(RuntimeStore.STORE_ROOT_OBJECT_ID, hidden.InstanceId), Is.True);
            Assert.That(store.AttachChild(RuntimeStore.STORE_ROOT_OBJECT_ID, first.InstanceId), Is.True);
            Assert.That(store.AttachChild(RuntimeStore.STORE_ROOT_OBJECT_ID, second.InstanceId), Is.True);

            var projected = RuntimeProjectedStoreSnapshotBuilder.Build(store, 7, (connectionId, source, objectId) => objectId != hidden.InstanceId);

            Assert.That(projected.Count, Is.EqualTo(3));
            Assert.That(projected.TryGet(RuntimeStore.STORE_ROOT_OBJECT_ID, out _), Is.False);
            Assert.That(projected.TryGet(hidden.InstanceId, out _), Is.False);
            Assert.That(projected.TryGet(first.InstanceId, out var firstNode), Is.True);
            Assert.That(firstNode.ParentObjectId, Is.EqualTo(RuntimeStore.STORE_ROOT_OBJECT_ID));
            Assert.That(firstNode.SiblingIndex, Is.EqualTo(0));
            Assert.That(firstNode.Depth, Is.EqualTo(1));
            Assert.That(projected.TryGet(second.InstanceId, out var secondNode), Is.True);
            Assert.That(secondNode.ParentObjectId, Is.EqualTo(RuntimeStore.STORE_ROOT_OBJECT_ID));
            Assert.That(secondNode.SiblingIndex, Is.EqualTo(1));
            Assert.That(projected.TryGet(independent.InstanceId, out var independentNode), Is.True);
            Assert.That(independentNode.ParentObjectId, Is.EqualTo(RuntimeStoreStructureChange.NO_PARENT_ID));
            Assert.That(independentNode.SiblingIndex, Is.EqualTo(0));
            Assert.That(independentNode.Depth, Is.EqualTo(0));
        }

        [Test]
        public void RootChildReparentAndMove_ApplyAsStructuralDelta()
        {
            var storeId = new FixedString32Bytes("root-topology-delta");
            var server = RuntimeStores.GetOrAddRuntimeStore(storeId);
            var client = RuntimeStores.GetOrAddRuntimeStore(storeId, realm: StoreRealm.Client);
            client.TakeRootRW();
            var moved = client.Create();
            var existing = client.Create();
            Assert.That(client.AttachChild(RuntimeStore.STORE_ROOT_OBJECT_ID, existing.InstanceId), Is.True);
            client.FlushToQuiescence();

            var codecs = new RuntimePatchCodecRegistry("root-topology-delta");
            var policies = new RuntimeReplicationPolicyRegistry();
            policies.Seal(Array.Empty<uint>());
            var assetLock = new GameAssetLibraryLock().Seal();
            var assetCatalog = RuntimeSessionAssetCatalog.FromLock(assetLock);
            var templates = new GameAssetTemplateCache(codecs, RuntimeTemplatePatchCodecContext.Instance);
            var streams = RuntimeStateStreamProfileRegistry.CreateEmptySealed();
            var descriptor = new RuntimeSessionDescriptor
            {
                ProtocolVersion = RuntimeProtocol.VERSION,
                BuildId = "root-topology-delta",
                RuntimeSchemaHash = codecs.SchemaHash,
                AssetCatalogHash = RuntimeSessionCatalogHasher.CalculateAssets(assetCatalog.ManifestEntries),
                ResourceCatalogHash = "resources",
                StateStreamCatalogHash = streams.CatalogHash,
            };
            var manifest = new RuntimeSessionManifestTemplate(descriptor, assetCatalog.ManifestEntries, new[] { new RuntimeStoreCatalogEntry { StoreId = storeId, StoreGeneration = server.StoreGeneration } });
            var context = new RuntimeProtocolContext(manifest, assetCatalog, assetLock, templates, codecs, policies, _world, streams);
            var reference = new NetStoreRef(storeId, client.StoreGeneration);
            var delta = new RuntimeStoreDeltaPayload
            {
                Store = reference,
                BaselineId = 1,
                DeliverySequence = 1,
                FromRevision = client.StoreRevision,
                ToRevision = client.StoreRevision + 1,
                Operations = new List<RuntimeStoreDeltaOperation>
                {
                    new() { Kind = RuntimeStoreDeltaOperationKind.Reparent, ObjectId = moved.InstanceId, ParentObjectId = RuntimeStore.STORE_ROOT_OBJECT_ID, SiblingIndex = 1 },
                    new() { Kind = RuntimeStoreDeltaOperationKind.Move, ObjectId = moved.InstanceId, ParentObjectId = RuntimeStore.STORE_ROOT_OBJECT_ID, SiblingIndex = 0 },
                },
            };
            var baselineCodec = new RuntimeStoreBaselineCodec(codecs);
            var spawnFactory = new RuntimeReplicaBaselineSpawnFactory(assetCatalog, assetLock, templates, policies);
            var stagingRealms = new RuntimeReplicaStagingRealms();
            var stager = new RuntimeReplicaBaselineStager(_world, baselineCodec, spawnFactory, stagingRealms);
            var transaction = new RuntimeReplicaDeltaTransaction(context, stager, spawnFactory, stagingRealms);
            var payload = new RuntimeStoreDeltaCodec(codecs).Encode(delta);
            var envelope = new RuntimeClientDeltaEnvelope(1, reference, delta.BaselineId, delta.DeliverySequence, delta.FromRevision, delta.ToRevision, payload);

            Assert.That(transaction.TryApply(envelope), Is.True, transaction.LastFailure?.ToString());
            Assert.That(client.TryTakeChildren(RuntimeStore.STORE_ROOT_OBJECT_ID, out var children), Is.True);
            Assert.That(children, Is.EqualTo(new[] { moved.InstanceId, existing.InstanceId }));
            Assert.That(client.TryTakeParentRO(moved.InstanceId, out var parent), Is.True);
            Assert.That(parent.InstanceId, Is.EqualTo(RuntimeStore.STORE_ROOT_OBJECT_ID));
        }

        private void SynchronizeRoot(int? initialConnectionId)
        {
            if (!RuntimeComponentTypeRegistry.TryGetId(typeof(NetworkOwner_GRC), out var componentTypeId))
                Assert.Ignore("The compiled runtime component registry is not initialized for NetworkOwner_GRC.");

            var storeId = new FixedString32Bytes("root-replication-test");
            var server = RuntimeStores.GetOrAddRuntimeStore(storeId);
            if (initialConnectionId.HasValue)
            {
                server.TakeRootRW().AddOrReplace(new NetworkOwner_GRC { ConnectionId = initialConnectionId.Value });
                server.FlushToQuiescence();
            }

            var codecs = new RuntimePatchCodecRegistry("root-replication-test");
            codecs.Register(new RuntimeRootNetworkOwnerCodec(componentTypeId));
            var policies = new RuntimeReplicationPolicyRegistry();
            policies.Register(componentTypeId, RuntimeReplicationPolicy.BaselineAndReliableOverrides);
            policies.Seal(new[] { componentTypeId });
            var assetLock = new GameAssetLibraryLock().Seal();
            var assetCatalog = RuntimeSessionAssetCatalog.FromLock(assetLock);
            var templates = new GameAssetTemplateCache(codecs, RuntimeTemplatePatchCodecContext.Instance);
            var streams = RuntimeStateStreamProfileRegistry.CreateEmptySealed();
            var storeReference = new NetStoreRef(storeId, server.StoreGeneration);
            var descriptor = new RuntimeSessionDescriptor
            {
                ProtocolVersion = RuntimeProtocol.VERSION,
                BuildId = "root-replication-test",
                RuntimeSchemaHash = codecs.SchemaHash,
                AssetCatalogHash = RuntimeSessionCatalogHasher.CalculateAssets(assetCatalog.ManifestEntries),
                ResourceCatalogHash = "resources",
                StateStreamCatalogHash = streams.CatalogHash,
            };
            var manifest = new RuntimeSessionManifestTemplate(descriptor, assetCatalog.ManifestEntries, new[] { new RuntimeStoreCatalogEntry { StoreId = storeId, StoreGeneration = server.StoreGeneration } });
            var context = new RuntimeProtocolContext(manifest, assetCatalog, assetLock, templates, codecs, policies, _world, streams);
            var projection = new RuntimeServerStoreProjection(context);
            var shadow = new RuntimeConnectionStoreShadow();
            var state = new RuntimeConnectionStoreReplicationState(storeReference, server.StoreRevision);
            var baseline = projection.BuildBaseline(server, 7, 1, shadow, out var membership);
            var baselineCodec = new RuntimeStoreBaselineCodec(codecs);
            var baselineBytes = baselineCodec.Encode(baseline);
            state.BeginBaseline(baseline.StoreRevision, baselineBytes, membership);

            var spawnFactory = new RuntimeReplicaBaselineSpawnFactory(assetCatalog, assetLock, templates, policies);
            var stagingRealms = new RuntimeReplicaStagingRealms();
            var stager = new RuntimeReplicaBaselineStager(_world, baselineCodec, spawnFactory, stagingRealms);
            var stage = stager.Prepare(baselineBytes);
            stage.Build();
            var replica = stage.Publish();
            Assert.That(replica.TryTakeRO(RuntimeStore.STORE_ROOT_OBJECT_ID, out var replicaRoot), Is.True);
            Assert.That(replicaRoot.TakeRO<NetworkOwner_GRC>()?.ConnectionId, Is.EqualTo(initialConnectionId));
            Assert.That(membership, Is.Empty);

            var revisions = new List<RuntimeStoreRevisionRecord>();
            void Capture(RuntimeStoreCommittedBatch batch) => revisions.Add(RuntimeStoreRevisionRecord.CopyFrom(batch));
            server.CommittedBatch += Capture;
            if (initialConnectionId.HasValue)
                server.TakeRootRW().TakeRW<NetworkOwner_GRC>().ConnectionId = 73;
            else
                server.TakeRootRW().AddOrReplace(new NetworkOwner_GRC { ConnectionId = 73 });
            server.FlushToQuiescence();
            server.CommittedBatch -= Capture;
            var revision = revisions.Single(value => value.ComponentStructureChanges.Any(change => change.Id == RuntimeStore.STORE_ROOT_OBJECT_ID) || value.ComponentChanges.Any(change => change.Id == RuntimeStore.STORE_ROOT_OBJECT_ID));
            var delta = projection.BuildDelta(server, revision, 7, state, shadow, state.HighestDeliverySequence + 1, out var enters, out var leaves);
            Assert.That(delta, Is.Not.Null);
            Assert.That(delta.Operations, Has.Count.EqualTo(1));
            Assert.That(delta.Operations[0].Kind, Is.EqualTo(RuntimeStoreDeltaOperationKind.Patch));
            Assert.That(delta.Operations[0].ObjectId, Is.EqualTo(RuntimeStore.STORE_ROOT_OBJECT_ID));
            Assert.That(enters, Is.Empty);
            Assert.That(leaves, Is.Empty);

            var deltaBytes = new RuntimeStoreDeltaCodec(codecs).Encode(delta);
            var envelope = new RuntimeClientDeltaEnvelope(1, storeReference, delta.BaselineId, delta.DeliverySequence, delta.FromRevision, delta.ToRevision, deltaBytes);
            var transaction = new RuntimeReplicaDeltaTransaction(context, stager, spawnFactory, stagingRealms);
            Assert.That(transaction.TryApply(envelope), Is.True, transaction.LastFailure?.ToString());
            Assert.That(replica.TakeRootRO().TakeRO<NetworkOwner_GRC>().ConnectionId, Is.EqualTo(73));
            Assert.That(replica.StoreRevision, Is.EqualTo(delta.ToRevision));
        }
    }
}

#endif
