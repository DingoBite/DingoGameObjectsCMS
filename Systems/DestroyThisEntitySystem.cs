using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.Systems
{
    public struct DestroyThisEntityRequest : IComponentData
    {
    }
    
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    public partial class DestroyThisEntitySystem : SystemBase
    {
        protected override void OnUpdate()
        {
            using var ecb = new EntityCommandBuffer(Allocator.Temp);
            
            foreach (var (instance, realm, entity) in SystemAPI
                         .Query<RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>()
                         .WithAll<DestroyThisEntityRequest>()
                         .WithEntityAccess())
            {
                ecb.DestroyEntity(entity);
                var store = instance.ValueRO.StoreId.ResolveStore(realm.ValueRO.Realm);
                store?.Remove(instance.ValueRO.Id);
            }
            
            ecb.Playback(EntityManager);
        }
    }
}