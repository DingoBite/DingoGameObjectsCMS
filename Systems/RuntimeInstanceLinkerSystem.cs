using DingoGameObjectsCMS;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Stores;
using Unity.Entities;

namespace DingoGameObjectsCMS.Systems
{
    [UpdateInGroup(typeof(FixedStepSimulationSystemGroup), OrderFirst = true)]
    [UpdateAfter(typeof(DestroyStaleRuntimeEntitiesSystem))]
    public partial class RuntimeInstanceLinkerSystem : SystemBase
    {
        private EntityQuery _runtimeEntityQuery;
        private int _lastEntityOrderVersion;
        private ulong _lastStoreLifecycleRevision;
        private bool _hasReconciled;

        protected override void OnCreate()
        {
            _runtimeEntityQuery = GetEntityQuery(
                ComponentType.ReadOnly<RuntimeInstance>(),
                ComponentType.ReadOnly<RuntimeRealm>());
        }

        protected override void OnUpdate()
        {
            var entityOrderVersion =
                _runtimeEntityQuery.GetCombinedComponentOrderVersion(
                    includeEntityType: true);
            var storeLifecycleRevision =
                RuntimeStores.LifecycleRevision;
            if (_hasReconciled
                && entityOrderVersion == _lastEntityOrderVersion
                && storeLifecycleRevision
                == _lastStoreLifecycleRevision)
            {
                return;
            }

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

                    if (store.IsEntityPendingDestroy(entity))
                        continue;

                    store.LinkEntity(instance.ValueRO.Id, entity);
                }
            }
            finally
            {
                EndLinkPass(StoreRealm.Server);
                EndLinkPass(StoreRealm.Client);
            }

            _lastEntityOrderVersion =
                _runtimeEntityQuery.GetCombinedComponentOrderVersion(
                    includeEntityType: true);
            _lastStoreLifecycleRevision =
                RuntimeStores.LifecycleRevision;
            _hasReconciled = true;
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
