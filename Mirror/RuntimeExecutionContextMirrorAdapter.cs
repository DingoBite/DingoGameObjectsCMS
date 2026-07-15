#if MIRROR
using System;
using DingoGameObjectsCMS.Mirror;

namespace DingoGameObjectsCMS.Stores
{
    public static partial class RuntimeExecutionContext
    {
        public static void SetNetworkRole(RuntimeNetRole role)
        {
            SetRole(role switch
            {
                RuntimeNetRole.Offline => RuntimeExecutionRole.OfflineAuthoritative,
                RuntimeNetRole.Server => RuntimeExecutionRole.ServerAuthoritative,
                RuntimeNetRole.Host => RuntimeExecutionRole.HostAuthoritative,
                RuntimeNetRole.Client => RuntimeExecutionRole.ClientReplica,
                _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown runtime network role."),
            });
        }
    }
}
#endif
