using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Crowd.HybridFactory
{
    [Serializable, Preserve, RuntimeComponentKey("dingo.examples.crowd.hybrid-factory")]
    public class CrowdFactory_GRC : GameRuntimeEntityFactoryComponent
    {
        public int Count = 1000;
        public float Radius = 4f;
        public float AngularSpeed = 1.5f;
        public float VerticalAmplitude = 0.35f;
        public float VerticalFrequency = 2f;

        public override void SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            var entityManager = store.World.EntityManager;
            var archetype = entityManager.CreateArchetype(
                ComponentType.ReadWrite<RuntimeEntityFactoryOwner>(),
                ComponentType.ReadWrite<CrowdOrbit>(),
                ComponentType.ReadWrite<CrowdVelocity>(),
                ComponentType.ReadWrite<LocalTransform>());

            var count = math.max(0, Count);
            var radius = math.max(0f, Radius);
            var verticalAmplitude = math.max(0f, VerticalAmplitude);
            var verticalFrequency = math.max(0f, VerticalFrequency);

            for (var i = 0; i < count; i++)
            {
                var phase = count > 0 ? math.PI * 2f * i / count : 0f;
                var product = ecb.CreateOwnedEntity(e, g.RuntimeInstance, archetype);
                ecb.SetComponent(product, new CrowdOrbit
                {
                    Radius = radius,
                    AngularSpeed = AngularSpeed,
                    VerticalAmplitude = verticalAmplitude,
                    VerticalFrequency = verticalFrequency,
                    Phase = phase,
                });
                ecb.SetComponent(product, new CrowdVelocity());
                ecb.SetComponent(product, LocalTransform.FromPosition(EvaluatePosition(radius, verticalAmplitude, phase, phase * 0.5f, 0f)));
            }
        }

        private static float3 EvaluatePosition(float radius, float verticalAmplitude, float angle, float verticalPhase, float time)
        {
            var position = new float3(math.cos(angle), 0f, math.sin(angle)) * radius;
            position.y = math.sin(time + verticalPhase) * verticalAmplitude;
            return position;
        }
    }
}
