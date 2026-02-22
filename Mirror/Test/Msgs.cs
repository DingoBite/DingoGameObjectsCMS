#if MIRROR
using Mirror;

namespace DingoGameObjectsCMS.Mirror.Test
{
    public struct RtDebugRequestHashMsg : NetworkMessage
    {
        public string Store;
    }

    public struct RtDebugHashMsg : NetworkMessage
    {
        public string Store;
        public ulong Hash;
        public bool Valid;
        public string Error;
    }

    public struct RtDebugRequestDumpMsg : NetworkMessage
    {
        public string Store;
        public int MaxDepth;
    }

    public struct RtDebugDumpMsg : NetworkMessage
    {
        public string Store;
        public string Dump;
    }
}
#endif
