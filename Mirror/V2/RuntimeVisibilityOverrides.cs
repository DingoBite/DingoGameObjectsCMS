#if MIRROR
using System;
using System.Collections.Generic;
using DingoGameObjectsCMS;
using DingoGameObjectsCMS.Mirror;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Reusable default-visible live-interest policy. Exact
    /// connection/store/object overrides are refreshed through a
    /// revision-neutral protocol-v2 membership envelope.
    /// </summary>
    public sealed class RuntimeVisibilityOverrides : IDisposable
    {
        private readonly DingoNetworkManager _networkManager;
        private readonly HashSet<VisibilityKey> _hidden = new();
        private bool _disposed;

        public RuntimeVisibilityOverrides(DingoNetworkManager networkManager)
        {
            _networkManager = networkManager
                              ?? throw new ArgumentNullException(nameof(networkManager));
            _networkManager.ProtocolConnectionRemoved += ClearConnection;
        }

        public bool IsVisible(int connectionId, RuntimeStore store, long objectId)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));

            var storeReference = new NetStoreRef(store.Id, store.StoreGeneration);
            return !_hidden.Contains(new VisibilityKey(connectionId, storeReference, objectId));
        }

        public RuntimeInterestRefreshResult SetVisible(
            int connectionId,
            in NetObjectRef objectReference,
            bool visible)
        {
            ThrowIfDisposed();
            if (!objectReference.IsValid)
                throw new ArgumentException("Visibility override requires a valid network object reference.", nameof(objectReference));
            if (!RuntimeStores.TryGetRuntimeStore(
                    objectReference.Store.StoreId,
                    objectReference.Store.StoreGeneration,
                    StoreRealm.Server,
                    out var store)
                || store == null
                || !store.TryTakeRO(objectReference.ObjectId, out _))
            {
                return new RuntimeInterestRefreshResult(
                    RuntimeInterestRefreshStatus.InvalidStore,
                    detail: $"Visibility override references inactive object '{objectReference}'.");
            }

            var key = new VisibilityKey(connectionId, objectReference.Store, objectReference.ObjectId);
            var wasHidden = _hidden.Contains(key);
            var shouldHide = !visible;
            if (wasHidden == shouldHide)
            {
                return new RuntimeInterestRefreshResult(
                    RuntimeInterestRefreshStatus.NoChange,
                    detail: $"Visibility of '{objectReference}' is already {(visible ? "visible" : "hidden")}.");
            }

            if (shouldHide)
                _hidden.Add(key);
            else
                _hidden.Remove(key);

            var result = _networkManager.RefreshInterest(connectionId, objectReference.Store);
            if (result.Status == RuntimeInterestRefreshStatus.InvalidProjection
                || result.Status == RuntimeInterestRefreshStatus.InvalidStore)
            {
                Restore(key, wasHidden);
            }

            return result;
        }

        public RuntimeInterestRefreshResult RefreshInterest(
            int connectionId,
            in NetStoreRef store)
        {
            ThrowIfDisposed();
            return _networkManager.RefreshInterest(connectionId, store);
        }

        public int RefreshInterest(int connectionId)
        {
            ThrowIfDisposed();
            return _networkManager.RefreshInterest(connectionId);
        }

        public int RefreshInterestAll()
        {
            ThrowIfDisposed();
            return _networkManager.RefreshInterestAll();
        }

        public void ClearConnection(int connectionId)
        {
            _hidden.RemoveWhere(value => value.ConnectionId == connectionId);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _networkManager.ProtocolConnectionRemoved -= ClearConnection;
            _hidden.Clear();
        }

        private void Restore(in VisibilityKey key, bool hidden)
        {
            if (hidden)
                _hidden.Add(key);
            else
                _hidden.Remove(key);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(RuntimeVisibilityOverrides));
        }

        private readonly struct VisibilityKey : IEquatable<VisibilityKey>
        {
            public readonly int ConnectionId;
            public readonly NetStoreRef Store;
            public readonly long ObjectId;

            public VisibilityKey(int connectionId, NetStoreRef store, long objectId)
            {
                ConnectionId = connectionId;
                Store = store;
                ObjectId = objectId;
            }

            public bool Equals(VisibilityKey other)
            {
                return ConnectionId == other.ConnectionId
                       && Store == other.Store
                       && ObjectId == other.ObjectId;
            }

            public override bool Equals(object value)
            {
                return value is VisibilityKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return HashCode.Combine(ConnectionId, Store, ObjectId);
            }
        }
    }
}
#endif
