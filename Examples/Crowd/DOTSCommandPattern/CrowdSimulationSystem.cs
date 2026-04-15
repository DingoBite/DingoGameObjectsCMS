using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DingoGameObjectsCMS.Examples.Crowd.DOTSCommandPattern
{

    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    [UpdateAfter(typeof(CrowdLifecycleSystem))]
    public partial class CrowdSimulationSystem : SystemBase
    {
        private EntityQuery _ownersQuery;

        protected override void OnCreate()
        {
            _ownersQuery = GetEntityQuery(
                ComponentType.ReadOnly<CrowdController_GRC>(),
                ComponentType.ReadOnly<RuntimeInstance>(),
                ComponentType.ReadOnly<RuntimeRealm>());
        }

        protected override void OnUpdate()
        {
            var ownerCount = math.max(1, _ownersQuery.CalculateEntityCount());
            var transformLookup = GetComponentLookup<LocalTransform>(true);
            var dt = SystemAPI.Time.DeltaTime;
            var elapsedTime = (float)SystemAPI.Time.ElapsedTime;

            using var ownerConfigs = new NativeParallelHashMap<Entity, CrowdOwnerConfig>(ownerCount, Allocator.Temp);

            foreach (var (controller, runtime, realm, ownerEntity) in SystemAPI
                         .Query<CrowdController_GRC, RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>()
                         .WithEntityAccess())
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

            foreach (var (ownerEntity, agentState, velocity, transform) in SystemAPI
                         .Query<RefRO<CrowdOwnerEntity>, RefRO<CrowdAgentState>, RefRW<CrowdVelocity>, RefRW<LocalTransform>>())
            {
                if (!ownerConfigs.TryGetValue(ownerEntity.ValueRO.Value, out var config))
                    continue;

                var angle = elapsedTime * config.AngularSpeed + agentState.ValueRO.AngleOffset;
                var verticalTime = elapsedTime * config.VerticalFrequency;
                var position = EvaluatePosition(config.Center, config.Radius, angle, config.VerticalAmplitude, agentState.ValueRO.VerticalPhase, verticalTime);
                var forward = math.normalizesafe(new float3(-math.sin(angle), 0f, math.cos(angle)), new float3(0f, 0f, 1f));
                var previous = transform.ValueRO.Position;

                velocity.ValueRW.Value = dt > 0f ? (position - previous) / dt : float3.zero;
                transform.ValueRW = LocalTransform.FromPositionRotationScale(position, quaternion.LookRotationSafe(forward, math.up()), 1f);
            }
        }

        private static float3 EvaluatePosition(float3 center, float radius, float angle, float verticalAmplitude, float verticalPhase, float time)
        {
            var offset = new float3(math.cos(angle), 0f, math.sin(angle)) * radius;
            offset.y = math.sin(time + verticalPhase) * verticalAmplitude;
            return center + offset;
        }
    }
}

