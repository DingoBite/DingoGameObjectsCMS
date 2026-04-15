using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Systems;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DingoGameObjectsCMS.Examples.Crowd.DOTSCommandPattern
{
    internal struct CrowdOwnerConfig
    {
        public RuntimeInstance RuntimeInstance;
        public StoreRealm Realm;
        public int CrowdCount;
        public float Radius;
        public float AngularSpeed;
        public float VerticalAmplitude;
        public float VerticalFrequency;
        public float3 Center;
    }

    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(DestroyStaleRuntimeEntitiesSystem))]
    public partial class CrowdLifecycleSystem : SystemBase
    {
        private EntityQuery _ownersQuery;

        protected override void OnCreate()
        {
            _ownersQuery = GetEntityQuery(ComponentType.ReadOnly<CrowdController_GRC>(), ComponentType.ReadOnly<RuntimeInstance>(), ComponentType.ReadOnly<RuntimeRealm>());
        }

        protected override void OnUpdate()
        {
            var ownerCount = math.max(1, _ownersQuery.CalculateEntityCount());
            var transformLookup = GetComponentLookup<LocalTransform>(true);

            using var ownerConfigs = new NativeParallelHashMap<Entity, CrowdOwnerConfig>(ownerCount, Allocator.Temp);
            using var ensureRequests = new NativeParallelHashMap<Entity, CrowdEnsureRequest>(ownerCount, Allocator.Temp);
            using var destroyRequests = new NativeList<CrowdDestroyRequest>(Allocator.Temp);
            using var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (controller, runtime, realm, ownerEntity) in SystemAPI.Query<CrowdController_GRC, RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>().WithEntityAccess())
            {
                ownerConfigs.TryAdd(ownerEntity, new CrowdOwnerConfig
                {
                    RuntimeInstance = runtime.ValueRO,
                    Realm = realm.ValueRO.Realm,
                    CrowdCount = math.max(0, controller.CrowdCount),
                    Radius = math.max(0f, controller.Radius),
                    AngularSpeed = controller.AngularSpeed,
                    VerticalAmplitude = math.max(0f, controller.VerticalAmplitude),
                    VerticalFrequency = math.max(0f, controller.VerticalFrequency),
                    Center = transformLookup.HasComponent(ownerEntity) ? transformLookup[ownerEntity].Position : float3.zero,
                });
            }

            foreach (var (request, requestEntity) in SystemAPI.Query<RefRO<CrowdEnsureRequest>>().WithEntityAccess())
            {
                ensureRequests.Remove(request.ValueRO.OwnerEntity);
                ensureRequests.TryAdd(request.ValueRO.OwnerEntity, request.ValueRO);
                ecb.DestroyEntity(requestEntity);
            }

            foreach (var (request, requestEntity) in SystemAPI.Query<RefRO<CrowdDestroyRequest>>().WithEntityAccess())
            {
                destroyRequests.Add(request.ValueRO);
                ecb.DestroyEntity(requestEntity);
            }

            foreach (var (ownerLink, ownerEntity, agentEntity) in SystemAPI.Query<RefRO<CrowdOwnerLink>, RefRO<CrowdOwnerEntity>>().WithEntityAccess())
            {
                if (ensureRequests.ContainsKey(ownerEntity.ValueRO.Value) || HasDestroyRequest(ownerLink.ValueRO, destroyRequests))
                    ecb.DestroyEntity(agentEntity);
            }

            foreach (var requestPair in ensureRequests)
            {
                var request = requestPair.Value;
                if (!ownerConfigs.TryGetValue(request.OwnerEntity, out var config))
                    continue;

                if (!SameRuntime(config.RuntimeInstance, request.OwnerRuntime) || config.Realm != request.Realm)
                    continue;

                for (var i = 0; i < config.CrowdCount; i++)
                    SpawnAgent(ecb, request.OwnerEntity, config, i);
            }

            ecb.Playback(EntityManager);
        }

        private static bool HasDestroyRequest(in CrowdOwnerLink ownerLink, NativeList<CrowdDestroyRequest> requests)
        {
            foreach (var request in requests)
            {
                if (request.Realm != ownerLink.Realm)
                    continue;

                if (SameRuntime(ownerLink.Owner, request.OwnerRuntime))
                    return true;
            }

            return false;
        }

        private static bool SameRuntime(in RuntimeInstance a, in RuntimeInstance b)
        {
            return a.Id == b.Id && a.Epoch == b.Epoch && a.StoreId.Equals(b.StoreId);
        }

        private static void SpawnAgent(EntityCommandBuffer ecb, Entity ownerEntity, in CrowdOwnerConfig config, int index)
        {
            var entity = ecb.CreateEntity();
            var angleOffset = config.CrowdCount > 0 ? math.PI * 2f * index / config.CrowdCount : 0f;
            var verticalPhase = angleOffset * 0.5f;
            var position = EvaluatePosition(config.Center, config.Radius, angleOffset, config.VerticalAmplitude, verticalPhase, 0f);

            ecb.AddComponent(entity, new CrowdAgentTag());
            ecb.AddComponent(entity, new CrowdOwnerEntity { Value = ownerEntity });
            ecb.AddComponent(entity, new CrowdOwnerLink { Owner = config.RuntimeInstance, Realm = config.Realm });
            ecb.AddComponent(entity, new CrowdAgentState
            {
                Index = index,
                AngleOffset = angleOffset,
                VerticalPhase = verticalPhase,
            });
            ecb.AddComponent(entity, new CrowdVelocity { Value = float3.zero });
            ecb.AddComponent(entity, LocalTransform.FromPosition(position));
        }

        private static float3 EvaluatePosition(float3 center, float radius, float angle, float verticalAmplitude, float verticalPhase, float time)
        {
            var offset = new float3(math.cos(angle), 0f, math.sin(angle)) * radius;
            offset.y = math.sin(time + verticalPhase) * verticalAmplitude;
            return center + offset;
        }
    }
}