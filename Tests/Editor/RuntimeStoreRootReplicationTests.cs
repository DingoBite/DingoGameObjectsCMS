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
        private IDisposable _registryInitializationScope;
        private bool _originalRegistryInitialized;
        private string _originalRegistryHash;
        private Type[] _originalRegistryTypes;
        private int _originalRegistryCount;
        private ulong _originalRegistryEpoch;

        [SetUp]
        public void SetUp()
        {
            if (CoroutineParent.GetNoCheck() == null)
            {
                _ownedCoroutineParent = new GameObject(nameof(RuntimeStoreRootReplicationTests));
                _ownedCoroutineParent.AddComponent<CoroutineParent>();
            }
            RuntimeStores.ResetState();
            _originalRegistryInitialized = RuntimeComponentTypeRegistry.IsInitialized;
            _originalRegistryHash = RuntimeComponentTypeRegistry.RegistryHash;
            _originalRegistryTypes = RuntimeComponentTypeRegistry.TypesById.ToArray();
            _originalRegistryCount = RuntimeComponentTypeRegistry.Count;
            _originalRegistryEpoch = RuntimeComponentTypeRegistry.InitializationVersion;
            if (!RuntimeComponentTypeRegistry.TryGetId(typeof(NetworkOwner_GRC), out _))
            {
                _registryInitializationScope = RuntimeComponentTypeRegistry.InitializeScoped(new Manifest
                {
                    Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION,
                    Types = new List<Entry> { RuntimeComponentTypeRegistry.CreateEntry(1, typeof(NetworkOwner_GRC)) },
                    ReservedIds = new List<int>(),
                });
            }
            Assert.That(RuntimeComponentTypeRegistry.TryGetId(typeof(NetworkOwner_GRC), out _), Is.True);
            _world = new World(nameof(RuntimeStoreRootReplicationTests));
            RuntimeStores.SetupWorld(_world);
            _world.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                RuntimeStores.ResetState();
            }
            finally
            {
                try
                {
                    if (_world != null && _world.IsCreated)
                        _world.Dispose();
                }
                finally
                {
                    try
                    {
                        if (_ownedCoroutineParent != null)
                            UnityEngine.Object.DestroyImmediate(_ownedCoroutineParent);
                    }
                    finally
                    {
                        var scope = _registryInitializationScope;
                        _registryInitializationScope = null;
                        scope?.Dispose();
                        Assert.That(RuntimeComponentTypeRegistry.IsInitialized, Is.EqualTo(_originalRegistryInitialized));
                        Assert.That(RuntimeComponentTypeRegistry.RegistryHash, Is.EqualTo(_originalRegistryHash));
                        Assert.That(RuntimeComponentTypeRegistry.TypesById, Is.EqualTo(_originalRegistryTypes));
                        Assert.That(RuntimeComponentTypeRegistry.Count, Is.EqualTo(_originalRegistryCount));
                        if (scope != null)
                            Assert.That(RuntimeComponentTypeRegistry.InitializationVersion, Is.GreaterThan(_originalRegistryEpoch));
                    }
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ServerCoordinator_SynchronousRejectionPreservesHealthyPeersAndCannotRetireReplacement(bool replace)
        {
            Assert.That(RuntimeComponentTypeRegistry.TryGetId(typeof(NetworkOwner_GRC), out var componentTypeId), Is.True);
            var store = RuntimeStores.GetOrAddRuntimeStore(new FixedString32Bytes("map"));
            var reference = new NetStoreRef(store.Id, store.StoreGeneration);
            store.TakeRootRW().AddOrReplace(new NetworkOwner_GRC { ConnectionId = 0 });
            store.FlushToQuiescence();
            var codecs = new RuntimePatchCodecRegistry("reentrant-delivery-test");
            codecs.Register(new RuntimeRootNetworkOwnerCodec(componentTypeId));
            var policies = new RuntimeReplicationPolicyRegistry();
            policies.Register(componentTypeId, RuntimeReplicationPolicy.BaselineAndReliableOverrides);
            policies.Seal(new[] { componentTypeId });
            var assetLock = new GameAssetLibraryLock().Seal();
            var catalog = RuntimeSessionAssetCatalog.FromLock(assetLock);
            var templates = new GameAssetTemplateCache(codecs, RuntimeTemplatePatchCodecContext.Instance);
            var streams = RuntimeStateStreamProfileRegistry.CreateEmptySealed();
            var descriptor = new RuntimeSessionDescriptor { ProtocolVersion = RuntimeProtocol.VERSION, BuildId = "reentrant-delivery-test", RuntimeSchemaHash = codecs.SchemaHash, AssetCatalogHash = RuntimeSessionCatalogHasher.CalculateAssets(catalog.ManifestEntries), ResourceCatalogHash = "resources", StateStreamCatalogHash = streams.CatalogHash };
            var manifestTemplate = new RuntimeSessionManifestTemplate(descriptor, catalog.ManifestEntries, new[] { new RuntimeStoreCatalogEntry { StoreId = reference.StoreId, StoreGeneration = reference.StoreGeneration } });
            var context = new RuntimeProtocolContext(manifestTemplate, catalog, assetLock, templates, codecs, policies, _world, streams);
            var failedDelivery = false;
            var manifests = new Dictionary<int, RuntimeSessionManifestSnapshot>();
            var rejected = new List<int>();
            var invalidated = new List<int>();
            var delivered = new List<int>();
            RuntimeProtocolServerCoordinator coordinator = null;
            coordinator = new RuntimeProtocolServerCoordinator(context, new RuntimeProtocolServerOutput((id, manifest) => manifests[id] = manifest, (id, _, _) =>
            {
                rejected.Add(id);
                coordinator.RemoveConnection(id);
                if (!replace)
                    return;
                failedDelivery = false;
                coordinator.AddConnection(id);
                Assert.That(coordinator.ReceiveHello(id, descriptor, 9).Accepted, Is.True);
                Assert.That(coordinator.ReceiveReady(id, manifests[id].SessionId).Accepted, Is.True);
                coordinator.RemoveConnection(2);
                coordinator.AddConnection(2);
                Assert.That(coordinator.ReceiveHello(2, descriptor, 10).Accepted, Is.True);
                Assert.That(coordinator.ReceiveReady(2, manifests[2].SessionId).Accepted, Is.True);
            }, (_, _) => { }, (id, _) => delivered.Add(id), (_, _) => { }, (in RuntimeReliableDeltaTransportEnvelope value) =>
            {
                if (failedDelivery && value.SessionId == manifests[1].SessionId)
                    throw new InvalidOperationException("scripted delta delivery preparation failure");
                return true;
            }, (in RuntimeBaselineChunk value) =>
            {
                if (failedDelivery && value.SessionId == manifests[1].SessionId)
                    throw new InvalidOperationException("scripted rebaseline delivery preparation failure");
                return RuntimeProtocol.BASELINE_CHUNK_BYTES;
            }));
            using (coordinator)
            {
                coordinator.ConnectionInvalidated += id => invalidated.Add(id);
                for (var id = 1; id <= 3; id++)
                {
                    coordinator.AddConnection(id);
                    Assert.That(coordinator.ReceiveHello(id, descriptor, (ulong)id + 1).Accepted, Is.True);
                    Assert.That(coordinator.ReceiveReady(id, manifests[id].SessionId).Accepted, Is.True);
                }
                failedDelivery = true;

                store.TakeRootRW().TakeRW<NetworkOwner_GRC>().ConnectionId = 73;
                store.FlushToQuiescence();

                Assert.That(rejected, Is.EqualTo(new[] { 1 }));
                Assert.That(invalidated, Is.Empty, "The rejection's disconnect must not invalidate a same-id replacement.");
                Assert.That(delivered, replace ? Is.EqualTo(new[] { 3 }) : Is.EqualTo(new[] { 2, 3 }), "The committed delta must reach healthy captured peers without replaying into a replacement.");
                Assert.That(coordinator.TryGetConnectionStoreState(3, reference, out var healthy), Is.True);
                Assert.That(healthy.StoreRevision, Is.EqualTo(store.StoreRevision), "The healthy captured peer must observe this committed revision.");
                if (replace)
                {
                    Assert.That(coordinator.TryGetConnectionStoreState(1, reference, out var replacement), Is.True);
                    Assert.That(replacement.BaselineId, Is.EqualTo(1));
                }
                else
                {
                    Assert.That(coordinator.TryGetConnectionStoreState(2, reference, out var second), Is.True);
                    Assert.That(second.StoreRevision, Is.EqualTo(store.StoreRevision));
                }
            }
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
        public void ScopedRegistryInitialization_RestoresExactSchemaAfterPartialInitializationFailure()
        {
            var originalTypes = RuntimeComponentTypeRegistry.TypesById.ToArray();
            var originalHash = RuntimeComponentTypeRegistry.RegistryHash;
            var originalCount = RuntimeComponentTypeRegistry.Count;
            var originalEpoch = RuntimeComponentTypeRegistry.InitializationVersion;
            Assert.That(RuntimeComponentTypeRegistry.TryGetId(typeof(NetworkOwner_GRC), out var originalId), Is.True);
            var invalidManifest = new Manifest
            {
                Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION,
                Types = new List<Entry>
                {
                    RuntimeComponentTypeRegistry.CreateEntry(3, typeof(NetworkOwner_GRC)),
                    RuntimeComponentTypeRegistry.CreateEntry(4, typeof(NetworkOwner_GRC)),
                },
                ReservedIds = new List<int>(),
            };

            Assert.Throws<InvalidOperationException>(() => RuntimeComponentTypeRegistry.InitializeScoped(invalidManifest));

            Assert.That(RuntimeComponentTypeRegistry.IsInitialized, Is.True);
            Assert.That(RuntimeComponentTypeRegistry.TypesById, Is.EqualTo(originalTypes));
            Assert.That(RuntimeComponentTypeRegistry.RegistryHash, Is.EqualTo(originalHash));
            Assert.That(RuntimeComponentTypeRegistry.Count, Is.EqualTo(originalCount));
            Assert.That(RuntimeComponentTypeRegistry.TryGetId(typeof(NetworkOwner_GRC), out var restoredId), Is.True);
            Assert.That(restoredId, Is.EqualTo(originalId));
            Assert.That(RuntimeComponentTypeRegistry.InitializationVersion, Is.GreaterThan(originalEpoch));
        }

        [Test]
        public void ScopedRegistryInitialization_OutOfOrderDisposeCannotReplaceTheActiveSchema()
        {
            var originalTypes = RuntimeComponentTypeRegistry.TypesById.ToArray();
            var originalHash = RuntimeComponentTypeRegistry.RegistryHash;
            var originalEpoch = RuntimeComponentTypeRegistry.InitializationVersion;
            using (var outer = RuntimeComponentTypeRegistry.InitializeScoped(new Manifest
            {
                Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION,
                Types = new List<Entry>(),
                ReservedIds = new List<int> { 7 },
            }))
            {
                using (RuntimeComponentTypeRegistry.InitializeScoped(new Manifest
                {
                    Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION,
                    Types = new List<Entry> { RuntimeComponentTypeRegistry.CreateEntry(3, typeof(NetworkOwner_GRC)) },
                    ReservedIds = new List<int> { 8 },
                }))
                {
                    var activeHash = RuntimeComponentTypeRegistry.RegistryHash;
                    var activeEpoch = RuntimeComponentTypeRegistry.InitializationVersion;

                    Assert.Throws<InvalidOperationException>(() => outer.Dispose());

                    Assert.That(RuntimeComponentTypeRegistry.TryGetId(typeof(NetworkOwner_GRC), out var activeId), Is.True);
                    Assert.That(activeId, Is.EqualTo(3));
                    Assert.That(RuntimeComponentTypeRegistry.RegistryHash, Is.EqualTo(activeHash));
                    Assert.That(RuntimeComponentTypeRegistry.InitializationVersion, Is.EqualTo(activeEpoch));
                }
                Assert.That(RuntimeComponentTypeRegistry.IsInitialized, Is.True);
                Assert.That(RuntimeComponentTypeRegistry.Count, Is.Zero);
                Assert.That(RuntimeComponentTypeRegistry.TypesById.Count, Is.EqualTo(8));
            }
            Assert.That(RuntimeComponentTypeRegistry.TypesById, Is.EqualTo(originalTypes));
            Assert.That(RuntimeComponentTypeRegistry.RegistryHash, Is.EqualTo(originalHash));
            Assert.That(RuntimeComponentTypeRegistry.InitializationVersion, Is.GreaterThan(originalEpoch));
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
            Assert.That(RuntimeComponentTypeRegistry.TryGetId(typeof(NetworkOwner_GRC), out var componentTypeId), Is.True);

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
