using System;
using System.Collections.Generic;
using DingoECSUtils.DotsPrefabLibAccess;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;
using Unity.Entities;
using Unity.Scenes;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    [RuntimeDotsDerived]
    public struct RuntimeBakedEntityGraphLive : IComponentData {}

    [RuntimeDotsDerived]
    public struct RuntimeBakedEntityPreparationDisabled : IComponentData {}

    [RuntimeDotsDerived]
    public struct RuntimeBakedEntityGraphLease : ICleanupComponentData
    {
        public ulong Handle;
    }

    public class RuntimeBakedEntityGraphOwnership
    {
        public readonly Entity Root;
        public readonly BakedEntityPrefabLease Lease;
        public readonly IReadOnlyList<Entity> Entities;

        public RuntimeBakedEntityGraphOwnership(Entity root, BakedEntityPrefabLease lease, IReadOnlyList<Entity> entities)
        {
            Root = root;
            Lease = lease;
            var snapshot = new Entity[entities.Count];
            for (var index = 0; index < snapshot.Length; index++)
            {
                snapshot[index] = entities[index];
            }
            Entities = Array.AsReadOnly(snapshot);
        }
    }

    /// <summary>Preparation-time graph compilation. Resource ownership is independent of entity identity.</summary>
    public static class RuntimeBakedEntityFactory
    {
        public static Entity CreateOwnedPrefab(EntityCommandBuffer ownerCommands, EntityManager entityManager, RuntimeStore store, Entity factoryRoot, RuntimeInstance factoryInstance, ulong productId, GameRuntimeObject productDefinition, BakedEntityPrefabLease template)
        {
            if (productId == 0 || productDefinition == null)
                throw new ArgumentException("A product definition and nonzero product id are required.");
            if (template == null || !template.IsReady || template.World != store.World || entityManager.World != store.World)
                throw new InvalidOperationException("The baked prefab must be prepared in the product Store's live World.");
            var sourceGraph = GetGraph(entityManager, template.PrefabRoot);
            ValidateTechnicalGraph(entityManager, sourceGraph, false);
            var retained = template.Retain();
            Entity root = Entity.Null;
            Entity[] graph = null;
            try
            {
                root = entityManager.Instantiate(template.PrefabRoot);
                graph = GetGraph(entityManager, root);
                RestoreCleanupSignature(entityManager, sourceGraph, graph);
                DetachSceneOwnership(entityManager, graph);
                entityManager.AddComponentData(root, new RuntimeEntityFactoryOwner { FactoryRoot = factoryRoot, FactoryInstance = factoryInstance });
                entityManager.AddComponentData(root, new RuntimeEntityFactoryProductIdentity { ProductId = productId });
                entityManager.AddComponent<RuntimeBakedEntityGraphLive>(root);
                entityManager.World.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>().TrackGraph(root, retained, graph);
                SetPreparationState(entityManager, graph, true);
                using var projection = new EntityCommandBuffer(Allocator.Temp);
                productDefinition.SetupOwnedEntityProjection(store, projection, root);
                projection.Playback(entityManager);
                var projectedGraph = GetGraph(entityManager, root);
                if (projectedGraph.Length != graph.Length)
                    throw new InvalidOperationException("Product projection may configure the technical graph, but cannot replace its topology.");
                for (var index = 0; index < graph.Length; index++)
                {
                    if (projectedGraph[index] != graph[index])
                        throw new InvalidOperationException("Product projection cannot replace or reorder the technical graph.");
                }
                ValidateTechnicalGraph(entityManager, graph, true);
                entityManager.World.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>().ResolveGraph(root);
                SetPreparationState(entityManager, graph, true);
                ownerCommands.AppendToBuffer(factoryRoot, new LinkedEntityGroup { Value = root });
                return root;
            }
            catch
            {
                RollbackGraph(entityManager, root, graph, retained);
                throw;
            }
        }

        public static Entity InstantiateOwnedEntity(EntityCommandBuffer ownerCommands, EntityManager entityManager, Entity factoryRoot, RuntimeInstance factoryInstance, ulong productId, in RuntimeOwnedEntityPrefab prefab)
        {
            if (productId == 0)
                throw new ArgumentOutOfRangeException(nameof(productId));
            var ownership = entityManager.World.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>();
            var source = ownership.ResolveGraph(prefab.Entity);
            if (!source.Lease.IsReady || source.Lease.World != entityManager.World)
                throw new InvalidOperationException("The prepared baked graph's resource lease is unavailable.");
            var retained = source.Lease.Retain();
            Entity root = Entity.Null;
            Entity[] graph = null;
            try
            {
                root = entityManager.Instantiate(prefab.Entity);
                graph = GetGraph(entityManager, root);
                RestoreCleanupSignature(entityManager, GetGraph(entityManager, prefab.Entity), graph);
                entityManager.SetComponentData(root, new RuntimeEntityFactoryOwner { FactoryRoot = factoryRoot, FactoryInstance = factoryInstance });
                entityManager.SetComponentData(root, new RuntimeEntityFactoryProductIdentity { ProductId = productId });
                ownership.TrackGraph(root, retained, graph);
                ownerCommands.AppendToBuffer(factoryRoot, new LinkedEntityGroup { Value = root });
                foreach (var entity in graph)
                {
                    if (!entityManager.HasComponent<RuntimeBakedEntityPreparationDisabled>(entity))
                        continue;
                    ownerCommands.RemoveComponent<Disabled>(entity);
                    ownerCommands.RemoveComponent<RuntimeBakedEntityPreparationDisabled>(entity);
                }
                return root;
            }
            catch
            {
                RollbackGraph(entityManager, root, graph, retained);
                throw;
            }
        }

        public static Entity[] GetGraph(EntityManager entityManager, Entity root)
        {
            if (!entityManager.Exists(root))
                throw new InvalidOperationException("The technical prefab root no longer exists.");
            if (!entityManager.HasBuffer<LinkedEntityGroup>(root))
                return new[] { root };
            var group = entityManager.GetBuffer<LinkedEntityGroup>(root);
            if (group.Length == 0 || group[0].Value != root)
                throw new InvalidOperationException("A technical prefab graph must list its root first.");
            var graph = new Entity[group.Length];
            var members = new HashSet<Entity>();
            for (var index = 0; index < group.Length; index++)
            {
                var entity = group[index].Value;
                if (!entityManager.Exists(entity) || !members.Add(entity))
                    throw new InvalidOperationException("A technical prefab graph contains a missing or repeated entity.");
                graph[index] = entity;
            }
            foreach (var entity in graph)
            {
                if (!entityManager.HasBuffer<LinkedEntityGroup>(entity))
                    continue;
                var nested = entityManager.GetBuffer<LinkedEntityGroup>(entity);
                if (nested.Length == 0 || nested[0].Value != entity)
                    throw new InvalidOperationException("A nested technical graph must list its own root first.");
                var nestedMembers = new HashSet<Entity>();
                for (var index = 0; index < nested.Length; index++)
                {
                    var member = nested[index].Value;
                    if (!members.Contains(member) || !nestedMembers.Add(member) || (entity != root && member == root))
                        throw new InvalidOperationException("A nested technical graph escapes or repeats its ownership.");
                }
            }
            return graph;
        }

        public static bool TryValidateGraph(EntityManager entityManager, Entity root, HashSet<Entity> claimed, out string error)
        {
            try
            {
                var graph = GetGraph(entityManager, root);
                ValidateTechnicalGraph(entityManager, graph, true);
                if (entityManager.HasComponent<RuntimeBakedEntityGraphLive>(root) || entityManager.HasComponent<RuntimeBakedEntityGraphLease>(root))
                    entityManager.World.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>().ResolveGraph(root);
                foreach (var entity in graph)
                {
                    if (claimed.Contains(entity))
                    {
                        error = "Factory products share mutable technical graph entities.";
                        return false;
                    }
                }
                foreach (var entity in graph)
                {
                    claimed.Add(entity);
                }
                error = null;
                return true;
            }
            catch (InvalidOperationException exception)
            {
                error = exception.Message;
                return false;
            }
        }

        public static void ReleaseRetiredGraphs(EntityManager entityManager)
        {
            entityManager.World.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>().ReleaseRetiredGraphs();
        }

        private static void RollbackGraph(EntityManager entityManager, Entity root, Entity[] graph, BakedEntityPrefabLease retained)
        {
            if (graph == null)
            {
                if (root == Entity.Null || !entityManager.Exists(root))
                {
                    retained.Dispose();
                    return;
                }
                graph = new[] { root };
            }
            // Never traverse a LinkedEntityGroup modified by a failing projector: it may now name borrowed entities.
            // The original clone snapshot is the ownership boundary, even when playback failed midway.
            var anchor = root != Entity.Null && entityManager.Exists(root) ? root : entityManager.CreateEntity();
            entityManager.World.GetOrCreateSystemManaged<RuntimeBakedEntityGraphCleanupSystem>().RestoreGraphTracking(root, anchor, retained, graph);
            using var remaining = new NativeList<Entity>(Allocator.Temp);
            foreach (var entity in graph)
            {
                if (!entityManager.Exists(entity))
                    continue;
                if (entityManager.HasBuffer<LinkedEntityGroup>(entity))
                    entityManager.RemoveComponent<LinkedEntityGroup>(entity);
                remaining.Add(entity);
            }
            entityManager.DestroyEntity(remaining.AsArray());
            if (anchor != root)
                entityManager.DestroyEntity(anchor);
            if (entityManager.Exists(anchor) && entityManager.HasComponent<RuntimeBakedEntityGraphLive>(anchor))
                entityManager.RemoveComponent<RuntimeBakedEntityGraphLive>(anchor);
            ReleaseRetiredGraphs(entityManager);
        }

        private static void ValidateTechnicalGraph(EntityManager entityManager, Entity[] graph, bool ownedRoot)
        {
            for (var index = ownedRoot ? 1 : 0; index < graph.Length; index++)
            {
                var entity = graph[index];
                if (entityManager.HasComponent<RuntimeEntityFactoryOwner>(entity) || entityManager.HasComponent<RuntimeEntityFactoryProductIdentity>(entity))
                    throw new InvalidOperationException("Technical descendants cannot declare independent factory product identity.");
                if (entityManager.HasComponent<RuntimeBakedEntityGraphLease>(entity) || entityManager.HasComponent<RuntimeBakedEntityGraphLive>(entity))
                    throw new InvalidOperationException("Technical descendants cannot declare independent baked resource ownership.");
            }
        }

        private static void RestoreCleanupSignature(EntityManager entityManager, Entity[] source, Entity[] target)
        {
            if (source.Length != target.Length)
                throw new InvalidOperationException("Cloning changed the technical prefab graph size.");
            for (var index = 0; index < source.Length; index++)
            {
                using var types = entityManager.GetComponentTypes(source[index], Allocator.Temp);
                foreach (var type in types)
                {
                    if (type == ComponentType.ReadWrite<RuntimeBakedEntityGraphLease>())
                        continue;
                    if ((type.IsCleanupComponent || type.IsCleanupBufferComponent || type.IsCleanupSharedComponent) && !entityManager.HasComponent(target[index], type))
                        entityManager.AddComponent(target[index], type);
                }
            }
        }

        private static void SetPreparationState(EntityManager entityManager, Entity[] graph, bool prefab)
        {
            foreach (var entity in graph)
            {
                if (prefab && !entityManager.HasComponent<Prefab>(entity))
                    entityManager.AddComponent<Prefab>(entity);
                if (!entityManager.HasComponent<Disabled>(entity))
                {
                    entityManager.AddComponent<RuntimeBakedEntityPreparationDisabled>(entity);
                    entityManager.AddComponent<Disabled>(entity);
                }
            }
        }

        private static void DetachSceneOwnership(EntityManager entityManager, Entity[] graph)
        {
            foreach (var entity in graph)
            {
                if (entityManager.HasComponent<SceneSection>(entity))
                    entityManager.RemoveComponent<SceneSection>(entity);
                if (entityManager.HasComponent<SceneTag>(entity))
                    entityManager.RemoveComponent<SceneTag>(entity);
                if (entityManager.HasComponent<RequestEntityPrefabLoaded>(entity))
                    entityManager.RemoveComponent<RequestEntityPrefabLoaded>(entity);
                if (entityManager.HasComponent<PrefabLoadResult>(entity))
                    entityManager.RemoveComponent<PrefabLoadResult>(entity);
                if (entityManager.HasComponent<PrefabRoot>(entity))
                    entityManager.RemoveComponent<PrefabRoot>(entity);
            }
        }
    }

    [UpdateInGroup(typeof(InitializationSystemGroup), OrderLast = true)]
    public partial class RuntimeBakedEntityGraphCleanupSystem : SystemBase
    {
        private readonly Dictionary<ulong, RuntimeBakedEntityGraphOwnership> _graphs = new();
        private readonly Dictionary<Entity, ulong> _handlesByRoot = new();
        private readonly Dictionary<BakedEntityPrefabLease, ulong> _handlesByLease = new();
        private EntityQuery _retiredGraphs;
        private ulong _nextHandle;

        public int TrackedGraphCount => _graphs.Count;

        protected override void OnCreate()
        {
            _retiredGraphs = GetEntityQuery(new EntityQueryDesc { All = new[] { ComponentType.ReadWrite<RuntimeBakedEntityGraphLease>() }, None = new[] { ComponentType.ReadOnly<RuntimeBakedEntityGraphLive>() }, Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab });
            RequireForUpdate(_retiredGraphs);
        }

        public RuntimeBakedEntityGraphLease TrackGraph(Entity root, BakedEntityPrefabLease lease, IReadOnlyList<Entity> entities)
        {
            return RegisterGraph(root, lease, entities, false);
        }

        public RuntimeBakedEntityGraphOwnership ResolveGraph(Entity root)
        {
            if (!EntityManager.Exists(root) || !EntityManager.HasComponent<RuntimeBakedEntityGraphLease>(root))
                throw new InvalidOperationException("The baked graph root has no resource ownership token in this World.");
            var token = EntityManager.GetComponentData<RuntimeBakedEntityGraphLease>(root);
            if (!_graphs.TryGetValue(token.Handle, out var ownership) || ownership.Root != root || !_handlesByRoot.TryGetValue(root, out var handle) || handle != token.Handle)
                throw new InvalidOperationException("The baked graph resource token does not belong to this root in this World.");
            return ownership;
        }

        public void RestoreGraphTracking(Entity originalRoot, Entity anchor, BakedEntityPrefabLease lease, IReadOnlyList<Entity> entities)
        {
            if (!_handlesByLease.TryGetValue(lease, out var handle))
            {
                RegisterGraph(anchor, lease, entities, true);
                return;
            }
            var ownership = _graphs[handle];
            if (ownership.Root != originalRoot && ownership.Root != anchor)
                throw new InvalidOperationException("Rollback cannot acquire another baked graph's resource lease.");
            if (ownership.Root != anchor)
            {
                if (EntityManager.Exists(ownership.Root) || _handlesByRoot.ContainsKey(anchor))
                    throw new InvalidOperationException("A live baked graph cannot transfer its resource ownership to another root.");
                _handlesByRoot.Remove(ownership.Root);
                _handlesByRoot.Add(anchor, handle);
                _graphs[handle] = new RuntimeBakedEntityGraphOwnership(anchor, lease, ownership.Entities);
            }
            WriteToken(anchor, new RuntimeBakedEntityGraphLease { Handle = handle });
        }

        public bool ReleaseGraph(Entity root)
        {
            var ownership = ResolveGraph(root);
            if (EntityManager.HasComponent<RuntimeBakedEntityGraphLive>(root) || HasPendingCleanup(root, ownership))
                return false;
            var token = EntityManager.GetComponentData<RuntimeBakedEntityGraphLease>(root);
            ownership.Lease.Dispose();
            _graphs.Remove(token.Handle);
            _handlesByRoot.Remove(root);
            _handlesByLease.Remove(ownership.Lease);
            EntityManager.RemoveComponent<RuntimeBakedEntityGraphLease>(root);
            return true;
        }

        public void ReleaseRetiredGraphs()
        {
            using var roots = _retiredGraphs.ToEntityArray(Allocator.Temp);
            foreach (var root in roots)
            {
                ReleaseGraph(root);
            }
        }

        protected override void OnUpdate() => ReleaseRetiredGraphs();

        private RuntimeBakedEntityGraphLease RegisterGraph(Entity root, BakedEntityPrefabLease lease, IReadOnlyList<Entity> entities, bool replaceUnboundToken)
        {
            if (!EntityManager.Exists(root) || lease == null || lease.World != World || entities == null)
                throw new InvalidOperationException("Baked graph resource ownership requires a live root, a same-World lease and its original graph snapshot.");
            if (_handlesByRoot.ContainsKey(root) || _handlesByLease.ContainsKey(lease))
                throw new InvalidOperationException("Each baked graph root must own an independent retained resource lease.");
            if (!replaceUnboundToken && EntityManager.HasComponent<RuntimeBakedEntityGraphLease>(root))
                throw new InvalidOperationException("A baked graph cannot register with a copied resource ownership token.");
            if (_nextHandle == ulong.MaxValue)
                throw new InvalidOperationException("The World's baked graph resource handle space is exhausted.");
            var token = new RuntimeBakedEntityGraphLease { Handle = ++_nextHandle };
            var ownership = new RuntimeBakedEntityGraphOwnership(root, lease, entities);
            _graphs.Add(token.Handle, ownership);
            _handlesByRoot.Add(root, token.Handle);
            _handlesByLease.Add(lease, token.Handle);
            try
            {
                WriteToken(root, token);
                return token;
            }
            catch
            {
                _graphs.Remove(token.Handle);
                _handlesByRoot.Remove(root);
                _handlesByLease.Remove(lease);
                throw;
            }
        }

        private void WriteToken(Entity root, RuntimeBakedEntityGraphLease token)
        {
            if (EntityManager.HasComponent<RuntimeBakedEntityGraphLease>(root))
                EntityManager.SetComponentData(root, token);
            else
                EntityManager.AddComponentData(root, token);
        }

        private bool HasPendingCleanup(Entity root, RuntimeBakedEntityGraphOwnership ownership)
        {
            foreach (var entity in ownership.Entities)
            {
                if (entity != root && EntityManager.Exists(entity))
                    return true;
            }
            using var types = EntityManager.GetComponentTypes(root, Allocator.Temp);
            foreach (var type in types)
            {
                if (type != ComponentType.ReadWrite<RuntimeBakedEntityGraphLease>() && (type.IsCleanupComponent || type.IsCleanupBufferComponent || type.IsCleanupSharedComponent))
                    return true;
            }
            return false;
        }
    }
}
