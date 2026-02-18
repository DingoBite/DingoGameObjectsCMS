#if MIRROR
using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using DingoGameObjectsCMS.RuntimeObjects;

namespace DingoGameObjectsCMS.Mirror
{
    [DisallowMultipleComponent]
    public sealed class DingoNetworkManager : NetworkManager
    {
        public RuntimeStoreNetServer RtServer { get; private set; }
        public RuntimeStoreNetClient RtClient { get; private set; }

        private Func<Hash128, RuntimeStore> _resolver;
        private Func<IEnumerable<Hash128>> _replicatedStoreGetter;

        public override void Awake()
        {
            base.Awake();
            DontDestroyOnLoad(gameObject);
        }

        public void SetStoreResolver(Func<Hash128, RuntimeStore> resolver) => _resolver = resolver;
        public void SetReplicatedStoresGetter(Func<IEnumerable<Hash128>> replicatedStoreGetter) => _replicatedStoreGetter = replicatedStoreGetter;

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (_resolver == null)
                throw new InvalidOperationException($"{nameof(DingoNetworkManager)}: store resolver is not configured.");

            TryUnregisterServerHandlers();
            RtServer = new RuntimeStoreNetServer(_resolver);
        }

        public override void OnStopServer()
        {
            RtServer = null;
            base.OnStopServer();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (_resolver == null)
                throw new InvalidOperationException($"{nameof(DingoNetworkManager)}: store resolver is not configured.");

            TryUnregisterClientHandlers();

            RtClient = new RuntimeStoreNetClient(_resolver);
        }

        public override void OnStopClient()
        {
            RtClient = null;
            base.OnStopClient();
        }

        public override void OnServerReady(NetworkConnectionToClient conn)
        {
            base.OnServerReady(conn);

            if (RtServer == null)
                return;

            foreach (var storeId in _replicatedStoreGetter())
            {
                RtServer.SendFullSnapshot(storeId, conn);
            }
        }
        
        public void ServerBroadcastSpawn(Hash128 storeId, GameRuntimeObject obj, long parentId = -1, int insertIndex = -1, uint clientSeq = 0) => RtServer?.BroadcastSpawn(storeId, obj, parentId, insertIndex, clientSeq);
        public void ServerBroadcastAttach(Hash128 storeId, long parentId, long childId, int insertIndex = -1) => RtServer?.BroadcastAttach(storeId, parentId, childId, insertIndex);
        public void ServerBroadcastMove(Hash128 storeId, long parentId, long childId, int newIndex) => RtServer?.BroadcastMove(storeId, parentId, childId, newIndex);
        public void ServerBroadcastRemove(Hash128 storeId, long id, RemoveMode m = RemoveMode.Subtree) => RtServer?.BroadcastRemove(storeId, id, m);
        
        public void ClientSendMutate(Hash128 storeId, uint seq, long targetId, uint compTypeId, byte[] payload) => RtClient?.SendMutate(storeId, seq, targetId, compTypeId, payload);
        
        private static void TryUnregisterServerHandlers()
        {
            NetworkServer.UnregisterHandler<RtMutateMsg>();
        }

        private static void TryUnregisterClientHandlers()
        {
            NetworkClient.UnregisterHandler<RtSpawnMsg>();
            NetworkClient.UnregisterHandler<RtAttachMsg>();
            NetworkClient.UnregisterHandler<RtMoveMsg>();
            NetworkClient.UnregisterHandler<RtRemoveMsg>();
            NetworkClient.UnregisterHandler<RtAppliedMsg>();
        }
    }
}
#endif