#if MIRROR
using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror
{
    [DisallowMultipleComponent]
    public sealed class DingoNetworkManager : NetworkManager
    {
        public RuntimeStoreNetServer RtServer { get; private set; }
        public RuntimeStoreNetClient RtClient { get; private set; }

        private Func<FixedString32Bytes, RuntimeStore> _resolver;
        private Func<IEnumerable<FixedString32Bytes>> _replicatedStoreGetter;
        private Func<RuntimeCommandsBus> _commandsBusGetter;

        public override void Awake()
        {
            base.Awake();
            DontDestroyOnLoad(gameObject);
        }

        public void SetStoreResolver(Func<FixedString32Bytes, RuntimeStore> resolver) => _resolver = resolver;
        public void SetReplicatedStoresGetter(Func<IEnumerable<FixedString32Bytes>> replicatedStoreGetter) => _replicatedStoreGetter = replicatedStoreGetter;
        public void SetRuntimeCommandsBusGetter(Func<RuntimeCommandsBus> commandsBusGetter) => _commandsBusGetter = commandsBusGetter;

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (_resolver == null)
                throw new InvalidOperationException($"{nameof(DingoNetworkManager)}: store resolver is not configured.");

            TryUnregisterServerHandlers();
            RtServer = new RuntimeStoreNetServer(_resolver, _replicatedStoreGetter, _commandsBusGetter?.Invoke());
            Debug.Log($"Server started: {networkAddress}");
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

            RtClient = new RuntimeStoreNetClient(_resolver, _commandsBusGetter?.Invoke());
            Debug.Log($"Client started: {networkAddress}");
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

            var stores = _replicatedStoreGetter?.Invoke();
            if (stores == null)
                return;

            foreach (var storeId in stores)
            {
                RtServer.SendFullSnapshot(storeId, conn);
            }
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            RtServer?.OnConnectionDisconnected(conn.connectionId);
            base.OnServerDisconnect(conn);
        }
        
        public void ServerBroadcastSpawn(FixedString32Bytes storeId, GameRuntimeObject obj, long parentId = -1, int insertIndex = -1, uint clientSeq = 0) => RtServer?.BroadcastSpawn(storeId, obj, parentId, insertIndex, clientSeq);
        public void ServerBroadcastAttach(FixedString32Bytes storeId, long parentId, long childId, int insertIndex = -1) => RtServer?.BroadcastAttach(storeId, parentId, childId, insertIndex);
        public void ServerBroadcastMove(FixedString32Bytes storeId, long parentId, long childId, int newIndex) => RtServer?.BroadcastMove(storeId, parentId, childId, newIndex);
        public void ServerBroadcastRemove(FixedString32Bytes storeId, long id, RemoveMode m = RemoveMode.Subtree) => RtServer?.BroadcastRemove(storeId, id, m);
        public void ServerBroadcastCommand(GameRuntimeCommand command, uint tick = 0, int sender = -1) => RtServer?.BroadcastCommand(command, tick, sender);
        
        public void ClientSendMutate(FixedString32Bytes storeId, uint seq, long targetId, uint compTypeId, byte[] payload) => RtClient?.SendMutate(storeId, seq, targetId, compTypeId, payload);
        public void ClientSendCommand(GameRuntimeCommand command, uint tick = 0) => RtClient?.SendCommand(command, tick);
        
        private static void TryUnregisterServerHandlers()
        {
            NetworkServer.UnregisterHandler<RtMutateMsg>();
            NetworkServer.UnregisterHandler<RtCommandMsg>();
            NetworkServer.UnregisterHandler<RtStoreAckMsg>();
            NetworkServer.UnregisterHandler<RtStoreResyncRequestMsg>();
        }

        private static void TryUnregisterClientHandlers()
        {
            NetworkClient.UnregisterHandler<RtSpawnMsg>();
            NetworkClient.UnregisterHandler<RtAttachMsg>();
            NetworkClient.UnregisterHandler<RtMoveMsg>();
            NetworkClient.UnregisterHandler<RtRemoveMsg>();
            NetworkClient.UnregisterHandler<RtAppliedMsg>();
            NetworkClient.UnregisterHandler<RtCommandMsg>();
            NetworkClient.UnregisterHandler<RtStoreFullSnapshotMsg>();
            NetworkClient.UnregisterHandler<RtStoreDeltaMsg>();
        }
    }
}
#endif
