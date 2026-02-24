#if MIRROR
using System;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;

namespace DingoGameObjectsCMS.Mirror
{
    public enum RuntimeNetRole : byte
    {
        Offline = 0,
        Server = 1,
        Client = 2,
        Host = 3,
    }

    [DisallowMultipleComponent]
    public sealed class DingoNetworkManager : NetworkManager
    {
        public RuntimeStoreNetServer RtServer { get; private set; }
        public RuntimeStoreNetClient RtClient { get; private set; }

        public RuntimeNetRole RuntimeRole => ResolveRuntimeRole();
        public event Action<RuntimeNetRole> RuntimeRoleChanged;

        private Func<FixedString32Bytes, RuntimeStore> _serverResolver;
        private Func<FixedString32Bytes, RuntimeStore> _clientResolver;

        private Func<IEnumerable<FixedString32Bytes>> _replicatedStoreGetter;
        private Func<RuntimeCommandsBus> _commandsBusGetter;

        public override void Awake()
        {
            base.Awake();
            DontDestroyOnLoad(gameObject);

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", $"awake address={networkAddress}");

            NotifyRuntimeRoleChanged();
        }
        
        public void SetServerStoreResolver(Func<FixedString32Bytes, RuntimeStore> resolver) => _serverResolver = resolver;
        public void SetClientStoreResolver(Func<FixedString32Bytes, RuntimeStore> resolver) => _clientResolver = resolver;

        public void SetReplicatedStoresGetter(Func<IEnumerable<FixedString32Bytes>> replicatedStoreGetter) => _replicatedStoreGetter = replicatedStoreGetter;
        public void SetRuntimeCommandsBusGetter(Func<RuntimeCommandsBus> commandsBusGetter) => _commandsBusGetter = commandsBusGetter;

        public override void OnStartServer()
        {
            base.OnStartServer();

            if (_serverResolver == null)
                throw new InvalidOperationException($"{nameof(DingoNetworkManager)}: server store resolver is not configured.");

            TryUnregisterServerHandlers();
            RtServer = new RuntimeStoreNetServer(_serverResolver, _replicatedStoreGetter, _commandsBusGetter?.Invoke());
            Debug.Log($"Server started: {networkAddress}");

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", $"OnStartServer address={networkAddress}");

            NotifyRuntimeRoleChanged();
        }

        public override void OnStopServer()
        {
            RtServer = null;
            base.OnStopServer();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", "OnStopServer");

            NotifyRuntimeRoleChanged();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();

            if (_clientResolver == null)
                throw new InvalidOperationException($"{nameof(DingoNetworkManager)}: client store resolver is not configured.");

            TryUnregisterClientHandlers();
            RtClient = new RuntimeStoreNetClient(_clientResolver, _commandsBusGetter?.Invoke());
            Debug.Log($"Client started: {networkAddress}");

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Client("MANAGER", $"OnStartClient address={networkAddress}");

            NotifyRuntimeRoleChanged();
        }

        public override void OnStopClient()
        {
            RtClient = null;
            base.OnStopClient();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Client("MANAGER", "OnStopClient");

            NotifyRuntimeRoleChanged();
        }

        public override void OnStartHost()
        {
            base.OnStartHost();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", "OnStartHost");

            NotifyRuntimeRoleChanged();
        }

        public override void OnStopHost()
        {
            base.OnStopHost();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", "OnStopHost");

            NotifyRuntimeRoleChanged();
        }

        public override void OnClientConnect()
        {
            base.OnClientConnect();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Client("MANAGER", "OnClientConnect");

            NotifyRuntimeRoleChanged();
        }

        public override void OnClientDisconnect()
        {
            base.OnClientDisconnect();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Client("MANAGER", "OnClientDisconnect");

            NotifyRuntimeRoleChanged();
        }

        public override void OnServerReady(NetworkConnectionToClient conn)
        {
            base.OnServerReady(conn);

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", $"OnServerReady conn={conn.connectionId} auth={conn.isAuthenticated}");

            if (RtServer == null)
                return;

            var stores = _replicatedStoreGetter?.Invoke();
            if (stores == null)
                return;

            foreach (var storeId in stores)
                RtServer.SendFullSnapshot(storeId, conn);
        }

        public override void OnServerDisconnect(NetworkConnectionToClient conn)
        {
            RtServer?.OnConnectionDisconnected(conn.connectionId);

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", $"OnServerDisconnect conn={conn.connectionId}");

            base.OnServerDisconnect(conn);
            NotifyRuntimeRoleChanged();
        }

        public void ServerBroadcastCommand(GameRuntimeCommand command, uint tick = 0, int sender = -1)
        {
            RtServer?.BroadcastCommand(command, tick, sender);

            if (RuntimeNetTrace.LOG_COMMANDS && command != null)
                RuntimeNetTrace.Server("CMD", $"broadcast wrapper store={command.StoreId} tick={tick} sender={sender}");
        }

        public void ClientSendCommand(GameRuntimeCommand command, uint tick = 0)
        {
            RtClient?.SendCommand(command, tick);

            if (RuntimeNetTrace.LOG_COMMANDS && command != null)
                RuntimeNetTrace.Client("CMD", $"send wrapper command store={command.StoreId} tick={tick}");
        }

        private static void TryUnregisterServerHandlers()
        {
            NetworkServer.UnregisterHandler<RtCommandMsg>();
            NetworkServer.UnregisterHandler<RtStoreAckMsg>();
            NetworkServer.UnregisterHandler<RtStoreResyncRequestMsg>();
        }

        private static void TryUnregisterClientHandlers()
        {
            NetworkClient.UnregisterHandler<RtCommandMsg>();
            NetworkClient.UnregisterHandler<RtStoreSyncMsg>();
        }

        private RuntimeNetRole ResolveRuntimeRole()
        {
            if (NetworkServer.active && NetworkClient.active)
                return RuntimeNetRole.Host;

            if (NetworkServer.active)
                return RuntimeNetRole.Server;

            if (NetworkClient.active)
                return RuntimeNetRole.Client;

            return RuntimeNetRole.Offline;
        }

        private void NotifyRuntimeRoleChanged()
        {
            var role = ResolveRuntimeRole();

            if (RuntimeNetTrace.LOG_MANAGER)
                RuntimeNetTrace.Server("MANAGER", $"role changed role={role}");

            RuntimeRoleChanged?.Invoke(role);
        }
    }
}
#endif