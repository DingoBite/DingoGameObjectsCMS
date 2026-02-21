#if MIRROR
using Mirror;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror.Test
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
        public FixedString32Bytes Error;
    }

    public struct RtDebugRequestDumpMsg : NetworkMessage
    {
        public FixedString32Bytes Store;
        public int MaxDepth;
    }

    public struct RtDebugDumpMsg : NetworkMessage
    {
        public FixedString32Bytes Store;
        public FixedString32Bytes Dump;
    }
}
#endif