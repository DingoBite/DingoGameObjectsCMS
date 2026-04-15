using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Crowd.Managed
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
            RebuildCrowd(store, g.InstanceId);
        }

        public override void AddForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            base.AddForEntity(store, ecb, g, e);
            RebuildCrowd(store, g.InstanceId);
        }

        public override void DestroyForRuntime(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            DestroyCrowd(store, g.InstanceId);
            base.DestroyForRuntime(store, ecb, g, e);
        }

        private void RebuildCrowd(RuntimeStore store, long ownerId)
        {
            DestroyCrowd(store, ownerId);

            var count = math.max(0, CrowdCount);
            for (var i = 0; i < count; i++)
            {
                var angleOffset = count > 0 ? math.PI * 2f * i / count : 0f;
                var child = store.CreateChild(ownerId);
                child.AddOrReplace(new CrowdAgent_GRC
                {
                    Index = i,
                    AngleOffset = angleOffset,
                    VerticalPhase = angleOffset * 0.5f,
                    Position = float3.zero,
                    Velocity = float3.zero,
                });
                child.CreateEntity();
            }
        }

        private static void DestroyCrowd(RuntimeStore store, long ownerId)
        {
            if (!store.TryTakeChildren(ownerId, out var children))
                return;

            var ids = new List<long>(children);
            foreach (var childId in ids)
            {
                store.Remove(childId);
            }
        }
    }

    [Serializable, Preserve]
    public sealed class CrowdAgent_GRC : GameRuntimeComponent<CrowdAgent_GRC>
    {
        public int Index;
        public float AngleOffset;
        public float VerticalPhase;
        public float3 Position;
        public float3 Velocity;

        public override void SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            ecb.AddComponent(e, LocalTransform.FromPosition(Position));
        }

        public override void AddForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            if (store.World.EntityManager.HasComponent<LocalTransform>(e))
                ecb.SetComponent(e, LocalTransform.FromPosition(Position));
            else
                ecb.AddComponent(e, LocalTransform.FromPosition(Position));
        }
    }
}