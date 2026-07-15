#if MIRROR
using DingoGameObjectsCMS.Mirror;
using Unity.Multiplayer.PlayMode;
using UnityEditor;

namespace DingoGameObjectsCMS.Editor
{
    public static class NetworkRoleBootstrapMppmTagInstaller
    {
        [InitializeOnLoadMethod]
        private static void Install()
        {
            NetworkRoleBootstrapTagSource.Reader = CurrentPlayer.ReadOnlyTags;
        }
    }
}
#endif
