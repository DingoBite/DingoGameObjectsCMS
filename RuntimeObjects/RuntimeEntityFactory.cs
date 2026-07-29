using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.Stores;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    [Serializable, Preserve]
    public struct RuntimeEntityFactoryTag : IComponentData
    {
    }

    [Serializable, Preserve]
    public struct RuntimeEntityFactoryOwner : IComponentData
    {
        public Entity FactoryRoot;
        public RuntimeInstance FactoryInstance;
    }

    public static class RuntimeEntityFactoryEcbExtensions
    {
        public static Entity CreateOwnedEntity(
            this EntityCommandBuffer ecb,
            Entity factoryRoot,
            RuntimeInstance factoryInstance)
        {
            var entity = ecb.CreateEntity();
            var owner = CreateOwner(factoryRoot, factoryInstance);
            ecb.AddComponent(entity, owner);
            ecb.AppendToBuffer(factoryRoot, new LinkedEntityGroup { Value = entity });
            return entity;
        }

        /// <summary>
        /// Creates a product from its final archetype. The archetype must
        /// contain <see cref="RuntimeEntityFactoryOwner"/>.
        /// </summary>
        public static Entity CreateOwnedEntity(
            this EntityCommandBuffer ecb,
            Entity factoryRoot,
            RuntimeInstance factoryInstance,
            EntityArchetype archetype)
        {
            var entity = ecb.CreateEntity(archetype);
            var owner = CreateOwner(factoryRoot, factoryInstance);
            ecb.SetComponent(entity, owner);
            ecb.AppendToBuffer(factoryRoot, new LinkedEntityGroup { Value = entity });
            return entity;
        }

        /// <summary>
        /// Instantiates a product from a fully projected ECS prefab. The
        /// prefab must contain <see cref="RuntimeEntityFactoryOwner"/>.
        /// </summary>
        public static Entity InstantiateOwnedEntity(
            this EntityCommandBuffer ecb,
            Entity factoryRoot,
            RuntimeInstance factoryInstance,
            Entity prefab)
        {
            var entity = ecb.Instantiate(prefab);
            var owner = CreateOwner(factoryRoot, factoryInstance);
            ecb.SetComponent(entity, owner);
            ecb.AppendToBuffer(
                factoryRoot,
                new LinkedEntityGroup { Value = entity });
            return entity;
        }

        private static RuntimeEntityFactoryOwner CreateOwner(
            Entity factoryRoot,
            RuntimeInstance factoryInstance)
        {
            return new RuntimeEntityFactoryOwner
            {
                FactoryRoot = factoryRoot,
                FactoryInstance = factoryInstance
            };
        }
    }

    public class RuntimeEntityFactoryValidationReport
    {
        public readonly GameRuntimeObject RuntimeObject;
        public readonly bool IsFactory;
        public readonly bool HasEntityProjection;
        public readonly ulong MutationRevision;
        public readonly string Error;

        public bool IsValid => string.IsNullOrEmpty(Error);

        public RuntimeEntityFactoryValidationReport(
            GameRuntimeObject runtimeObject,
            bool isFactory,
            bool hasEntityProjection,
            ulong mutationRevision,
            string error)
        {
            RuntimeObject = runtimeObject;
            IsFactory = isFactory;
            HasEntityProjection = hasEntityProjection;
            MutationRevision = mutationRevision;
            Error = error;
        }
    }

    public static class RuntimeEntityFactoryValidator
    {
        public static RuntimeEntityFactoryValidationReport Validate(GameRuntimeObject runtimeObject)
        {
            if (runtimeObject == null)
            {
                return new RuntimeEntityFactoryValidationReport(
                    null,
                    false,
                    false,
                    0,
                    "Runtime entity factory validation requires a runtime object.");
            }

            var isFactory = runtimeObject.IsEntityFactory;
            var hasProjection = runtimeObject.HasEntityProjection;
            var mutationRevision = runtimeObject.EntityFactoryMutationRevision;
            string error = null;
            if (isFactory && hasProjection && mutationRevision != 0)
            {
                error =
                    $"Factory GRO {runtimeObject.InstanceId} in store '{runtimeObject.StoreId}' changed after entity projection (mutation revision {mutationRevision}).";
            }
            if (isFactory
                && hasProjection
                && !HasValidFactoryRoot(runtimeObject, out var rootError))
            {
                error = string.IsNullOrEmpty(error)
                    ? rootError
                    : error + " " + rootError;
            }

            return new RuntimeEntityFactoryValidationReport(
                runtimeObject,
                isFactory,
                hasProjection,
                mutationRevision,
                error);
        }

        private static bool HasValidFactoryRoot(
            GameRuntimeObject runtimeObject,
            out string error)
        {
            error = null;
            if (!RuntimeStores.TryGetRuntimeStore(
                    runtimeObject.StoreId,
                    runtimeObject.Realm,
                    out var store)
                || !store.TryGetEntity(
                    runtimeObject.InstanceId,
                    out var root)
                || store.World == null
                || !store.World.IsCreated)
            {
                error =
                    $"Factory GRO {runtimeObject.InstanceId} in store '{runtimeObject.StoreId}' has no active Entity projection.";
                return false;
            }

            var entityManager = store.World.EntityManager;
            if (!entityManager.Exists(root)
                || !entityManager.HasComponent<RuntimeEntityFactoryTag>(root)
                || !entityManager.HasBuffer<LinkedEntityGroup>(root))
            {
                error =
                    $"Factory GRO {runtimeObject.InstanceId} in store '{runtimeObject.StoreId}' root is missing its factory signature.";
                return false;
            }

            var linkedEntities =
                entityManager.GetBuffer<LinkedEntityGroup>(root);
            if (linkedEntities.Length == 0
                || linkedEntities[0].Value != root)
            {
                error =
                    $"Factory GRO {runtimeObject.InstanceId} in store '{runtimeObject.StoreId}' LinkedEntityGroup must contain the root first.";
                return false;
            }

            var expectedOwner = runtimeObject.RuntimeInstance;
            var linkedProducts = new HashSet<Entity>();
            for (var i = 1; i < linkedEntities.Length; i++)
            {
                var product = linkedEntities[i].Value;
                if (product == root
                    || !linkedProducts.Add(product))
                {
                    error =
                        $"Factory GRO {runtimeObject.InstanceId} in store '{runtimeObject.StoreId}' LinkedEntityGroup contains a duplicate product.";
                    return false;
                }
                if (!entityManager.Exists(product)
                    || !entityManager.HasComponent<
                        RuntimeEntityFactoryOwner>(product))
                {
                    error =
                        $"Factory GRO {runtimeObject.InstanceId} in store '{runtimeObject.StoreId}' has a linked product without RuntimeEntityFactoryOwner.";
                    return false;
                }

                var owner = entityManager.GetComponentData<
                    RuntimeEntityFactoryOwner>(product);
                if (owner.FactoryRoot != root
                    || !RuntimeInstancesEqual(
                        owner.FactoryInstance,
                        expectedOwner))
                {
                    error =
                        $"Factory GRO {runtimeObject.InstanceId} in store '{runtimeObject.StoreId}' has a linked product with mismatched ownership.";
                    return false;
                }
            }

            using var ownerQuery = entityManager.CreateEntityQuery(
                ComponentType.ReadOnly<RuntimeEntityFactoryOwner>());
            using var ownedProducts =
                ownerQuery.ToEntityArray(Allocator.Temp);
            for (var i = 0; i < ownedProducts.Length; i++)
            {
                var product = ownedProducts[i];
                var owner = entityManager.GetComponentData<
                    RuntimeEntityFactoryOwner>(product);
                if (owner.FactoryRoot == root
                    && !linkedProducts.Contains(product))
                {
                    error =
                        $"Factory GRO {runtimeObject.InstanceId} in store '{runtimeObject.StoreId}' has an owned product outside its LinkedEntityGroup.";
                    return false;
                }
            }

            return true;
        }

        private static bool RuntimeInstancesEqual(
            in RuntimeInstance left,
            in RuntimeInstance right)
        {
            return left.Id == right.Id
                   && left.StoreId.Equals(right.StoreId)
                   && left.Epoch == right.Epoch;
        }
    }
}
