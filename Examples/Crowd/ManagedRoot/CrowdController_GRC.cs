using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using Unity.Mathematics;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Crowd.ManagedRoot
{
    [Serializable, Preserve]
    public sealed class CrowdController_GRC : GameRuntimeComponent<CrowdController_GRC>
    {
        [Serializable]
        public struct CrowdAgentState
        {
            public int Index;
            public float AngleOffset;
            public float VerticalPhase;
            public float3 Position;
            public float3 Velocity;
        }

        public int CrowdCount = 8;
        public float Radius = 4f;
        public float AngularSpeed = 1.5f;
        public float VerticalAmplitude = 0.35f;
        public float VerticalFrequency = 2f;

        [NonSerialized] private List<CrowdAgentState> _agents = new();

        public IReadOnlyList<CrowdAgentState> Agents => _agents;

        public override void SetupForEntity(RuntimeObjects.Stores.RuntimeStore store, Unity.Entities.EntityCommandBuffer ecb, RuntimeObjects.Objects.GameRuntimeObject g, Unity.Entities.Entity e)
        {
            base.SetupForEntity(store, ecb, g, e);
            RebuildCrowd();
        }

        public override void AddForEntity(RuntimeObjects.Stores.RuntimeStore store, Unity.Entities.EntityCommandBuffer ecb, RuntimeObjects.Objects.GameRuntimeObject g, Unity.Entities.Entity e)
        {
            base.AddForEntity(store, ecb, g, e);
            RebuildCrowd();
        }

        public override void DestroyForRuntime(RuntimeObjects.Stores.RuntimeStore store, Unity.Entities.EntityCommandBuffer ecb, RuntimeObjects.Objects.GameRuntimeObject g, Unity.Entities.Entity e)
        {
            _agents?.Clear();
            base.DestroyForRuntime(store, ecb, g, e);
        }

        public void Simulate(float3 center, float elapsedTime, float dt)
        {
            _agents ??= new List<CrowdAgentState>();
            for (var i = 0; i < _agents.Count; i++)
            {
                var agent = _agents[i];
                var angle = elapsedTime * AngularSpeed + agent.AngleOffset;
                var verticalTime = elapsedTime * VerticalFrequency;
                var position = EvaluatePosition(center, Radius, angle, VerticalAmplitude, agent.VerticalPhase, verticalTime);
                agent.Velocity = dt > 0f ? (position - agent.Position) / dt : float3.zero;
                agent.Position = position;
                _agents[i] = agent;
            }
        }

        private void RebuildCrowd()
        {
            _agents ??= new List<CrowdAgentState>();
            _agents.Clear();

            var count = math.max(0, CrowdCount);
            for (var i = 0; i < count; i++)
            {
                var angleOffset = count > 0 ? math.PI * 2f * i / count : 0f;
                _agents.Add(new CrowdAgentState
                {
                    Index = i,
                    AngleOffset = angleOffset,
                    VerticalPhase = angleOffset * 0.5f,
                    Position = float3.zero,
                    Velocity = float3.zero,
                });
            }
        }

        private static float3 EvaluatePosition(float3 center, float radius, float angle, float verticalAmplitude, float verticalPhase, float time)
        {
            var offset = new float3(math.cos(angle), 0f, math.sin(angle)) * math.max(0f, radius);
            offset.y = math.sin(time + verticalPhase) * math.max(0f, verticalAmplitude);
            return center + offset;
        }
    }
}