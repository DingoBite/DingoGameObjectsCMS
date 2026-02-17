#if MIRROR
using Mirror;

namespace DingoGameObjectsCMS.Mirror.Test
{
    public struct RtPingMsg : NetworkMessage
    {
        public uint Seq;
        public double SentAt;
    }

    public struct RtPongMsg : NetworkMessage
    {
        public uint Seq;
        public double SentAt;
    }
}
#endif
