using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace DingoGameObjectsCMS.Examples.Crowd.ManagedRoot
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup))]
    public partial class CrowdSimulationSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var transformLookup = GetComponentLookup<LocalTransform>(true);
            var dt = SystemAPI.Time.DeltaTime;
            var elapsedTime = (float)SystemAPI.Time.ElapsedTime;

            foreach (var (controller, ownerEntity) in SystemAPI.Query<CrowdController_GRC>().WithEntityAccess())
            {
                var center = transformLookup.HasComponent(ownerEntity) ? transformLookup[ownerEntity].Position : float3.zero;

                controller.Simulate(center, elapsedTime, dt);
            }
        }
    }
}