using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Stores;
using Unity.Entities;

namespace DingoGameObjectsCMS.Systems
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(RuntimeInstanceLinkerSystem))]
    public partial class RuntimeHierarchyProjectionSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            FlushRealm(StoreRealm.Server);
            FlushRealm(StoreRealm.Client);
        }

        private void FlushRealm(StoreRealm realm)
        {
            foreach (var store in RuntimeStores.EnumerateStores(realm))
            {
                store.PruneDestroyedEntities(EntityManager);
                store.FlushEntityHierarchy(EntityManager);
            }
        }
    }
}
