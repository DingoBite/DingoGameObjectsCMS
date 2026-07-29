using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DingoGameObjectsCMS.Examples.Crowd.HybridFactory
{
    [BurstCompile]
    public partial struct CrowdOrbitJob : IJobEntity
    {
        public float DeltaTime;
        public float ElapsedTime;

        private void Execute(ref LocalTransform transform, ref CrowdVelocity velocity, in CrowdOrbit orbit)
        {
            var angle = ElapsedTime * orbit.AngularSpeed + orbit.Phase;
            var verticalTime = ElapsedTime * orbit.VerticalFrequency;
            var position = EvaluatePosition(orbit.Radius, orbit.VerticalAmplitude, angle, orbit.Phase * 0.5f, verticalTime);
            var forward = math.normalizesafe(new float3(-math.sin(angle), 0f, math.cos(angle)), new float3(0f, 0f, 1f));

            velocity.Value = DeltaTime > 0f ? (position - transform.Position) / DeltaTime : float3.zero;
            transform = LocalTransform.FromPositionRotationScale(position, quaternion.LookRotationSafe(forward, math.up()), 1f);
        }

        private static float3 EvaluatePosition(float radius, float verticalAmplitude, float angle, float verticalPhase, float time)
        {
            var position = new float3(math.cos(angle), 0f, math.sin(angle)) * radius;
            position.y = math.sin(time + verticalPhase) * verticalAmplitude;
            return position;
        }
    }

    [BurstCompile]
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial struct CrowdSimulationSystem : ISystem
    {
        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            state.Dependency = new CrowdOrbitJob
            {
                DeltaTime = SystemAPI.Time.DeltaTime,
                ElapsedTime = (float)SystemAPI.Time.ElapsedTime,
            }.ScheduleParallel(state.Dependency);
        }
    }
}
