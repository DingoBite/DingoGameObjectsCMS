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
            foreach (var bind in _bindByKey.Values)
            {
                bind.V = null;
            }
        }

        private static void OnActiveStoresChanged(IReadOnlyDictionary<FixedString32Bytes, RuntimeStore> _)
        {
            if (_bindByKey.Count == 0)
                return;

            var keys = new List<FixedString32Bytes>(_bindByKey.Keys);
            foreach (var key in keys)
            {
                _bindByKey[key].V = ResolveStore(key);
            }
        }

        private static RuntimeStore ResolveStore(FixedString32Bytes key)
        {
            return RuntimeStores.GetRuntimeStore(key, RuntimeExecutionContext.ReadRealm);
        }
    }
}