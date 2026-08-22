#if MIRROR
using System;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.Stores;
using Mirror;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror
{
    [DisallowMultipleComponent]
    public class DingoNetworkManager : NetworkManager
    {
        public RuntimeStoreNetServer RtServer { get; private set; }
        public RuntimeStoreNetClient RtClient { get; private set; }
        public RuntimeNetRole RuntimeRole => ResolveRuntimeRole();
        public int LocalConnectionId => RuntimeRole == RuntimeNetRole.Client
            ? RtClient?.AssignedConnectionId ?? -1
            : -1;

        public event Action<RuntimeNetRole> RuntimeRoleChanged;
        public event Action<int, ulong> ProtocolConnectionReady;
        public event Action<int> ProtocolConnectionRemoved;

        private RuntimeProtocolContextFactory _contextFactory;
        private Func<RuntimeCommandsBus> _commandsBusGetter;

        public override void Awake()
        {
            base.Awake();
            DontDestroyOnLoad(gameObject);
            NotifyRuntimeRoleChanged();
        }

        public void SetProtocolContextFactory(RuntimeProtocolContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        }

        public void SetRuntimeCommandsBusGetter(Func<RuntimeCommandsBus> commandsBusGetter)
        {
            _commandsBusGetter = commandsBusGetter ?? throw new ArgumentNullException(nameof(commandsBusGetter));
        }

        public override void OnStartServer()
        {
            base.OnStartServer();
            ReplaceServerEndpoint();
            NotifyRuntimeRoleChanged();
        }

        public override void OnStopServer()
        {
            DisposeServerEndpoint();
            base.OnStopServer();
            NotifyRuntimeRoleChanged();
        }

        public override void OnStartClient()
        {
            base.OnStartClient();
            if (NetworkServer.active)
            {
                NotifyRuntimeRoleChanged();
                return;
            }

            RuntimeExecutionContext.SetReplicaReady(false);
            RuntimeExecutionContext.SetNetworkRole(ResolveRuntimeRole());
            var context = RequireContext(StoreRealm.Client);
            UnregisterClientHandlers();
            RtClient = new RuntimeStoreNetClient(context, CreateClientNonce());
            RtClient.ReplicaReadyChanged += RuntimeExecutionContext.SetReplicaReady;
            NotifyRuntimeRoleChanged();
        }

        public override void OnStopClient()
        {
            if (RtClient != null)
            {
                RtClient.ReplicaReadyChanged -= RuntimeExecutionContext.SetReplicaReady;
                RtClient.Dispose();
                RtClient = null;
            }
            RuntimeExecutionContext.SetReplicaReady(false);
            base.OnStopClient();
            NotifyRuntimeRoleChanged();
        }

        public override void OnStartHost()
        {
            base.OnStartHost();
            NotifyRuntimeRoleChanged();
        }

        public override void OnStopHost()
        {
            base.OnStopHost();
            NotifyRuntimeRoleChanged();
        }

        public override void OnServerConnect(NetworkConnectionToClient connection)
        {
            base.OnServerConnect(connection);
            try
            {
                if (RtServer == null || RtServer.IsSessionInvalidated)
                {
                    ReplaceServerEndpoint();
                    NotifyRuntimeRoleChanged();
                }
                RtServer.OnConnectionConnected(connection.connectionId);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                connection.Disconnect();
            }
        }

        public override void OnServerDisconnect(NetworkConnectionToClient connection)
        {
            RtServer?.OnConnectionDisconnected(connection.connectionId);
            base.OnServerDisconnect(connection);
            NotifyRuntimeRoleChanged();
        }

        public override void OnClientConnect()
        {
            base.OnClientConnect();
            RtClient?.BeginHandshake();
            NotifyRuntimeRoleChanged();
        }

        public override void OnClientDisconnect()
        {
            RuntimeExecutionContext.SetReplicaReady(false);
            base.OnClientDisconnect();
            NotifyRuntimeRoleChanged();
        }

        public override void OnServerReady(NetworkConnectionToClient connection)
        {
            base.OnServerReady(connection);
        }

        public void ClientSendCommand(GameRuntimeCommand command)
        {
            var commandsBus = _commandsBusGetter?.Invoke()
                              ?? throw new InvalidOperationException($"{nameof(DingoNetworkManager)} has no RuntimeCommandsBus.");
            var authority = RuntimeExecutionContext.Current;
            commandsBus.Dispatch(command, in authority);
        }

        public RuntimeInterestRefreshResult RefreshInterest(
            int connectionId,
            in NetStoreRef store)
        {
            return RtServer != null
                ? RtServer.RefreshInterest(connectionId, store)
                : new RuntimeInterestRefreshResult(
                    RuntimeInterestRefreshStatus.NotReady,
                    detail: "Protocol server endpoint is not active.");
        }

        public int RefreshInterest(int connectionId)
        {
            return RtServer?.RefreshInterest(connectionId) ?? 0;
        }

        public int RefreshInterestAll()
        {
            return RtServer?.RefreshInterestAll() ?? 0;
        }

        private RuntimeProtocolContext RequireContext(StoreRealm realm)
        {
            var contextFactory = _contextFactory
                                 ?? throw new InvalidOperationException($"{nameof(DingoNetworkManager)} requires a protocol context factory before networking starts.");
            return contextFactory(realm)
                   ?? throw new InvalidOperationException($"Protocol context factory returned null for realm {realm}.");
        }

        private void ReplaceServerEndpoint()
        {
            var context = RequireContext(StoreRealm.Server);
            DisposeServerEndpoint();
            UnregisterServerHandlers();
            RtServer = new RuntimeStoreNetServer(context);
            RtServer.ConnectionReady += OnProtocolConnectionReady;
            RtServer.ConnectionRemoved += OnProtocolConnectionRemoved;
        }

        private void DisposeServerEndpoint()
        {
            var server = RtServer;
            if (server == null)
                return;

            server.Dispose();
            server.ConnectionReady -= OnProtocolConnectionReady;
            server.ConnectionRemoved -= OnProtocolConnectionRemoved;
            RtServer = null;
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
            RuntimeExecutionContext.SetNetworkRole(role);
            RuntimeRoleChanged?.Invoke(role);
        }

        private static ulong CreateClientNonce()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            var value = BitConverter.ToUInt64(bytes, 0);
            return value == 0 ? 1UL : value;
        }

        private void OnProtocolConnectionReady(int connectionId, ulong sessionId)
        {
            ProtocolConnectionReady?.Invoke(connectionId, sessionId);
        }

        private void OnProtocolConnectionRemoved(int connectionId)
        {
            ProtocolConnectionRemoved?.Invoke(connectionId);
        }

        private static void UnregisterServerHandlers()
        {
            NetworkServer.UnregisterHandler<RtSessionHello>();
            NetworkServer.UnregisterHandler<RtSessionReady>();
            NetworkServer.UnregisterHandler<RtStoreAck>();
            NetworkServer.UnregisterHandler<RtStoreResyncRequest>();
            NetworkServer.UnregisterHandler<RtCommandJournalResyncRequest>();
            NetworkServer.UnregisterHandler<RtCommandEnvelope>();
        }

        private static void UnregisterClientHandlers()
        {
            NetworkClient.UnregisterHandler<RtSessionManifest>();
            NetworkClient.UnregisterHandler<RtProtocolReject>();
            NetworkClient.UnregisterHandler<RtBaselineChunk>();
            NetworkClient.UnregisterHandler<RtCheckpointChunk>();
            NetworkClient.UnregisterHandler<RtStoreDelta>();
            NetworkClient.UnregisterHandler<RtCommandResult>();
            NetworkClient.UnregisterHandler<RtStateStreamFrame>();
            NetworkClient.UnregisterHandler<RtCommandJournalBatch>();
        }
    }
}
#endif
