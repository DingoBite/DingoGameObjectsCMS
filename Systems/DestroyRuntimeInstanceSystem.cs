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
    [UpdateAfter(typeof(RuntimeHierarchyProjectionSystem))]
    public partial class DestroyRuntimeInstanceSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            using var ecb = new EntityCommandBuffer(Allocator.Temp);
            var destroyStateLookup = SystemAPI.GetComponentLookup<RuntimeEntityDestroyState>(isReadOnly: false);
            
            foreach (var (r, e) in SystemAPI.Query<RefRO<DestroyRuntimeInstanceRequest>>().WithEntityAccess())
            {
                if (destroyStateLookup.HasComponent(e) && destroyStateLookup[e].Pending != 0)
                    continue;

                var store = r.ValueRO.Instance.ResolveStore(r.ValueRO.Realm);
                if (store != null && store.Remove(r.ValueRO.Instance.Id, ecb, out var instanceE))
                {
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
