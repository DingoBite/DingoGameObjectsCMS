#if MIRROR
using System.Threading.Tasks;
using DingoProjectAppStructure.Core.AppRootCore;
using DingoProjectAppStructure.Core.Model;
using Mirror;
using NaughtyAttributes;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror.Test
{
    public sealed class RuntimeStoreNetSmokeHarness : AppStateElementBehaviour
    {
        private uint _seq;

        public override Task BindAsync(AppModelRoot appModel)
        {
            NetworkClient.RegisterHandler<RtPingMsg>(OnClientPing);

            NetworkServer.RegisterHandler<RtPongMsg>(OnServerPong, requireAuthentication: false);

            return base.BindAsync(appModel);
        }

        [Button]
        public void Ping()
        {
            if (!NetworkServer.active)
                return;

            var msg = new RtPingMsg
            {
                Seq = ++_seq,
                SentAt = Time.realtimeSinceStartupAsDouble
            };

            NetworkServer.SendToReady(msg, Channels.Reliable);
        }

        private void OnClientPing(RtPingMsg msg)
        {
            if (!NetworkClient.isConnected)
                return;

            Debug.Log($"[PING] seq={msg.Seq}");
            NetworkClient.Send(new RtPongMsg
            {
                Seq = msg.Seq,
                SentAt = msg.SentAt
            }, Channels.Reliable);
        }

        private void OnServerPong(NetworkConnectionToClient conn, RtPongMsg msg)
        {
            var rttMs = (Time.realtimeSinceStartupAsDouble - msg.SentAt) * 1000.0;
            Debug.Log($"[PING] conn={conn.connectionId} seq={msg.Seq} rtt={rttMs:0.0} ms");
        }
    }
}
#endif