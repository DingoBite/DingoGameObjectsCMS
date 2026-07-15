using System;
using System.Collections.Generic;
using Bind;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.Stores
{
    public enum RuntimeExecutionPhase : byte
    {
        OfflineAuthoritative = 0,
        ServerAuthoritative = 1,
        HostAuthoritative = 2,
        ClientConnectingReplica = 3,
        ClientReplicaReady = 4,
    }

    public readonly struct RuntimeExecutionState
    {
        public readonly RuntimeExecutionPhase Phase;
        public readonly RuntimeExecutionRole StableRole;
        public readonly StoreRealm ReadRealm;
        public readonly StoreRealm WriteRealm;
        public readonly bool CanMutateStores;
        public readonly bool IsReplicaReady;

        public RuntimeExecutionState(RuntimeExecutionPhase phase, RuntimeExecutionRole stableRole, StoreRealm readRealm, StoreRealm writeRealm, bool canMutateStores, bool isReplicaReady)
        {
            Phase = phase;
            StableRole = stableRole;
            ReadRealm = readRealm;
            WriteRealm = writeRealm;
            CanMutateStores = canMutateStores;
            IsReplicaReady = isReplicaReady;
        }
    }

    public static partial class RuntimeExecutionContext
    {
        private static readonly Bind<RuntimeExecutionState> _state = new();
        private static readonly Bind<RuntimeExecutionPhase> _phase = new();
        private static readonly Bind<IReadOnlyDictionary<FixedString32Bytes, RuntimeStore>> _activeStores = new();

        private static RuntimeExecutionRole _executionRole;
        private static bool _replicaReady;

        public static IReadonlyBind<RuntimeExecutionState> State => _state;
        public static IReadonlyBind<RuntimeExecutionPhase> Phase => _phase;
        public static IReadonlyBind<IReadOnlyDictionary<FixedString32Bytes, RuntimeStore>> ActiveStores => _activeStores;

        public static RuntimeExecutionState Current => _state.V;
        public static StoreRealm ReadRealm => _state.V.ReadRealm;
        public static StoreRealm WriteRealm => _state.V.WriteRealm;
        public static bool CanMutateStores => _state.V.CanMutateStores;
        public static bool IsReplicaReady => _state.V.IsReplicaReady;

        static RuntimeExecutionContext()
        {
            RuntimeStores.ServerStores.AddListener(OnServerStoresChanged);
            RuntimeStores.ClientStores.AddListener(OnClientStoresChanged);
            ResetState();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnSubsystemRegistration()
        {
            ResetState();
        }

        public static void ResetState()
        {
            _executionRole = RuntimeExecutionRole.OfflineAuthoritative;
            _replicaReady = false;
            RefreshState();
        }

        public static void SetRole(RuntimeExecutionRole role)
        {
            ValidateRole(role);
            if (_executionRole != role)
                _replicaReady = false;

            _executionRole = role;
            RefreshState();
        }

        public static void SetReplicaReady(bool ready)
        {
            _replicaReady = _executionRole == RuntimeExecutionRole.ClientReplica && ready;
            RefreshState();
        }

        public static RuntimeStore GetActiveStore(FixedString32Bytes id)
        {
            TryGetActiveStore(id, out var store);
            return store;
        }

        public static bool TryGetActiveStore(FixedString32Bytes id, out RuntimeStore store)
        {
            var stores = _activeStores.V;
            if (stores != null && stores.TryGetValue(id, out store))
                return true;

            store = null;
            return false;
        }

        private static void OnServerStoresChanged(IReadOnlyDictionary<FixedString32Bytes, RuntimeStore> _)
        {
            if (_state.V.ReadRealm == StoreRealm.Server)
                RefreshState();
        }

        private static void OnClientStoresChanged(IReadOnlyDictionary<FixedString32Bytes, RuntimeStore> _)
        {
            if (_state.V.ReadRealm == StoreRealm.Client)
                RefreshState();
        }

        private static void RefreshState()
        {
            var phase = ResolvePhase(_executionRole, _replicaReady);
            var stableRole = ResolveStableRole(phase);
            var readRealm = ResolveReadRealm(phase);
            var writeRealm = ResolveWriteRealm(phase);
            var canMutateStores = ResolveCanMutateStores(phase);
            var isReplicaReady = phase == RuntimeExecutionPhase.ClientReplicaReady;
            var activeStores = readRealm == StoreRealm.Server ? RuntimeStores.ServerStores.V : RuntimeStores.ClientStores.V;

            RuntimeStores.SetRole(stableRole);

            _state.V = new RuntimeExecutionState(phase, stableRole, readRealm, writeRealm, canMutateStores, isReplicaReady);
            _phase.V = phase;
            _activeStores.V = activeStores;
        }

        private static RuntimeExecutionPhase ResolvePhase(RuntimeExecutionRole role, bool replicaReady)
        {
            return role switch
            {
                RuntimeExecutionRole.OfflineAuthoritative => RuntimeExecutionPhase.OfflineAuthoritative,
                RuntimeExecutionRole.ServerAuthoritative => RuntimeExecutionPhase.ServerAuthoritative,
                RuntimeExecutionRole.HostAuthoritative => RuntimeExecutionPhase.HostAuthoritative,
                RuntimeExecutionRole.ClientReplica => replicaReady ? RuntimeExecutionPhase.ClientReplicaReady : RuntimeExecutionPhase.ClientConnectingReplica,
                _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown runtime execution role."),
            };
        }

        private static void ValidateRole(RuntimeExecutionRole role)
        {
            switch (role)
            {
                case RuntimeExecutionRole.OfflineAuthoritative:
                case RuntimeExecutionRole.ServerAuthoritative:
                case RuntimeExecutionRole.HostAuthoritative:
                case RuntimeExecutionRole.ClientReplica:
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown runtime execution role.");
            }
        }

        private static RuntimeExecutionRole ResolveStableRole(RuntimeExecutionPhase phase)
        {
            return phase switch
            {
                RuntimeExecutionPhase.ServerAuthoritative => RuntimeExecutionRole.ServerAuthoritative,
                RuntimeExecutionPhase.HostAuthoritative => RuntimeExecutionRole.HostAuthoritative,
                RuntimeExecutionPhase.ClientConnectingReplica => RuntimeExecutionRole.ClientReplica,
                RuntimeExecutionPhase.ClientReplicaReady => RuntimeExecutionRole.ClientReplica,
                _ => RuntimeExecutionRole.OfflineAuthoritative,
            };
        }

        private static StoreRealm ResolveReadRealm(RuntimeExecutionPhase phase)
        {
            return phase switch
            {
                RuntimeExecutionPhase.ClientConnectingReplica => StoreRealm.Client,
                RuntimeExecutionPhase.ClientReplicaReady => StoreRealm.Client,
                _ => StoreRealm.Server,
            };
        }

        private static StoreRealm ResolveWriteRealm(RuntimeExecutionPhase phase)
        {
            return phase switch
            {
                RuntimeExecutionPhase.ClientConnectingReplica => StoreRealm.Client,
                RuntimeExecutionPhase.ClientReplicaReady => StoreRealm.Client,
                _ => StoreRealm.Server,
            };
        }

        private static bool ResolveCanMutateStores(RuntimeExecutionPhase phase)
        {
            return phase != RuntimeExecutionPhase.ClientConnectingReplica && phase != RuntimeExecutionPhase.ClientReplicaReady;
        }
    }
}
