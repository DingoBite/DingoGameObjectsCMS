using System;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Crowd.HybridFactory
{
    [Serializable, Preserve]
    public struct CrowdOrbit : IComponentData
    {
        public float Radius;
        public float AngularSpeed;
        public float VerticalAmplitude;
        public float VerticalFrequency;
        public float Phase;
    }

    [Serializable, Preserve]
    public struct CrowdVelocity : IComponentData
    {
        public Unity.Mathematics.float3 Value;
    }
}
