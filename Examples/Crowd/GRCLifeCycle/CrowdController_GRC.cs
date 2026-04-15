using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Crowd.GRCLifeCycle
{
    [Serializable, Preserve]
    public sealed class CrowdController_GRC : GameRuntimeComponent<CrowdController_GRC>
    {
        public int CrowdCount = 8;
        public float Radius = 4f;
        public float AngularSpeed = 1.5f;
        public float VerticalAmplitude = 0.35f;
        public float VerticalFrequency = 2f;

        public override void SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            base.SetupForEntity(store, ecb, g, e);
            RebuildCrowd(store, ecb, g.RuntimeInstance, g.Realm, e);
        }

        public override void AddForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            base.AddForEntity(store, ecb, g, e);
            RebuildCrowd(store, ecb, g.RuntimeInstance, g.Realm, e);
        }

        public override void DestroyForRuntime(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            DestroyCrowd(store, ecb, g.RuntimeInstance, g.Realm);
            base.DestroyForRuntime(store, ecb, g, e);
        }

        private void RebuildCrowd(RuntimeStore store, EntityCommandBuffer ecb, RuntimeInstance ownerRuntime, StoreRealm realm, Entity ownerEntity)
        {
            DestroyCrowd(store, ecb, ownerRuntime, realm);
            SpawnCrowd(store, ecb, ownerRuntime, realm, ownerEntity);
        }

        private void DestroyCrowd(RuntimeStore store, EntityCommandBuffer ecb, RuntimeInstance ownerRuntime, StoreRealm realm)
        {
            var em = store.World.EntityManager;
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<CrowdAgentTag>(), ComponentType.ReadOnly<CrowdOwnerLink>());
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<CrowdOwnerLink>(Allocator.Temp);

            for (var i = 0; i < entities.Length; i++)
            {
                if (!MatchesOwner(owners[i], ownerRuntime, realm))
                    continue;

                ecb.DestroyEntity(entities[i]);
            }
        }

        private void SpawnCrowd(RuntimeStore store, EntityCommandBuffer ecb, RuntimeInstance ownerRuntime, StoreRealm realm, Entity ownerEntity)
        {
            var center = TryResolveOwnerPosition(store, ownerEntity);
            var count = math.max(0, CrowdCount);
            for (var i = 0; i < count; i++)
            {
                var angleOffset = count > 0 ? math.PI * 2f * i / count : 0f;
                var verticalPhase = angleOffset * 0.5f;
                var position = EvaluatePosition(center, Radius, angleOffset, VerticalAmplitude, verticalPhase, 0f);
                var entity = ecb.CreateEntity();

                ecb.AddComponent(entity, new CrowdAgentTag());
                ecb.AddComponent(entity, new CrowdOwnerEntity { Value = ownerEntity });
                ecb.AddComponent(entity, new CrowdOwnerLink { Owner = ownerRuntime, Realm = realm });
                ecb.AddComponent(entity, new CrowdAgentState
                {
                    Index = i,
                    AngleOffset = angleOffset,
                    VerticalPhase = verticalPhase,
                });
                ecb.AddComponent(entity, new CrowdVelocity { Value = float3.zero });
                ecb.AddComponent(entity, LocalTransform.FromPosition(position));
            }
        }

        private static bool MatchesOwner(in CrowdOwnerLink ownerLink, in RuntimeInstance ownerRuntime, StoreRealm realm)
        {
            return ownerLink.Realm == realm && ownerLink.Owner.Id == ownerRuntime.Id && ownerLink.Owner.Epoch == ownerRuntime.Epoch && ownerLink.Owner.StoreId.Equals(ownerRuntime.StoreId);
        }

        private static float3 TryResolveOwnerPosition(RuntimeStore store, Entity ownerEntity)
        {
            var em = store.World.EntityManager;
            if (!em.Exists(ownerEntity) || !em.HasComponent<LocalTransform>(ownerEntity))
                return float3.zero;

            return em.GetComponentData<LocalTransform>(ownerEntity).Position;
        }

        private static float3 EvaluatePosition(float3 center, float radius, float angle, float verticalAmplitude, float verticalPhase, float time)
        {
            var offset = new float3(math.cos(angle), 0f, math.sin(angle)) * math.max(0f, radius);
            offset.y = math.sin(time + verticalPhase) * math.max(0f, verticalAmplitude);
            return center + offset;
        }
    }
}