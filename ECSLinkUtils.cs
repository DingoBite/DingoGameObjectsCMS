using System.Collections.Generic;
using Unity.Entities;
using UnityEngine;

namespace DingoGameObjectsCMS
{
    public static class ECSLinkUtils
    {
        private sealed class GrcEditingEcbState
        {
            public int Frame = -1;
            public int Token;
            public EntityCommandBuffer Buffer;
        }

        private static readonly Dictionary<World, GrcEditingEcbState> _grcEditingBuffers = new();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetGrcEditingBuffers()
        {
            _grcEditingBuffers.Clear();
        }

        public static EntityCommandBuffer TakeGRCEditingECB(this World world) => world.TakeGRCEditingECB(out _);

        public static EntityCommandBuffer TakeGRCEditingECB(this World world, out int token)
        {
            if (world == null || !world.IsCreated)
                throw new System.InvalidOperationException("TakeGRCEditingECB requires a valid ECS World.");

            if (!_grcEditingBuffers.TryGetValue(world, out var state))
            {
                state = new GrcEditingEcbState();
                _grcEditingBuffers[world] = state;
            }

            var frame = Time.frameCount;
            if (state.Frame != frame)
            {
                state.Frame = frame;
                state.Token++;
                state.Buffer = world.TakeEndSimulationECB();
            }

            token = state.Token;
            return state.Buffer;
        }

        public static EntityCommandBuffer TakeEndSimulationECB(this World world)
        {
            var ecbSys = world.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
            var ecb = ecbSys.CreateCommandBuffer();
            return ecb;
        }

        public static EntityCommandBuffer TakeBeginSimulationECB(this World world)
        {
            var ecbSys = world.GetOrCreateSystemManaged<BeginSimulationEntityCommandBufferSystem>();
            var ecb = ecbSys.CreateCommandBuffer();
            return ecb;
        }
    }
}