using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Stores;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DingoGameObjectsCMS.Examples.Crowd.Managed
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial class CrowdSimulationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var transformLookup = GetComponentLookup<LocalTransform>(true);
            var dt = SystemAPI.Time.DeltaTime;
            var elapsedTime = (float)SystemAPI.Time.ElapsedTime;

            foreach (var (agent, transform, runtime, realm) in SystemAPI.Query<CrowdAgent_GRC, RefRW<LocalTransform>, RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>())
            {
                if (!runtime.ValueRO.TryResolveActiveStore(realm.ValueRO.Realm, out var store))
                    continue;

                if (!store.TryTakeParentRO(runtime.ValueRO.Id, out var parent))
                    continue;

                var controller = parent.TakeRO<CrowdController_GRC>();
                if (controller == null)
                    continue;

                var center = float3.zero;
                if (store.TryGetEntity(parent.InstanceId, out var parentEntity) && transformLookup.HasComponent(parentEntity))
                    center = transformLookup[parentEntity].Position;

                var angle = elapsedTime * controller.AngularSpeed + agent.AngleOffset;
                var verticalTime = elapsedTime * controller.VerticalFrequency;
                var position = EvaluatePosition(center, controller.Radius, angle, controller.VerticalAmplitude, agent.VerticalPhase, verticalTime);
                agent.Velocity = dt > 0f ? (position - agent.Position) / dt : float3.zero;
                agent.Position = position;
                transform.ValueRW = LocalTransform.FromPosition(position);
            }
        }

        private static float3 EvaluatePosition(float3 center, float radius, float angle, float verticalAmplitude, float verticalPhase, float time)
        {
            var offset = new float3(math.cos(angle), 0f, math.sin(angle)) * math.max(0f, radius);
            offset.y = math.sin(time + verticalPhase) * math.max(0f, verticalAmplitude);
            return center + offset;
        }
    }
}