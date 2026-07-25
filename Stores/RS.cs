using System;
using System.Collections.Generic;
using Bind;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Collections;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace DingoGameObjectsCMS.Stores
{
    public static class RS
    {
        private static readonly Dictionary<FixedString32Bytes, Bind<RuntimeStore>> _bindByKey = new();
        private static int _activeStoreBindingSuspensionDepth;
        private static int _resetGeneration;
        private static bool _activeStoreBindingsDirty;
        private static bool _isFlushingActiveStoreBindings;

        public static bool ActiveStoreBindingNotificationsSuspended =>
            _activeStoreBindingSuspensionDepth > 0;
        public static bool IsFlushingActiveStoreBindings =>
            _isFlushingActiveStoreBindings;

        static RS()
        {
            RuntimeExecutionContext.ActiveStores.AddListener(OnActiveStoresChanged);
            ResetState();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnSubsystemRegistration()
        {
            ResetState();
        }

#if UNITY_EDITOR
        [InitializeOnLoadMethod]
        private static void InstallPlayModeReset()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                ResetState();
        }
#endif

        public static IReadonlyBind<RuntimeStore> Bind(FixedString32Bytes key) => GetActiveRuntimeStoreBind(key);

        public static RuntimeStore Get(FixedString32Bytes key) => ResolveStore(key);

        /// <summary>
        /// Keeps existing store-backed presentation bound to its current
        /// stores while an atomic restore prepares a replacement world.
        /// Disposing the returned scope publishes the final active-store set
        /// once, without exposing intermediate restore states.
        /// </summary>
        public static IDisposable SuspendActiveStoreBindingNotifications()
        {
            _activeStoreBindingSuspensionDepth++;
            return new ActiveStoreBindingSuspension(_resetGeneration);
        }
        
        public static RuntimeStore Set(RuntimeStore store)
        {
            if (store == null)
                throw new ArgumentNullException(nameof(store));
            return RuntimeStores.SetRuntimeStore(store);
        }

        private static Bind<RuntimeStore> GetActiveRuntimeStoreBind(FixedString32Bytes key)
        {
            if (_bindByKey.TryGetValue(key, out var bind))
                return bind;

            bind = new Bind<RuntimeStore>(true);
            _bindByKey[key] = bind;
            bind.V = ResolveStore(key);
            return bind;
        }

        private static void ResetState()
        {
            _resetGeneration++;
            _activeStoreBindingSuspensionDepth = 0;
            _activeStoreBindingsDirty = false;
            _isFlushingActiveStoreBindings = false;
            foreach (var bind in _bindByKey.Values)
            {
                bind.V = null;
            }
        }

        private static void OnActiveStoresChanged(IReadOnlyDictionary<FixedString32Bytes, RuntimeStore> _)
        {
            if (_activeStoreBindingSuspensionDepth > 0)
            {
                _activeStoreBindingsDirty = true;
                return;
            }

            RefreshActiveStoreBindings();
        }

        private static void RefreshActiveStoreBindings()
        {
            if (_bindByKey.Count == 0)
            {
                _activeStoreBindingsDirty = false;
                return;
            }

            _activeStoreBindingsDirty = false;
            _isFlushingActiveStoreBindings = true;
            try
            {
                var keys = new List<FixedString32Bytes>(_bindByKey.Keys);
                foreach (var key in keys)
                {
                    _bindByKey[key].V = ResolveStore(key);
                }
            }
            finally
            {
                _isFlushingActiveStoreBindings = false;
            }
        }

        private static void ResumeActiveStoreBindingNotifications(
            int generation)
        {
            if (generation != _resetGeneration)
                return;
            if (_activeStoreBindingSuspensionDepth <= 0)
                throw new InvalidOperationException(
                    "Active-store binding suspension is unbalanced.");

            _activeStoreBindingSuspensionDepth--;
            if (_activeStoreBindingSuspensionDepth == 0
                && _activeStoreBindingsDirty)
            {
                RefreshActiveStoreBindings();
            }
        }

        private static RuntimeStore ResolveStore(FixedString32Bytes key)
        {
            return RuntimeStores.GetRuntimeStore(key, RuntimeExecutionContext.ReadRealm);
        }

        private sealed class ActiveStoreBindingSuspension : IDisposable
        {
            private readonly int _generation;
            private bool _disposed;

            public ActiveStoreBindingSuspension(int generation)
            {
                _generation = generation;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                ResumeActiveStoreBindingNotifications(_generation);
            }
        }
    }
}
