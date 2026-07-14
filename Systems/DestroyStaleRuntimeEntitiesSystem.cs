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
            var destroyStateLookup = SystemAPI.GetComponentLookup<RuntimeEntityDestroyState>(isReadOnly: true);

            foreach (var (instance, realm, entity) in SystemAPI
                         .Query<RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>()
                         .WithEntityAccess())
            {
                if (destroyStateLookup.HasComponent(entity) && destroyStateLookup[entity].Pending != 0)
                    continue;

                if (instance.ValueRO.TryResolveActiveStore(realm.ValueRO.Realm, out var store)
                    && store.IsEntityPendingDestroy(entity))
                    continue;

                if (instance.ValueRO.TryResolveActiveStore(realm.ValueRO.Realm, out store)
                    && store.TryTakeRO(instance.ValueRO.Id, out _))
                {
                    if (!store.TryGetEntity(instance.ValueRO.Id, out var linkedEntity) || linkedEntity == entity)
                        continue;
                }

                ecb.DestroyEntity(entity);
                hasAny = true;
            }

            if (hasAny)
                ecb.Playback(EntityManager);
        }
    }
}
