using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using NUnit.Framework;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.TestTools;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public struct RuntimeEntityFactoryProductTestData : IComponentData
    {
        public int Value;
    }

    [BurstCompile]
    public struct RuntimeEntityFactoryWarmTickTestJob : IJobChunk
    {
        public ComponentTypeHandle<RuntimeEntityFactoryProductTestData> ProductType;

        public void Execute(
            in ArchetypeChunk chunk,
            int unfilteredChunkIndex,
            bool useEnabledMask,
            in v128 chunkEnabledMask)
        {
            var products = chunk.GetNativeArray(ref ProductType);
            for (var i = 0; i < products.Length; i++)
            {
                var product = products[i];
                product.Value++;
                products[i] = product;
            }
        }
    }

    public class RuntimeEntityFactoryManagedProjectionTestComponent :
        GameRuntimeComponent<RuntimeEntityFactoryManagedProjectionTestComponent>
    {
        public int Value;
    }

    public class RuntimeEntityFactoryPassiveMutationTestComponent : GameRuntimeComponent
    {
        public int Value;
    }

    public class RuntimeEntityFactoryTestComponent : GameRuntimeEntityFactoryComponent
    {
        public static EntityArchetype ProductArchetype;

        public int EmptyProductCount;
        public int ArchetypeProductCount;

        public override void SetupForEntity(
            RuntimeStore store,
            EntityCommandBuffer ecb,
            GameRuntimeObject runtimeObject,
            Entity root)
        {
            for (var i = 0; i < EmptyProductCount; i++)
            {
                ecb.CreateOwnedEntity(root, runtimeObject.RuntimeInstance);
            }

            for (var i = 0; i < ArchetypeProductCount; i++)
            {
                var entity = ecb.CreateOwnedEntity(
                    root,
                    runtimeObject.RuntimeInstance,
                    ProductArchetype);
                ecb.SetComponent(entity, new RuntimeEntityFactoryProductTestData { Value = i });
            }
        }
    }

    public class RuntimeEntityFactoryProfileTests
    {
        private const uint FACTORY_COMPONENT_ID = 1;
        private const uint MANAGED_COMPONENT_ID = 2;
        private const uint PASSIVE_COMPONENT_ID = 3;

        private World _world;
        private EntityManager _entityManager;
        private RuntimeStore _store;
        private GameObject _ownedCoroutineParent;

        [SetUp]
        public void SetUp()
        {
            EnsureCoroutineParent();
            RuntimeStores.ResetState();
            _world = new World(nameof(RuntimeEntityFactoryProfileTests));
            _entityManager = _world.EntityManager;
            RuntimeStores.SetupWorld(_world);
            _store = RuntimeStores.GetOrAddRuntimeStore("entity-factory-profile-tests");
            RuntimeEntityFactoryTestComponent.ProductArchetype = _entityManager.CreateArchetype(
                typeof(RuntimeEntityFactoryOwner),
                typeof(RuntimeEntityFactoryProductTestData));
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeEntityFactoryTestComponent.ProductArchetype = default;
            if (_world != null && _world.IsCreated)
                _world.Dispose();
            RuntimeStores.ResetState();
            if (_ownedCoroutineParent != null)
                Object.DestroyImmediate(_ownedCoroutineParent);
        }

        [Test]
        public void ManagedRuntimeComponentProjection_RemainsUnchanged()
        {
            var runtimeObject = _store.Create();
            var component = new RuntimeEntityFactoryManagedProjectionTestComponent { Value = 17 };
            runtimeObject.AddOrReplaceById(MANAGED_COMPONENT_ID, component);

            var entity = runtimeObject.CreateEntity();
            PlaybackEditingCommands();

            Assert.That(
                _entityManager.HasComponent<RuntimeEntityFactoryManagedProjectionTestComponent>(entity),
                Is.True);
            Assert.That(
                _entityManager.GetComponentObject<RuntimeEntityFactoryManagedProjectionTestComponent>(entity),
                Is.SameAs(component));
            Assert.That(_entityManager.HasComponent<RuntimeEntityFactoryTag>(entity), Is.False);
        }

        [Test]
        public void FactoryProjection_CreatesTaggedRootAndOwnedProductsWithoutManagedFactoryComponent()
        {
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(FACTORY_COMPONENT_ID, new RuntimeEntityFactoryTestComponent
            {
                EmptyProductCount = 1,
                ArchetypeProductCount = 2
            });

            var entity = runtimeObject.CreateEntity();
            PlaybackEditingCommands();

            Assert.That(_entityManager.HasComponent<RuntimeEntityFactoryTag>(entity), Is.True);
            Assert.That(_entityManager.HasComponent<RuntimeEntityFactoryTestComponent>(entity), Is.False);

            var linkedEntities = _entityManager.GetBuffer<LinkedEntityGroup>(entity);
            Assert.That(linkedEntities.Length, Is.EqualTo(4));
            Assert.That(linkedEntities[0].Value, Is.EqualTo(entity));

            using var query = _entityManager.CreateEntityQuery(typeof(RuntimeEntityFactoryOwner));
            using var products = query.ToEntityArray(Allocator.Temp);
            Assert.That(products.Length, Is.EqualTo(3));
            var archetypeProducts = 0;
            foreach (var product in products)
            {
                var owner = _entityManager.GetComponentData<RuntimeEntityFactoryOwner>(product);
                Assert.That(owner.FactoryRoot, Is.EqualTo(entity));
                Assert.That(owner.FactoryInstance.Id, Is.EqualTo(runtimeObject.InstanceId));
                Assert.That(owner.FactoryInstance.StoreId, Is.EqualTo(runtimeObject.RuntimeInstance.StoreId));
                Assert.That(owner.FactoryInstance.Epoch, Is.EqualTo(runtimeObject.RuntimeInstance.Epoch));
                if (_entityManager.HasComponent<RuntimeEntityFactoryProductTestData>(product))
                    archetypeProducts++;
            }

            Assert.That(archetypeProducts, Is.EqualTo(2));
        }

        [Test]
        public void RemovingFactoryRoot_DestroysTenThousandOwnedProducts()
        {
            const int productCount = 10_000;
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(FACTORY_COMPONENT_ID, new RuntimeEntityFactoryTestComponent
            {
                ArchetypeProductCount = productCount
            });

            var root = runtimeObject.CreateEntity();
            PlaybackEditingCommands();
            using var query = _entityManager.CreateEntityQuery(typeof(RuntimeEntityFactoryOwner));
            Assert.That(query.CalculateEntityCount(), Is.EqualTo(productCount));

            Assert.That(_store.Remove(runtimeObject.InstanceId), Is.True);
            PlaybackEditingCommands();

            Assert.That(_entityManager.Exists(root), Is.False);
            Assert.That(query.CalculateEntityCount(), Is.Zero);
        }

        [Test]
        public void TenThousandProducts_WarmedDotsTickDoesNotTouchStoreOrAllocateManagedMemory()
        {
            const int productCount = 10_000;
            const int warmupTicks = 8;
            const int measuredTicks = 32;
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(FACTORY_COMPONENT_ID, new RuntimeEntityFactoryTestComponent
            {
                ArchetypeProductCount = productCount
            });

            var root = runtimeObject.CreateEntity();
            PlaybackEditingCommands();
            using var query = _entityManager.CreateEntityQuery(
                ComponentType.ReadWrite<RuntimeEntityFactoryProductTestData>());
            Assert.That(query.CalculateEntityCount(), Is.EqualTo(productCount));
            var firstProduct = _entityManager.GetBuffer<LinkedEntityGroup>(root)[1].Value;

            for (var tick = 0; tick < warmupTicks; tick++)
            {
                UpdateProducts(query);
            }

            var storeRevision = _store.StoreRevision;
            var dirtyPublishVersion = _store.DirtyPublishVersion;
            _ = System.GC.GetAllocatedBytesForCurrentThread();
            var allocatedBefore = System.GC.GetAllocatedBytesForCurrentThread();
            for (var tick = 0; tick < measuredTicks; tick++)
            {
                UpdateProducts(query);
            }
            var allocatedBytes =
                System.GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

            Assert.That(allocatedBytes, Is.Zero);
            Assert.That(_store.StoreRevision, Is.EqualTo(storeRevision));
            Assert.That(_store.DirtyPublishVersion, Is.EqualTo(dirtyPublishVersion));
            Assert.That(runtimeObject.EntityFactoryMutationRevision, Is.Zero);
            Assert.That(
                _entityManager.GetComponentData<RuntimeEntityFactoryProductTestData>(firstProduct).Value,
                Is.EqualTo(warmupTicks + measuredTicks));
        }

        [Test]
        public void Validator_DetectsEverySupportedMutationPathWithoutAutomaticLogging()
        {
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(
                FACTORY_COMPONENT_ID,
                new RuntimeEntityFactoryTestComponent());
            runtimeObject.CreateEntity();
            PlaybackEditingCommands();

            var initial = RuntimeEntityFactoryValidator.Validate(runtimeObject);
            Assert.That(initial.IsFactory, Is.True);
            Assert.That(initial.HasEntityProjection, Is.True);
            Assert.That(initial.MutationRevision, Is.Zero);
            Assert.That(initial.IsValid, Is.True);

            Assert.DoesNotThrow(() => runtimeObject.TakeRW<RuntimeEntityFactoryTestComponent>());
            Assert.DoesNotThrow(() => runtimeObject.SetDirtyById(FACTORY_COMPONENT_ID));
            Assert.DoesNotThrow(() => runtimeObject.AddOrReplaceById(
                PASSIVE_COMPONENT_ID,
                new RuntimeEntityFactoryPassiveMutationTestComponent { Value = 1 }));
            Assert.DoesNotThrow(() => runtimeObject.AddOrReplaceById(
                PASSIVE_COMPONENT_ID,
                new RuntimeEntityFactoryPassiveMutationTestComponent { Value = 2 }));
            Assert.DoesNotThrow(() => runtimeObject.RemoveByTypeId(PASSIVE_COMPONENT_ID));
            Assert.DoesNotThrow(() => _store.TakeRW(runtimeObject.InstanceId));

            var report = RuntimeEntityFactoryValidator.Validate(runtimeObject);
            Assert.That(report.IsValid, Is.False);
            Assert.That(report.MutationRevision, Is.EqualTo(6));
            Assert.That(report.Error, Does.Contain("changed after entity projection"));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void AddingFactoryComponentAfterNormalProjection_IsReportedAsInvalid()
        {
            var runtimeObject = _store.Create();
            var root = runtimeObject.CreateEntity();
            PlaybackEditingCommands();

            runtimeObject.AddOrReplaceById(
                FACTORY_COMPONENT_ID,
                new RuntimeEntityFactoryTestComponent());

            var report = RuntimeEntityFactoryValidator.Validate(runtimeObject);
            Assert.That(report.IsFactory, Is.True);
            Assert.That(report.HasEntityProjection, Is.True);
            Assert.That(report.MutationRevision, Is.EqualTo(1));
            Assert.That(report.IsValid, Is.False);
            Assert.That(_entityManager.HasComponent<RuntimeEntityFactoryTag>(root), Is.False);
            Assert.That(_entityManager.HasBuffer<LinkedEntityGroup>(root), Is.False);
        }

        [Test]
        public void RelinkedFactoryProjection_RestoresMutationTrackingAndValidatesRoot()
        {
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(
                FACTORY_COMPONENT_ID,
                new RuntimeEntityFactoryTestComponent());

            var root = _entityManager.CreateEntity();
            _entityManager.AddComponent<RuntimeEntityFactoryTag>(root);
            var linkedEntities =
                _entityManager.AddBuffer<LinkedEntityGroup>(root);
            linkedEntities.Add(root);
            _store.LinkEntity(runtimeObject.InstanceId, root);

            var initial = RuntimeEntityFactoryValidator.Validate(runtimeObject);
            Assert.That(initial.IsValid, Is.True);
            Assert.That(initial.MutationRevision, Is.Zero);

            Assert.That(
                _store.TryTakeRW(runtimeObject.InstanceId, out _),
                Is.True);

            var mutated = RuntimeEntityFactoryValidator.Validate(runtimeObject);
            Assert.That(mutated.IsValid, Is.False);
            Assert.That(mutated.MutationRevision, Is.EqualTo(1));
        }

        [Test]
        public void RelinkedFactoryProjection_WithoutRootSignatureIsInvalid()
        {
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(
                FACTORY_COMPONENT_ID,
                new RuntimeEntityFactoryTestComponent());

            var root = _entityManager.CreateEntity();
            _store.LinkEntity(runtimeObject.InstanceId, root);

            var report =
                RuntimeEntityFactoryValidator.Validate(runtimeObject);

            Assert.That(report.IsValid, Is.False);
            Assert.That(report.Error, Does.Contain("missing its factory signature"));
        }

        [Test]
        public void Validator_RejectsLinkedProductWithMismatchedOwner()
        {
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(
                FACTORY_COMPONENT_ID,
                new RuntimeEntityFactoryTestComponent
                {
                    EmptyProductCount = 1
                });
            var root = runtimeObject.CreateEntity();
            PlaybackEditingCommands();
            var product =
                _entityManager.GetBuffer<LinkedEntityGroup>(root)[1].Value;
            var owner = _entityManager.GetComponentData<
                RuntimeEntityFactoryOwner>(product);
            owner.FactoryInstance.Id++;
            _entityManager.SetComponentData(product, owner);

            var report =
                RuntimeEntityFactoryValidator.Validate(runtimeObject);

            Assert.That(report.IsValid, Is.False);
            Assert.That(report.Error, Does.Contain("mismatched ownership"));
        }

        [Test]
        public void Validator_RejectsOwnedProductOutsideLinkedGroup()
        {
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(
                FACTORY_COMPONENT_ID,
                new RuntimeEntityFactoryTestComponent());
            var root = runtimeObject.CreateEntity();
            PlaybackEditingCommands();
            var orphan = _entityManager.CreateEntity();
            _entityManager.AddComponentData(
                orphan,
                new RuntimeEntityFactoryOwner
                {
                    FactoryRoot = root,
                    FactoryInstance = runtimeObject.RuntimeInstance
                });

            var report =
                RuntimeEntityFactoryValidator.Validate(runtimeObject);

            Assert.That(report.IsValid, Is.False);
            Assert.That(
                report.Error,
                Does.Contain("outside its LinkedEntityGroup"));
        }

        private void EnsureCoroutineParent()
        {
            if (CoroutineParent.GetNoCheck() != null)
                return;

            _ownedCoroutineParent = new GameObject(
                $"{nameof(RuntimeEntityFactoryProfileTests)} CoroutineParent");
            _ownedCoroutineParent.AddComponent<CoroutineParent>();
        }

        private void PlaybackEditingCommands()
        {
            _world.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>().Update();
        }

        private void UpdateProducts(EntityQuery query)
        {
            var job = new RuntimeEntityFactoryWarmTickTestJob
            {
                ProductType = _entityManager.GetComponentTypeHandle<
                    RuntimeEntityFactoryProductTestData>(isReadOnly: false)
            };
            job.ScheduleParallel(query, default).Complete();
        }
    }
}
