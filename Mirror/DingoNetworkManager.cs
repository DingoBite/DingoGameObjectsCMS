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
        /// <summary>Keep new connections unobserving until gameplay calls AddPlayerForConnection.</summary>
        public bool DeferPlayerObservation { get; set; }

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
        private bool _runtimeSuspended;

        public override void Awake()
        {
            base.Awake();
            DontDestroyOnLoad(gameObject);
            NotifyRuntimeRoleChanged();
        }

        public void SetProtocolContextFactory(RuntimeProtocolContextFactory contextFactory)
        {
            _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
            Trace("Protocol context factory configured.");
        }

        public void SetRuntimeCommandsBusGetter(Func<RuntimeCommandsBus> commandsBusGetter)
        {
            _commandsBusGetter = commandsBusGetter ?? throw new ArgumentNullException(nameof(commandsBusGetter));
            Trace("RuntimeCommandsBus getter configured.");
        }

        public override void OnStartServer()
        {
            _runtimeSuspended = false;
            Trace($"OnStartServer begin. server={NetworkServer.active}, client={NetworkClient.active}.");
            base.OnStartServer();
            ReplaceServerEndpoint();
            NotifyRuntimeRoleChanged();
            Trace($"OnStartServer complete. role={RuntimeRole}, endpoint={RtServer != null}.");
        }

        public override void OnStopServer()
        {
            Trace($"OnStopServer begin. endpoint={RtServer != null}.");
            DisposeServerEndpoint();
            base.OnStopServer();
            NotifyRuntimeRoleChanged();
            Trace($"OnStopServer complete. role={RuntimeRole}.");
        }

        public override void OnStartClient()
        {
            _runtimeSuspended = false;
            Trace($"OnStartClient begin. server={NetworkServer.active}, client={NetworkClient.active}.");
            base.OnStartClient();
            if (NetworkServer.active)
            {
                NotifyRuntimeRoleChanged();
                Trace("OnStartClient host-local branch; replica endpoint is not created.");
                return;
            }

            ReplaceClientEndpoint();
            NotifyRuntimeRoleChanged();
            Trace($"OnStartClient complete. role={RuntimeRole}, endpoint={RtClient != null}.");
        }

        public override void OnStopClient()
        {
            Trace($"OnStopClient begin. endpoint={RtClient != null}, connected={NetworkClient.isConnected}.");
            DisposeClientEndpoint();
            RuntimeExecutionContext.SetReplicaReady(false);
            base.OnStopClient();
            NotifyRuntimeRoleChanged();
            Trace($"OnStopClient complete. role={RuntimeRole}.");
        }

        public override void OnStartHost()
        {
            base.OnStartHost();
            NotifyRuntimeRoleChanged();
            Trace($"OnStartHost. role={RuntimeRole}.");
        }

        public override void OnStopHost()
        {
            Trace("OnStopHost begin.");
            base.OnStopHost();
            NotifyRuntimeRoleChanged();
            Trace($"OnStopHost complete. role={RuntimeRole}.");
        }

        public override void OnServerConnect(NetworkConnectionToClient connection)
        {
            Trace($"OnServerConnect begin. connection={connection?.connectionId ?? -1}, authenticated={connection?.isAuthenticated ?? false}.");
            base.OnServerConnect(connection);
            if (_runtimeSuspended)
                return;
            try
            {
                if (RtServer == null || RtServer.IsSessionInvalidated)
                {
                    ReplaceServerEndpoint();
                    NotifyRuntimeRoleChanged();
                }
                RtServer.OnConnectionConnected(connection.connectionId);
                Trace($"OnServerConnect registered protocol connection={connection.connectionId}.");
            }
            catch (Exception exception)
            {
                Trace($"OnServerConnect failed for connection={connection?.connectionId ?? -1}: {exception.GetType().Name}: {exception.Message}");
                Debug.LogException(exception);
                connection.Disconnect();
            }
        }

        public override void OnServerDisconnect(NetworkConnectionToClient connection)
        {
            Trace($"OnServerDisconnect. connection={connection?.connectionId ?? -1}.");
            RtServer?.OnConnectionDisconnected(connection.connectionId);
            base.OnServerDisconnect(connection);
            NotifyRuntimeRoleChanged();
        }

        public override void OnClientConnect()
        {
            Trace($"OnClientConnect begin. connectionPresent={NetworkClient.connection != null}, authenticated={NetworkClient.connection?.isAuthenticated ?? false}.");
            base.OnClientConnect();
            RtClient?.BeginHandshake();
            NotifyRuntimeRoleChanged();
            Trace($"OnClientConnect complete. handshakeStarted={RtClient != null}, role={RuntimeRole}.");
        }

        public override void OnClientDisconnect()
        {
            Trace($"OnClientDisconnect. connectionPresent={NetworkClient.connection != null}, replicaReady={RtClient?.IsReplicaReady == true}.");
            RuntimeExecutionContext.SetReplicaReady(false);
            base.OnClientDisconnect();
            NotifyRuntimeRoleChanged();
        }

        public override void OnServerReady(NetworkConnectionToClient connection)
        {
            if (!DeferPlayerObservation || connection.identity != null)
            {
                base.OnServerReady(connection);
            }
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

        /// <summary>Retire the runtime protocol while retaining authenticated transport connections.</summary>
        public void SuspendRuntime()
        {
            _runtimeSuspended = true;
            DisposeClientEndpoint();
            DisposeServerEndpoint();
            RuntimeExecutionContext.SetReplicaReady(false);
            // Reliable packets already sent by the retiring runtime may still arrive.
            // Consume them without admitting commands into the replacement runtime.
            if (NetworkServer.active)
            {
                NetworkServer.RegisterHandler<RtSessionHello>((_, _) => { });
                NetworkServer.RegisterHandler<RtSessionReady>((_, _) => { });
                NetworkServer.RegisterHandler<RtStoreAck>((_, _) => { });
                NetworkServer.RegisterHandler<RtStoreResyncRequest>((_, _) => { });
                NetworkServer.RegisterHandler<RtCommandJournalResyncRequest>((_, _) => { });
                NetworkServer.RegisterHandler<RtCommandEnvelope>((_, _) => { });
            }
            if (NetworkClient.active && !NetworkServer.active)
            {
                NetworkClient.RegisterHandler<RtSessionManifest>(_ => { });
                NetworkClient.RegisterHandler<RtProtocolReject>(_ => { });
                NetworkClient.RegisterHandler<RtBaselineChunk>(_ => { });
                NetworkClient.RegisterHandler<RtCheckpointChunk>(_ => { });
                NetworkClient.RegisterHandler<RtStoreDelta>(_ => { });
                NetworkClient.RegisterHandler<RtCommandResult>(_ => { });
                NetworkClient.RegisterHandler<RtStateStreamFrame>(_ => { });
                NetworkClient.RegisterHandler<RtCommandJournalBatch>(_ => { });
            }
        }

        /// <summary>Start a fresh protocol handshake and baseline on the retained connections.</summary>
        public void ResumeRuntime()
        {
            if (!_runtimeSuspended)
                throw new InvalidOperationException("Suspend the runtime before replacing its protocol endpoints.");

            _runtimeSuspended = false;
            if (NetworkServer.active)
            {
                ReplaceServerEndpoint();
                foreach (var connection in NetworkServer.connections.Values)
                {
                    if (connection.isAuthenticated)
                        RtServer.OnConnectionConnected(connection.connectionId);
                }
            }
            else if (NetworkClient.active && NetworkClient.isConnected)
            {
                ReplaceClientEndpoint();
                RtClient.BeginHandshake();
            }
            NotifyRuntimeRoleChanged();
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
            Trace("Replacing RuntimeStore server endpoint.");
            var context = RequireContext(StoreRealm.Server);
            DisposeServerEndpoint();
            UnregisterServerHandlers();
            RtServer = new RuntimeStoreNetServer(context);
            RtServer.ConnectionReady += OnProtocolConnectionReady;
            RtServer.ConnectionRemoved += OnProtocolConnectionRemoved;
            Trace("RuntimeStore server endpoint created.");
        }

        private void DisposeServerEndpoint()
        {
            var server = RtServer;
            if (server == null)
                return;

            Trace("Disposing RuntimeStore server endpoint.");
            server.Dispose();
            server.ConnectionReady -= OnProtocolConnectionReady;
            server.ConnectionRemoved -= OnProtocolConnectionRemoved;
            RtServer = null;
        }

        private void ReplaceClientEndpoint()
        {
            var context = RequireContext(StoreRealm.Client);
            DisposeClientEndpoint();
            UnregisterClientHandlers();
            RuntimeExecutionContext.SetReplicaReady(false);
            RuntimeExecutionContext.SetNetworkRole(ResolveRuntimeRole());
            RtClient = new RuntimeStoreNetClient(context, CreateClientNonce());
            RtClient.ReplicaReadyChanged += RuntimeExecutionContext.SetReplicaReady;
        }

        private void DisposeClientEndpoint()
        {
            if (RtClient == null)
                return;

            RtClient.ReplicaReadyChanged -= RuntimeExecutionContext.SetReplicaReady;
            RtClient.Dispose();
            RtClient = null;
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
            Trace($"Runtime role published: {role}; server={NetworkServer.active}, client={NetworkClient.active}, connected={NetworkClient.isConnected}.");
        }

        private static ulong CreateClientNonce()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            var value = BitConverter.ToUInt64(bytes, 0);
            return value == 0 ? 1UL : value;
        }

        private void OnProtocolConnectionReady(int connectionId, ulong sessionId)
        {
            Trace($"Protocol connection ready. connection={connectionId}, session={sessionId}.");
            ProtocolConnectionReady?.Invoke(connectionId, sessionId);
        }

        private void OnProtocolConnectionRemoved(int connectionId)
        {
            Trace($"Protocol connection removed. connection={connectionId}.");
            ProtocolConnectionRemoved?.Invoke(connectionId);
        }

        private void Trace(string message)
        {
            Debug.Log(
                $"[NETTRACE][MirrorManager][t={Time.realtimeSinceStartupAsDouble:F3}] {message}",
                this);
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
