using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Crowd.DOTSCommandPattern
{
    [Preserve]
    public struct CrowdAgentTag : IComponentData { }

    [Preserve]
    public struct CrowdAgentState : IComponentData
    {
        public int Index;
        public float AngleOffset;
        public float VerticalPhase;
    }

    [Preserve]
    public struct CrowdOwnerEntity : IComponentData
    {
        public Entity Value;
    }

    [Preserve]
    public struct CrowdOwnerLink : IComponentData
    {
        public RuntimeInstance Owner;
        public StoreRealm Realm;
    }

    [Preserve]
    public struct CrowdVelocity : IComponentData
    {
        public float3 Value;
    }

    [Preserve]
    public struct CrowdEnsureRequest : IComponentData
    {
        public Entity OwnerEntity;
        public RuntimeInstance OwnerRuntime;
        public StoreRealm Realm;
    }

    [Preserve]
    public struct CrowdDestroyRequest : IComponentData
    {
        public RuntimeInstance OwnerRuntime;
        public StoreRealm Realm;
    }
}