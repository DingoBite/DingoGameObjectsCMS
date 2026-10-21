using System;
using System.Collections.Generic;
using System.Reflection;
using DingoECSUtils.DotsPrefabLibAccess;
using DingoGameObjectsCMS.Editor;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;

namespace DingoGameObjectsCMS.Tests.Editor
{
    [RuntimeDotsDerived]
    public struct BakedGraphTestReference : IComponentData
    {
        public Entity Target;
    }

    [RuntimeDotsDerived]
    public struct BakedGraphTestCleanup : ICleanupComponentData {}

    public class RuntimeBakedEntityFactoryTests
    {
        private World _world;
        private EntityManager _manager;

        [SetUp]
        public void SetUp()
        {
            _world = new World(nameof(RuntimeBakedEntityFactoryTests));
            _manager = _world.EntityManager;
        }

        [TearDown]
        public void TearDown() => _world.Dispose();

        [Test]
        public void GraphLeaseTokenIsUnmanagedAndExcludedFromPersistedState()
        {
            Assert.That(UnsafeUtility.IsUnmanaged<RuntimeBakedEntityGraphLease>(), Is.True);
            var descriptor = RuntimeDotsStateSchemaDiscovery.DescribeComponent(typeof(RuntimeBakedEntityGraphLease));
            Assert.That(descriptor.Schema.Classification, Is.EqualTo(RuntimeDotsStateClassification.Derived));
            Assert.That(descriptor.PersistedValue, Is.Null);
        }

