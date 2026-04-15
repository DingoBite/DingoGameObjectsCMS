using DingoGameObjectsCMS;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Stores;
using Unity.Entities;

namespace DingoGameObjectsCMS.Systems
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderLast = true)]
    public partial class RuntimeInstanceLinkerSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            BeginLinkPass(StoreRealm.Server);
            BeginLinkPass(StoreRealm.Client);

            try
            {
                foreach (var (instance, realm, entity) in SystemAPI
                             .Query<RefRO<RuntimeInstance>, RefRO<RuntimeRealm>>()
                             .WithEntityAccess())
                {
                    if (!instance.ValueRO.TryResolveActiveStore(realm.ValueRO.Realm, out var store))
                        continue;

                    store.LinkEntity(instance.ValueRO.Id, entity);
                }
            }
            finally
            {
                EndLinkPass(StoreRealm.Server);
                EndLinkPass(StoreRealm.Client);
            }
        }

        private static void BeginLinkPass(StoreRealm realm)
        {
            foreach (var store in RuntimeStores.EnumerateStores(realm))
            {
                store.BeginEntityLinkPass();
            }
        }

        private static void EndLinkPass(StoreRealm realm)
        {
            foreach (var store in RuntimeStores.EnumerateStores(realm))
            {
                store.EndEntityLinkPass();
            }
        }
    }
}
