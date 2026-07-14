using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Stores;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.Systems
{
    public struct DestroyThisEntityRequest : IComponentData
    {
    }
    
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(RuntimeHierarchyProjectionSystem))]
    public partial class DestroyThisEntitySystem : SystemBase
    {
        protected override void OnUpdate()
        {
            using var ecb = new EntityCommandBuffer(Allocator.Temp);
            var destroyStateLookup = SystemAPI.GetComponentLookup<RuntimeEntityDestroyState>(isReadOnly: false);
            
            foreach (var (instance, realm, entity) in SystemAPI
                         .Query<RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>()
                         .WithAll<DestroyThisEntityRequest>()
                         .WithEntityAccess())
            {
                if (destroyStateLookup.HasComponent(entity) && destroyStateLookup[entity].Pending != 0)
                    continue;

                var store = instance.ValueRO.ResolveStore(realm.ValueRO.Realm);
                if (store == null || !store.Remove(instance.ValueRO.Id, ecb))
                    ecb.DestroyEntity(entity);
            }
            
            ecb.Playback(EntityManager);
        }
    }
}
