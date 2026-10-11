#if UNITY_EDITOR && MIRROR
using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Mirror;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using Mirror;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class DingoNetworkManagerRuntimeLifecycleTests
    {
        private GameObject _networkRoot;
        private GameObject _ownedCoroutineParent;
        private DingoNetworkManager _manager;
        private DingoNetworkManagerLifecycleTestTransport _transport;
        private Transport _previousTransport;
        private RuntimeExecutionState _previousExecutionState;
        private bool _previousServerListen;
        private bool _ownsGlobals;
        private bool _serverStarted;
        private bool _clientStarted;
        private World _world;
        private GameAssetTemplateCache _templates;
        private RuntimeSessionDescriptor _descriptor;

        [SetUp]
        public void SetUp()
        {
            Assert.That(NetworkServer.active, Is.False, "Lifecycle tests require an idle Mirror server.");
            Assert.That(NetworkClient.active, Is.False, "Lifecycle tests require an idle Mirror client.");
            Assert.That(NetworkServer.connections, Is.Empty, "Lifecycle tests must not replace retained transport connections.");
            Assert.That(RuntimeStores.ServerStores.V, Is.Empty, "Lifecycle tests require no live authoritative stores.");
            Assert.That(RuntimeStores.ClientStores.V, Is.Empty, "Lifecycle tests require no live replica stores.");
            _previousTransport = Transport.active;
            _previousExecutionState = RuntimeExecutionContext.Current;
            _previousServerListen = NetworkServer.listen;
            _ownsGlobals = true;
            RuntimeStores.ResetState();
            RuntimeExecutionContext.ResetState();
            if (CoroutineParent.GetNoCheck() == null)
            {
                _ownedCoroutineParent = new GameObject(nameof(DingoNetworkManagerRuntimeLifecycleTests));
                _ownedCoroutineParent.AddComponent<CoroutineParent>();
            }

            _world = new World(nameof(DingoNetworkManagerRuntimeLifecycleTests));
            RuntimeStores.SetupWorld(_world);
            _networkRoot = new GameObject(nameof(DingoNetworkManagerRuntimeLifecycleTests));
            _networkRoot.SetActive(false);
            _transport = _networkRoot.AddComponent<DingoNetworkManagerLifecycleTestTransport>();
            _manager = _networkRoot.AddComponent<DingoNetworkManager>();
            _manager.transport = _transport;
            _manager.autoCreatePlayer = false;
            Transport.active = _transport;
            var assets = new GameAssetLibraryLock();
            assets.Seal();
            var catalog = RuntimeSessionAssetCatalog.FromLock(assets);
            var codecs = new RuntimePatchCodecRegistry("manager-lifecycle-tests");
            _templates = new GameAssetTemplateCache(codecs, RuntimeTemplatePatchCodecContext.Instance);
            var policies = new RuntimeReplicationPolicyRegistry();
            policies.Seal(Array.Empty<uint>());
            var streams = RuntimeStateStreamProfileRegistry.CreateEmptySealed();
            _descriptor = new RuntimeSessionDescriptor { ProtocolVersion = RuntimeProtocol.VERSION, BuildId = "manager-lifecycle-tests", RuntimeSchemaHash = codecs.SchemaHash, AssetCatalogHash = RuntimeSessionCatalogHasher.CalculateAssets(catalog.ManifestEntries), ResourceCatalogHash = "resources", StateStreamCatalogHash = streams.CatalogHash };
            var manifest = new RuntimeSessionManifestTemplate(_descriptor, catalog.ManifestEntries, Array.Empty<RuntimeStoreCatalogEntry>());
            var context = new RuntimeProtocolContext(manifest, catalog, assets, _templates, codecs, policies, _world, streams);
            _manager.SetProtocolContextFactory(_ => context);
        }

        [TearDown]
        public void TearDown()
        {
            if (!_ownsGlobals)
                return;

            try
            {
                if (_manager != null)
                {
                    if (_manager.RtClient != null)
                        _manager.OnStopClient();
                    if (_manager.RtServer != null)
                        _manager.OnStopServer();
                }
            }
            finally
            {
                try
                {
                    if (_clientStarted)
                    {
                        NetworkClient.Disconnect();
                        NetworkClient.Shutdown();
                    }
                    if (_serverStarted)
                        NetworkServer.Shutdown();
                }
                finally
                {
                    RuntimeStores.ResetState();
                    RuntimeExecutionContext.SetRole(_previousExecutionState.StableRole);
                    RuntimeExecutionContext.SetReplicaReady(_previousExecutionState.IsReplicaReady);
                    _templates?.Clear();
                    if (_world != null && _world.IsCreated)
                        _world.Dispose();
                    if (_networkRoot != null)
                        Object.DestroyImmediate(_networkRoot);
                    if (_ownedCoroutineParent != null)
                        Object.DestroyImmediate(_ownedCoroutineParent);
                    Transport.active = _previousTransport;
                    NetworkServer.listen = _previousServerListen;
                    _ownsGlobals = false;
                    _serverStarted = false;
                    _clientStarted = false;
                }
            }
        }

        [Test]
        public void SuspendConsumesLateHelloAndResumeStartsFreshHandshakeOnRetainedConnection()
        {
            StartServer();
            var connection = AddConnection(21);
            var oldEndpoint = _manager.RtServer;
            var removed = new List<int>();
            var ready = new List<int>();
            Action<int> onRemoved = removed.Add;
            Action<int, ulong> onReady = (id, _) => ready.Add(id);
            _manager.ProtocolConnectionRemoved += onRemoved;
            _manager.ProtocolConnectionReady += onReady;
            try
            {
                CompleteHandshake(connection, 23);
                Assert.That(ready, Is.EqualTo(new[] { connection.connectionId }));

                _manager.SuspendRuntime();

                Assert.That(_manager.RtServer, Is.Null);
                Assert.That(removed, Is.EqualTo(new[] { connection.connectionId }));
                ReceiveServer(connection, new RtSessionHello { Descriptor = _descriptor, ClientNonce = 24 });
                Assert.That(connection.DrainManifests(), Is.Empty, "Suspended Hello must be consumed without admitting a new handshake.");
                Assert.That(ready.Count, Is.EqualTo(1));
                Assert.That(NetworkServer.connections[connection.connectionId], Is.SameAs(connection));
                Assert.That(connection.DisconnectCount, Is.Zero);

                _manager.ResumeRuntime();

                Assert.That(_manager.RtServer, Is.Not.Null);
                Assert.That(_manager.RtServer, Is.Not.SameAs(oldEndpoint));
                CompleteHandshake(connection, 25);
                Assert.That(ready, Is.EqualTo(new[] { connection.connectionId, connection.connectionId }));
                Assert.That(_manager.RtServer.GetReadyConnections().Count, Is.EqualTo(1));
                Assert.That(connection.DisconnectCount, Is.Zero);
            }
            finally
            {
                _manager.ProtocolConnectionRemoved -= onRemoved;
                _manager.ProtocolConnectionReady -= onReady;
            }
        }

        [Test]
        public void ProtocolRemovalResumePreservesReplacementEndpointHandlersAndExactNotificationsAcrossCycles()
        {
            StartServer();
            var first = AddConnection(21);
            var second = AddConnection(22);
            var oldEndpoint = _manager.RtServer;
            var removed = new List<int>();
            var oldRemoved = new List<int>();
            var ready = new List<int>();
            var resumeRequested = true;
            var detachedBeforeResume = false;
            RuntimeStoreNetServer replacement = null;
            Action<int> onOldRemoved = oldRemoved.Add;
            Action<int, ulong> onReady = (id, _) => ready.Add(id);
            Action<int> onRemoved = id =>
            {
                removed.Add(id);
                if (!resumeRequested)
                    return;
                resumeRequested = false;
                detachedBeforeResume = _manager.RtServer == null;
                _manager.ResumeRuntime();
                replacement = _manager.RtServer;
            };
            oldEndpoint.ConnectionRemoved += onOldRemoved;
            _manager.ProtocolConnectionRemoved += onRemoved;
            _manager.ProtocolConnectionReady += onReady;
            try
            {
                CompleteHandshake(first, 23);
                CompleteHandshake(second, 24);

                _manager.SuspendRuntime();

                Assert.That(resumeRequested, Is.False, "The public removal callback must remain forwarded during endpoint retirement.");
                Assert.That(detachedBeforeResume, Is.True);
                Assert.That(removed, Is.EquivalentTo(new[] { first.connectionId, second.connectionId }));
                Assert.That(oldRemoved, Is.EquivalentTo(new[] { first.connectionId, second.connectionId }));
                Assert.That(replacement, Is.Not.Null);
                Assert.That(replacement, Is.Not.SameAs(oldEndpoint));
                Assert.That(_manager.RtServer, Is.SameAs(replacement), "Returning from the retiring endpoint must preserve the nested replacement.");
                CompleteHandshake(first, 25);
                CompleteHandshake(second, 26);
                Assert.That(ready, Is.EqualTo(new[] { 21, 22, 21, 22 }));
                Assert.That(replacement.GetReadyConnections().Count, Is.EqualTo(2));

                for (var cycle = 0; cycle < 2; cycle++)
                {
                    var before = _manager.RtServer;
                    _manager.SuspendRuntime();
                    Assert.That(_manager.RtServer, Is.Null);
                    Assert.That(removed.Count, Is.EqualTo(4 + cycle * 2));
                    Assert.That(removed.GetRange(2 + cycle * 2, 2), Is.EquivalentTo(new[] { first.connectionId, second.connectionId }));
                    Assert.That(oldRemoved.Count, Is.EqualTo(2), "The retired endpoint must not acquire replacement subscriptions.");
                    _manager.ResumeRuntime();
                    Assert.That(_manager.RtServer, Is.Not.SameAs(before));
                    replacement = _manager.RtServer;
                    CompleteHandshake(first, (ulong)(27 + cycle * 2));
                    CompleteHandshake(second, (ulong)(28 + cycle * 2));
                    Assert.That(ready.Count, Is.EqualTo(6 + cycle * 2), "Repeated replacement must publish one readiness notification per connection.");
                    Assert.That(_manager.RtServer.GetReadyConnections().Count, Is.EqualTo(2));
                }
                Assert.That(first.DisconnectCount, Is.Zero);
                Assert.That(second.DisconnectCount, Is.Zero);
            }
            finally
            {
                oldEndpoint.ConnectionRemoved -= onOldRemoved;
                _manager.ProtocolConnectionRemoved -= onRemoved;
                _manager.ProtocolConnectionReady -= onReady;
                if (replacement != null && !ReferenceEquals(_manager.RtServer, replacement))
                    replacement.Dispose();
            }
        }

        [Test]
        public void ReplicaStatePublicationResumePreservesReplacementManifestHandler()
        {
            var hellos = new List<RtSessionHello>();
            var ready = new List<RtSessionReady>();
            var resumeRequested = true;
            var detachedBeforeResume = false;
            RuntimeExecutionState observedState = default;
            RuntimeStoreNetClient replacement = null;
            Action<NetworkDiagnostics.MessageInfo> onSent = info =>
            {
                if (info.message is RtSessionHello hello)
                    hellos.Add(hello);
                if (info.message is RtSessionReady value)
                    ready.Add(value);
            };
            Action<RuntimeExecutionState> onState = state =>
            {
                if (!resumeRequested)
                    return;
                resumeRequested = false;
                observedState = state;
                detachedBeforeResume = _manager.RtClient == null;
                _manager.ResumeRuntime();
                replacement = _manager.RtClient;
            };
            NetworkDiagnostics.OutMessageEvent += onSent;
            try
            {
                StartClient();
                var oldEndpoint = _manager.RtClient;
                ReceiveClient(Manifest(11, 21));
                Assert.That(oldEndpoint.AssignedConnectionId, Is.EqualTo(21), "The initial public Manifest handler must provide the positive control.");
                Assert.That(hellos.Count, Is.EqualTo(1));
                Assert.That(ready.Count, Is.EqualTo(1));
                // State Bind publishes even while the client is still awaiting its baseline.
                // Disposal detaches the readiness forwarding, so this callback comes from Suspend's explicit publication.
                RuntimeExecutionContext.State.AddListener(onState);
                try
                {
                    _manager.SuspendRuntime();
                }
                finally
                {
                    RuntimeExecutionContext.State.RemoveListener(onState);
                }

                Assert.That(resumeRequested, Is.False);
                Assert.That(detachedBeforeResume, Is.True);
                Assert.That(observedState.Phase, Is.EqualTo(RuntimeExecutionPhase.ClientConnectingReplica));
                Assert.That(observedState.IsReplicaReady, Is.False);
                Assert.That(replacement, Is.Not.Null);
                Assert.That(replacement, Is.Not.SameAs(oldEndpoint));
                Assert.That(_manager.RtClient, Is.SameAs(replacement));
                Assert.That(oldEndpoint.AssignedConnectionId, Is.EqualTo(-1));
                Assert.That(hellos.Count, Is.EqualTo(2), "Nested Resume must begin exactly one fresh Hello.");
                ReceiveClient(Manifest(12, 22));
                Assert.That(replacement.AssignedConnectionId, Is.EqualTo(22), "Returning from Suspend must preserve the replacement's real Manifest handler.");
                Assert.That(ready.Count, Is.EqualTo(2));
                Assert.That(ready[1].SessionId, Is.EqualTo(12));

                _manager.SuspendRuntime();
                ReceiveClient(Manifest(12, 99));
                Assert.That(_manager.RtClient, Is.Null);
                Assert.That(ready.Count, Is.EqualTo(2), "A late Manifest must be consumed while suspended.");
                Assert.That(NetworkClient.isConnected, Is.True);
                _manager.ResumeRuntime();
                Assert.That(_manager.RtClient, Is.Not.SameAs(replacement));
                Assert.That(hellos.Count, Is.EqualTo(3));
                ReceiveClient(Manifest(13, 23));
                Assert.That(_manager.RtClient.AssignedConnectionId, Is.EqualTo(23));
                Assert.That(ready.Count, Is.EqualTo(3));
                Assert.That(ready[2].SessionId, Is.EqualTo(13));
                Assert.That(NetworkClient.isConnected, Is.True);
            }
            finally
            {
                NetworkDiagnostics.OutMessageEvent -= onSent;
                RuntimeExecutionContext.State.RemoveListener(onState);
            }
        }

        private void StartServer()
        {
            NetworkServer.listen = true;
            _serverStarted = true;
            NetworkServer.Listen(4);
            _manager.OnStartServer();
        }

        private void StartClient()
        {
            _clientStarted = true;
            NetworkClient.Connect("manager-lifecycle-test");
            _transport.CompleteClientConnection();
            NetworkClient.connection.isAuthenticated = true;
            _manager.OnStartClient();
            _manager.OnClientConnect();
        }

        private DingoNetworkManagerLifecycleTestConnection AddConnection(int id)
        {
            var connection = new DingoNetworkManagerLifecycleTestConnection(id);
            Assert.That(NetworkServer.AddConnection(connection), Is.True);
            _manager.RtServer.OnConnectionConnected(id);
            return connection;
        }

        private void CompleteHandshake(DingoNetworkManagerLifecycleTestConnection connection, ulong nonce)
        {
            ReceiveServer(connection, new RtSessionHello { Descriptor = _descriptor, ClientNonce = nonce });
            var manifests = connection.DrainManifests();
            Assert.That(manifests.Count, Is.EqualTo(1), "The public Hello handler must emit exactly one actual Manifest.");
            Assert.That(manifests[0].SessionId, Is.Not.Zero);
            Assert.That(manifests[0].AssignedConnectionId, Is.EqualTo(connection.connectionId));
            ReceiveServer(connection, new RtSessionReady { SessionId = manifests[0].SessionId });
        }

        private void ReceiveServer<T>(NetworkConnectionToClient connection, T value) where T : struct, NetworkMessage
        {
            using var message = NetworkWriterPool.Get();
            NetworkMessages.Pack(value, message);
            var batcher = new Batcher(_transport.GetBatchThreshold());
            batcher.AddMessage(message.ToArraySegment(), NetworkTime.localTime);
            using var batch = NetworkWriterPool.Get();
            Assert.That(batcher.GetBatch(batch), Is.True);
            Assert.That(_transport.OnServerDataReceived, Is.Not.Null);
            _transport.OnServerDataReceived(connection.connectionId, batch.ToArraySegment(), Channels.Reliable);
        }

        private void ReceiveClient<T>(T value) where T : struct, NetworkMessage
        {
            using var message = NetworkWriterPool.Get();
            NetworkMessages.Pack(value, message);
            var batcher = new Batcher(_transport.GetBatchThreshold());
            batcher.AddMessage(message.ToArraySegment(), NetworkTime.localTime);
            using var batch = NetworkWriterPool.Get();
            Assert.That(batcher.GetBatch(batch), Is.True);
            Assert.That(_transport.OnClientDataReceived, Is.Not.Null);
            _transport.OnClientDataReceived(batch.ToArraySegment(), Channels.Reliable);
        }

        private RtSessionManifest Manifest(ulong sessionId, int connectionId) => new() { SessionId = sessionId, AssignedConnectionId = connectionId, Descriptor = _descriptor, Assets = Array.Empty<RuntimeAssetCatalogEntry>(), Stores = Array.Empty<RuntimeStoreCatalogEntry>() };
    }

    public class DingoNetworkManagerLifecycleTestConnection : NetworkConnectionToClient
    {
        public int DisconnectCount { get; private set; }

        public DingoNetworkManagerLifecycleTestConnection(int id) : base(id) { isAuthenticated = true; }
        public override void Disconnect() => DisconnectCount++;

        public List<RtSessionManifest> DrainManifests()
        {
            var messages = new List<RtSessionManifest>();
            var unbatcher = new Unbatcher();
            foreach (var batch in batches.Values)
            {
                using var writer = NetworkWriterPool.Get();
                while (batch.GetBatch(writer))
                {
                    Assert.That(unbatcher.AddBatch(writer.ToArraySegment()), Is.True);
                    writer.Position = 0;
                }
            }
            while (unbatcher.GetNextMessage(out var bytes, out _))
            {
                using var reader = NetworkReaderPool.Get(bytes);
                Assert.That(NetworkMessages.UnpackId(reader, out var id), Is.True);
                Assert.That(id, Is.EqualTo(NetworkMessageId<RtSessionManifest>.Id), "The server handshake must not reject or emit an unrelated packet.");
                messages.Add(reader.Read<RtSessionManifest>());
            }
            return messages;
        }
    }

    public class DingoNetworkManagerLifecycleTestTransport : Transport
    {
        private bool _clientConnected;
        private bool _serverActive;

        public void CompleteClientConnection()
        {
            _clientConnected = true;
            OnClientConnected?.Invoke();
        }

        public override bool Available() => true;
        public override bool ClientConnected() => _clientConnected;
        public override void ClientConnect(string address) { }
        public override void ClientSend(ArraySegment<byte> segment, int channelId = Channels.Reliable) { }
        public override void ClientDisconnect()
        {
            if (!_clientConnected)
                return;
            _clientConnected = false;
            OnClientDisconnected?.Invoke();
        }
        public override Uri ServerUri() => new("manager-lifecycle-test://localhost");
        public override bool ServerActive() => _serverActive;
        public override void ServerStart() => _serverActive = true;
        public override void ServerSend(int connectionId, ArraySegment<byte> segment, int channelId = Channels.Reliable) { }
        public override void ServerDisconnect(int connectionId) { }
        public override string ServerGetClientAddress(int connectionId) => "manager-lifecycle-test";
        public override void ServerStop() => _serverActive = false;
        public override int GetMaxPacketSize(int channelId = Channels.Reliable) => 4096;
        public override void Shutdown()
        {
            ClientDisconnect();
            ServerStop();
        }
    }
}
#endif
