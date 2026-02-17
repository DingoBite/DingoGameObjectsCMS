#if MIRROR
using System.Collections.Generic;
using DingoGameObjectsCMS.Mirror.Test;
using Mirror;
using DingoGameObjectsCMS.RuntimeObjects;
using SnakeAndMice.GameComponents.AppGlue;

namespace DingoGameObjectsCMS.MirrorDebug
{
    public sealed class RuntimeStoreNetDebugServer
    {
        private readonly RuntimeStores _stores;
        private readonly Dictionary<int, RtDebugHashMsg> _lastHashByConn = new();

        public RuntimeStoreNetDebugServer(RuntimeStores stores)
        {
            _stores = stores;

            NetworkServer.RegisterHandler<RtDebugHashMsg>(OnHash, requireAuthentication: false);
            NetworkServer.RegisterHandler<RtDebugDumpMsg>(OnDump, requireAuthentication: false);
        }

        private RuntimeStore Get(string key) => _stores.GetOrAddRuntimeStore(key);

        public void RequestCompareAllReady(string storeKey)
        {
            NetworkServer.SendToReady(new RtDebugRequestHashMsg { Store = storeKey }, Channels.Reliable);
        }

        public void RequestDumpFromAllReady(string storeKey, int maxDepth = 64)
        {
            NetworkServer.SendToReady(new RtDebugRequestDumpMsg { Store = storeKey, MaxDepth = maxDepth }, Channels.Reliable);
        }

        private void OnHash(NetworkConnectionToClient conn, RtDebugHashMsg msg)
        {
            _lastHashByConn[conn.connectionId] = msg;

            var store = Get(msg.Store);

            var okS = RuntimeStoreValidator.Validate(store, out var errS);
            var hashS = RuntimeStoreStructureHasher.ComputeHash(store);

            if (!msg.Valid || !okS || msg.Hash != hashS)
            {
                UnityEngine.Debug.LogError(
                    $"[RT-DEBUG] MISMATCH store={msg.Store} conn={conn.connectionId}\n" +
                    $" client: valid={msg.Valid} hash={msg.Hash} err={msg.Error}\n" +
                    $" server: valid={okS} hash={hashS} err={errS}"
                );
            }
            else
            {
                UnityEngine.Debug.Log($"[RT-DEBUG] OK store={msg.Store} conn={conn.connectionId} hash={hashS}");
            }
        }

        private void OnDump(NetworkConnectionToClient conn, RtDebugDumpMsg msg)
        {
            UnityEngine.Debug.Log($"[RT-DEBUG] DUMP store={msg.Store} conn={conn.connectionId}\n{msg.Dump}");
        }
    }
}
#endif
