#if MIRROR
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror
{
    public static class RuntimeNetTrace
    {
        public static readonly bool ENABLED = true;
        public static readonly bool LOG_MANAGER = true;
        public static readonly bool LOG_COMMANDS = true;
        public static readonly bool LOG_SNAPSHOTS = true;
        public static readonly bool LOG_MUTATIONS = true;
        public static readonly bool LOG_STRUCTURE_MESSAGES = false;
        public static readonly bool LOG_DIRTY = false;

        public static void Server(string area, string message)
        {
            if (!ENABLED)
                return;

            Debug.Log($"[RTNET][S][{area}] f={Time.frameCount} t={Time.unscaledTime:F3} {message}");
        }

        public static void Client(string area, string message)
        {
            if (!ENABLED)
                return;

            Debug.Log($"[RTNET][C][{area}] f={Time.frameCount} t={Time.unscaledTime:F3} {message}");
        }
    }
}
#endif
