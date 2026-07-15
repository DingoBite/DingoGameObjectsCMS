#if MIRROR
using System;
using Mirror;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror
{
    [DisallowMultipleComponent]
    public class NetworkRoleBootstrap : MonoBehaviour
    {
        [SerializeField] private DingoNetworkManager _networkManager;
        [SerializeField] private bool _startAutomatically;

        private bool _hasApplied;
        private RuntimeNetRole _appliedRole = RuntimeNetRole.Offline;

        public bool HasApplied => _hasApplied;
        public RuntimeNetRole AppliedRole => _appliedRole;

        private void Start()
        {
            if (_startAutomatically)
                StartConfiguredRole();
        }

        public bool StartConfiguredRole()
        {
            return StartConfiguredRole(Environment.GetCommandLineArgs(), NetworkRoleBootstrapTagSource.ReadCurrent());
        }

        public bool StartConfiguredRole(string[] arguments, string[] playerTags)
        {
            if (_hasApplied)
                return false;
            if (_networkManager == null)
                throw new InvalidOperationException($"{nameof(NetworkRoleBootstrap)} on '{name}' requires a serialized {nameof(DingoNetworkManager)} reference.");
            if (!_networkManager.enabled)
                throw new InvalidOperationException($"{nameof(NetworkRoleBootstrap)} cannot start disabled manager '{_networkManager.name}'.");
            if (NetworkManager.singleton != null && NetworkManager.singleton != _networkManager)
                throw new InvalidOperationException($"{nameof(NetworkRoleBootstrap)} expected manager '{_networkManager.name}', but Mirror singleton is '{NetworkManager.singleton.name}'.");

            var config = NetworkRoleBootstrapParser.Parse(arguments, playerTags);
            if (NetworkServer.active || NetworkClient.active)
            {
                var activeRole = _networkManager.RuntimeRole;
                if (activeRole != config.Role)
                    throw new InvalidOperationException($"Network is already active as {activeRole}, but bootstrap requested {config.Role}.");

                _hasApplied = true;
                _appliedRole = activeRole;
                return false;
            }

            ApplyConnectionSettings(config);
            _hasApplied = true;
            _appliedRole = config.Role;

            switch (config.Role)
            {
                case RuntimeNetRole.Offline:
                    return false;
                case RuntimeNetRole.Server:
                    _networkManager.StartServer();
                    return true;
                case RuntimeNetRole.Host:
                    _networkManager.StartHost();
                    return true;
                case RuntimeNetRole.Client:
                    _networkManager.StartClient();
                    return true;
                default:
                    throw new ArgumentOutOfRangeException(nameof(config.Role), config.Role, null);
            }
        }

        private void ApplyConnectionSettings(NetworkRoleBootstrapConfig config)
        {
            if (config.Address != null)
                _networkManager.networkAddress = config.Address;
            if (!config.Port.HasValue)
                return;
            if (_networkManager.transport is not PortTransport portTransport)
                throw new InvalidOperationException($"Transport on '{_networkManager.name}' does not support the {NetworkRoleBootstrapParser.PORT_ARGUMENT} argument.");

            portTransport.Port = config.Port.Value;
        }
    }
}
#endif
