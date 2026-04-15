using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Stores;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.Systems
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderFirst = true)]
    public partial class DestroyStaleRuntimeEntitiesSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            using var ecb = new EntityCommandBuffer(Allocator.Temp);
            var hasAny = false;

            foreach (var (instance, realm, entity) in SystemAPI
                         .Query<RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>()
                         .WithEntityAccess())
            {
                if (instance.ValueRO.TryResolveActiveStore(realm.ValueRO.Realm, out _))
                    continue;

                ecb.DestroyEntity(entity);
                hasAny = true;
            }

            if (hasAny)
                ecb.Playback(EntityManager);
        }
    }
}
