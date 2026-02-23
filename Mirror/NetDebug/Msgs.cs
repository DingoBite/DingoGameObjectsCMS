#if MIRROR
using Mirror;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror.NetDebug
{
    public struct RtDebugRequestHashMsg : NetworkMessage
    {
        public FixedString32Bytes Store;
    }

    public struct RtDebugHashMsg : NetworkMessage
    {
        public FixedString32Bytes Store;
        public ulong Hash;
        public bool Valid;
        public string Error;
    }

    public struct RtDebugRequestDumpMsg : NetworkMessage
    {
        public FixedString32Bytes Store;
        public int MaxDepth;
    }

    public struct RtDebugDumpMsg : NetworkMessage
    {
        public FixedString32Bytes Store;
        public string Dump;
    }
    
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
