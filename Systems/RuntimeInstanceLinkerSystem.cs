using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Entities;

namespace DingoGameObjectsCMS.Systems
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    public partial class RuntimeInstanceLinkerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            foreach (var (instance, realm, entity) in SystemAPI
                         .Query<RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>()
                         .WithEntityAccess()
                         .WithChangeFilter<RuntimeInstance>())
            {
                var store = instance.ValueRO.StoreId.ResolveStore(realm.ValueRO.Realm);
                store?.LinkEntity(instance.ValueRO.Id, entity);
            }
        }
    }
}