        [Test]
        public void NativeCloneRemapsAllLinkedTechnicalEntityReferences()
        {
            var source = _manager.CreateEntity(typeof(Prefab), typeof(BakedGraphTestReference));
            var first = _manager.CreateEntity(typeof(Prefab), typeof(BakedGraphTestReference));
            var second = _manager.CreateEntity(typeof(Prefab), typeof(BakedGraphTestReference));
            SetGroup(source, source, first, second);
            SetGroup(first, first, second);
            _manager.SetComponentData(source, new BakedGraphTestReference { Target = first });
            _manager.SetComponentData(first, new BakedGraphTestReference { Target = second });
            _manager.SetComponentData(second, new BakedGraphTestReference { Target = source });

            var clone = _manager.Instantiate(source);
            var graph = RuntimeBakedEntityFactory.GetGraph(_manager, clone);

            Assert.That(graph.Length, Is.EqualTo(3));
            Assert.That(graph[0], Is.EqualTo(clone));
            CollectionAssert.DoesNotContain(graph, source);
            CollectionAssert.DoesNotContain(graph, first);
            CollectionAssert.DoesNotContain(graph, second);
            Assert.That(_manager.GetComponentData<BakedGraphTestReference>(graph[0]).Target, Is.EqualTo(graph[1]));
            Assert.That(_manager.GetComponentData<BakedGraphTestReference>(graph[1]).Target, Is.EqualTo(graph[2]));
            Assert.That(_manager.GetComponentData<BakedGraphTestReference>(graph[2]).Target, Is.EqualTo(graph[0]));
            var nested = _manager.GetBuffer<LinkedEntityGroup>(graph[1]);
            Assert.That(nested[0].Value, Is.EqualTo(graph[1]));
            Assert.That(nested[1].Value, Is.EqualTo(graph[2]));
            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, clone, new HashSet<Entity>(), out var error), Is.True, error);
        }

        [Test]
        public void ProductGraphsCannotShareAMutableTechnicalDescendant()
        {
            var first = _manager.CreateEntity();
            var second = _manager.CreateEntity();
            var shared = _manager.CreateEntity();
            SetGroup(first, first, shared);
            SetGroup(second, second, shared);
            var claimed = new HashSet<Entity>();

            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, first, claimed, out var error), Is.True, error);
            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, second, claimed, out error), Is.False);
            Assert.That(error, Does.Contain("share mutable"));
        }

        [Test]
        public void NestedTechnicalGroupCannotDestroyAnEntityOutsideItsProduct()
        {
            var root = _manager.CreateEntity();
            var child = _manager.CreateEntity();
            var foreign = _manager.CreateEntity();
            SetGroup(root, root, child);
            SetGroup(child, child, foreign);

            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, root, new HashSet<Entity>(), out var error), Is.False);
            Assert.That(error, Does.Contain("escapes"));
            Assert.That(_manager.Exists(foreign), Is.True, "Validation must not mutate another product's entity.");
        }

        [Test]
        public void MissingOrRepeatedLinkedEntitiesAreRejectedBeforeOwnershipRegistration()
        {
            var root = _manager.CreateEntity();
            var child = _manager.CreateEntity();
            SetGroup(root, root, child, child);
            var claimed = new HashSet<Entity>();

            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, root, claimed, out var error), Is.False);
            Assert.That(error, Does.Contain("missing or repeated"));
            Assert.That(claimed, Is.Empty);
            var group = _manager.GetBuffer<LinkedEntityGroup>(root);
            group.RemoveAt(group.Length - 1);
            _manager.DestroyEntity(child);
            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, root, claimed, out error), Is.False);
            Assert.That(error, Does.Contain("missing or repeated"));
            Assert.That(claimed, Is.Empty);
        }

        [Test]
        public void OnlyTheLogicalProductRootMayDeclareFactoryIdentity()
        {
            var root = _manager.CreateEntity(typeof(RuntimeEntityFactoryOwner), typeof(RuntimeEntityFactoryProductIdentity));
            var child = _manager.CreateEntity(typeof(RuntimeEntityFactoryProductIdentity));
            SetGroup(root, root, child);

            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, root, new HashSet<Entity>(), out var error), Is.False);
            Assert.That(error, Does.Contain("independent factory product identity"));
            _manager.RemoveComponent<RuntimeEntityFactoryProductIdentity>(child);
            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, root, new HashSet<Entity>(), out error), Is.True, error);
            _manager.AddComponent<RuntimeEntityFactoryOwner>(child);
            Assert.That(RuntimeBakedEntityFactory.TryValidateGraph(_manager, root, new HashSet<Entity>(), out error), Is.False);
        }

        [Test]
        public void EcbOnlyOverloadCannotSilentlyCloneAResourceOwnedBakedGraph()
        {
            var prefab = _manager.CreateEntity(typeof(Prefab), typeof(RuntimeBakedEntityGraphLive), typeof(RuntimeEntityFactoryOwner), typeof(RuntimeEntityFactoryProductIdentity));
            var captured = RuntimeEntityFactoryEcbExtensions.CaptureOwnedPrefab(_manager, prefab);
            Assert.That(captured.RequiresGraphClone, Is.True);
            using var commands = new EntityCommandBuffer(Allocator.Temp);

            Assert.Throws<InvalidOperationException>(() => commands.InstantiateOwnedEntity(Entity.Null, default, 1, captured));
        }

        [Test]
        public void ResourceLeaseWaitsForAllDescendantAndRootCleanup()
        {
            // A retired lease is sufficient to observe release without loading an actual prefab.
            using var loader = new BakedEntityPrefabLoader(_world);
            Assert.That(loader.RetireAsync().IsCompleted, Is.True);
            var lease = loader.PrepareAsync(default).GetAwaiter().GetResult();
            var root = _manager.CreateEntity(typeof(RuntimeBakedEntityGraphLive), typeof(BakedGraphTestCleanup));
            var child = _manager.CreateEntity(typeof(BakedGraphTestCleanup));
            SetGroup(root, root, child);
            var originalGraph = new[] { root, child };
            var ownership = _world.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>();
            ownership.TrackGraph(root, lease, originalGraph);
            originalGraph[1] = root;
            Assert.That(ownership.ResolveGraph(root).Entities[1], Is.EqualTo(child), "Resource cleanup must retain its own immutable graph snapshot.");
            Assert.That(ownership.ReleaseGraph(root), Is.False, "Live graph resources cannot be released through the explicit cleanup API.");

            _manager.DestroyEntity(root);
            RuntimeBakedEntityFactory.ReleaseRetiredGraphs(_manager);
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Retired));
            Assert.That(_manager.HasComponent<RuntimeBakedEntityGraphLease>(root), Is.True);

            _manager.RemoveComponent<BakedGraphTestCleanup>(child);
            Assert.That(_manager.Exists(child), Is.False);
            RuntimeBakedEntityFactory.ReleaseRetiredGraphs(_manager);
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Retired), "Root cleanup consumers also retain the resource.");

            _manager.RemoveComponent<BakedGraphTestCleanup>(root);
            RuntimeBakedEntityFactory.ReleaseRetiredGraphs(_manager);
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Released));
            Assert.That(_manager.Exists(root), Is.False);
            Assert.That(ownership.TrackedGraphCount, Is.Zero);
            RuntimeBakedEntityFactory.ReleaseRetiredGraphs(_manager);
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Released), "Repeated cleanup must remain idempotent.");
        }

        [Test]
        public void FailedProjectionRollbackRetiresOnlyOriginalSnapshotAndWaitsForItsCleanup()
        {
            using var loader = new BakedEntityPrefabLoader(_world);
            Assert.That(loader.RetireAsync().IsCompleted, Is.True);
            var lease = loader.PrepareAsync(default).GetAwaiter().GetResult();
            var root = _manager.CreateEntity(typeof(RuntimeBakedEntityGraphLive));
            var child = _manager.CreateEntity(typeof(BakedGraphTestCleanup));
            var foreign = _manager.CreateEntity(typeof(BakedGraphTestReference));
            var originalGraph = new[] { root, child };
            SetGroup(root, root, child, foreign);
            SetGroup(child, child, foreign);
            _world.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>().TrackGraph(root, lease, originalGraph);

            // The public preparation API requires an actual loaded prefab. Exercise its failure
            // boundary directly without introducing a synthetic loading backend into production.
            var rollback = typeof(RuntimeBakedEntityFactory).GetMethod("RollbackGraph", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(rollback, Is.Not.Null);
            rollback.Invoke(null, new object[] { _manager, root, originalGraph, lease });

            Assert.That(_manager.Exists(foreign), Is.True, "A failing projector cannot transfer a borrowed entity into rollback ownership.");
            Assert.That(_manager.Exists(child), Is.True, "The original child's cleanup consumer must still complete.");
            Assert.That(_manager.HasComponent<RuntimeBakedEntityGraphLive>(root), Is.False);
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Retired));

            _manager.RemoveComponent<BakedGraphTestCleanup>(child);
            RuntimeBakedEntityFactory.ReleaseRetiredGraphs(_manager);
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Released));
            Assert.That(_manager.Exists(root), Is.False);
            Assert.That(_manager.Exists(foreign), Is.True);
        }

        [Test]
        public void CopiedTokenCannotResolveOrReleaseAnotherRootsResource()
        {
            using var loader = new BakedEntityPrefabLoader(_world);
            Assert.That(loader.RetireAsync().IsCompleted, Is.True);
            var lease = loader.PrepareAsync(default).GetAwaiter().GetResult();
            var original = _manager.CreateEntity(typeof(RuntimeBakedEntityGraphLive));
            var foreign = _manager.CreateEntity();
            var ownership = _world.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>();
            var token = ownership.TrackGraph(original, lease, new[] { original });
            _manager.AddComponentData(foreign, token);

            Assert.Throws<InvalidOperationException>(() => ownership.ResolveGraph(foreign));
            Assert.Throws<InvalidOperationException>(() => ownership.ReleaseGraph(foreign));
            Assert.Throws<InvalidOperationException>(() => ownership.TrackGraph(foreign, lease, new[] { foreign }));
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Retired));
            Assert.That(ownership.TrackedGraphCount, Is.EqualTo(1));

            _manager.RemoveComponent<RuntimeBakedEntityGraphLease>(foreign);
            _manager.DestroyEntity(original);
            RuntimeBakedEntityFactory.ReleaseRetiredGraphs(_manager);
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Released));
            Assert.That(ownership.TrackedGraphCount, Is.Zero);
        }

        [Test]
        public void OwnershipRegistriesRemainIndependentAcrossWorlds()
        {
            using var otherWorld = new World("Independent Baked Graph Ownership");
            using var loader = new BakedEntityPrefabLoader(_world);
            using var otherLoader = new BakedEntityPrefabLoader(otherWorld);
            Assert.That(loader.RetireAsync().IsCompleted, Is.True);
            Assert.That(otherLoader.RetireAsync().IsCompleted, Is.True);
            var lease = loader.PrepareAsync(default).GetAwaiter().GetResult();
            var otherLease = otherLoader.PrepareAsync(default).GetAwaiter().GetResult();
            var root = _manager.CreateEntity(typeof(RuntimeBakedEntityGraphLive));
            var otherManager = otherWorld.EntityManager;
            var otherRoot = otherManager.CreateEntity(typeof(RuntimeBakedEntityGraphLive));
            var ownership = _world.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>();
            var otherOwnership = otherWorld.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>();
            var token = ownership.TrackGraph(root, lease, new[] { root });
            var otherToken = otherOwnership.TrackGraph(otherRoot, otherLease, new[] { otherRoot });
            Assert.That(token.Handle, Is.EqualTo(otherToken.Handle), "Handle allocation is deliberately World-local.");
            Assert.That(ownership.ResolveGraph(root).Lease, Is.SameAs(lease));
            Assert.That(otherOwnership.ResolveGraph(otherRoot).Lease, Is.SameAs(otherLease));

            _manager.DestroyEntity(root);
            RuntimeBakedEntityFactory.ReleaseRetiredGraphs(_manager);
            Assert.That(lease.Status, Is.EqualTo(BakedEntityPrefabStatus.Released));
            Assert.That(otherLease.Status, Is.EqualTo(BakedEntityPrefabStatus.Retired));
            Assert.That(otherOwnership.TrackedGraphCount, Is.EqualTo(1));
            otherManager.DestroyEntity(otherRoot);
            RuntimeBakedEntityFactory.ReleaseRetiredGraphs(otherManager);
            Assert.That(otherLease.Status, Is.EqualTo(BakedEntityPrefabStatus.Released));
            Assert.That(otherOwnership.TrackedGraphCount, Is.Zero);
        }

        private void SetGroup(Entity root, params Entity[] members)
        {
            var group = _manager.AddBuffer<LinkedEntityGroup>(root);
            foreach (var entity in members)
            {
                group.Add(new LinkedEntityGroup { Value = entity });
            }
        }
    }
}
