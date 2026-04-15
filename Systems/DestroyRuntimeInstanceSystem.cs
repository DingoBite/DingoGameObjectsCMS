using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Stores;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.Systems
{
    public struct DestroyRuntimeInstanceRequest : IComponentData
    {
        public RuntimeInstance Instance;
        public StoreRealm Realm;
    }
    
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    public partial class DestroyRuntimeInstanceSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            using var ecb = new EntityCommandBuffer(Allocator.Temp);
            
            foreach (var (r, e) in SystemAPI.Query<RefRO<DestroyRuntimeInstanceRequest>>().WithEntityAccess())
            {
                var store = r.ValueRO.Instance.ResolveStore(r.ValueRO.Realm);
                if (store != null && store.Remove(r.ValueRO.Instance.Id, ecb, out var instanceE))
                {
                    ecb.DestroyEntity(instanceE);
                    if (instanceE != e)
                        ecb.DestroyEntity(e);
                }
                else
                {
                    ecb.DestroyEntity(e);
                }
            }
            
            ecb.Playback(EntityManager);
        }
    }
}
