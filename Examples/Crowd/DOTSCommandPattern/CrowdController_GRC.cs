using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Crowd.DOTSCommandPattern
{
    [Serializable, Preserve]
    public sealed class CrowdController_GRC : GameRuntimeComponent<CrowdController_GRC>
    {
        public int CrowdCount = 8;
        public float Radius = 4f;
        public float AngularSpeed = 1.5f;
        public float VerticalAmplitude = 0.35f;
        public float VerticalFrequency = 2f;

        public override void SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            base.SetupForEntity(store, ecb, g, e);
            EnqueueEnsureCrowd(ecb, g.RuntimeInstance, g.Realm, e);
        }

        public override void AddForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            base.AddForEntity(store, ecb, g, e);
            EnqueueEnsureCrowd(ecb, g.RuntimeInstance, g.Realm, e);
        }

        public override void DestroyForRuntime(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            EnqueueDestroyCrowd(ecb, g.RuntimeInstance, g.Realm);
            base.DestroyForRuntime(store, ecb, g, e);
        }

        private static void EnqueueEnsureCrowd(EntityCommandBuffer ecb, RuntimeInstance ownerRuntime, StoreRealm realm, Entity ownerEntity)
        {
            var request = ecb.CreateEntity();
            ecb.AddComponent(request, new CrowdEnsureRequest
            {
                OwnerEntity = ownerEntity,
                OwnerRuntime = ownerRuntime,
                Realm = realm,
            });
        }

        private static void EnqueueDestroyCrowd(EntityCommandBuffer ecb, RuntimeInstance ownerRuntime, StoreRealm realm)
        {
            var request = ecb.CreateEntity();
            ecb.AddComponent(request, new CrowdDestroyRequest
            {
                OwnerRuntime = ownerRuntime,
                Realm = realm,
            });
        }
    }
}