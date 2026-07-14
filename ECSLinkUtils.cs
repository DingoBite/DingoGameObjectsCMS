using Unity.Entities;

namespace DingoGameObjectsCMS
{
    public static class ECSLinkUtils
    {
        public static EntityCommandBuffer TakeGRCEditingECB(this World world)
        {
            if (world == null || !world.IsCreated)
                throw new System.InvalidOperationException("TakeGRCEditingECB requires a valid ECS World.");

            return world.TakeEndSimulationECB();
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
