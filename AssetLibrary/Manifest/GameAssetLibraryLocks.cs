using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Stores;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public static class GameAssetLibraryLocks
    {
        private static readonly Dictionary<LockKey, GameAssetLibraryLock> _locks = new();

        static GameAssetLibraryLocks()
        {
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

        public static void Set(FixedString32Bytes lockName, GameAssetLibraryLock assetLock)
        {
            Set(lockName, RuntimeExecutionContext.WriteRealm, assetLock);
        }

        public static void Set(FixedString32Bytes lockName, StoreRealm realm, GameAssetLibraryLock assetLock)
        {
            var key = new LockKey(lockName, realm);
            if (assetLock == null)
            {
                _locks.Remove(key);
                return;
            }

            _locks[key] = assetLock;
        }

        public static bool TryGet(FixedString32Bytes lockName, out GameAssetLibraryLock assetLock)
        {
            return TryGet(lockName, RuntimeExecutionContext.ReadRealm, out assetLock);
        }

        public static bool TryGet(FixedString32Bytes lockName, StoreRealm realm, out GameAssetLibraryLock assetLock)
        {
            return _locks.TryGetValue(new LockKey(lockName, realm), out assetLock);
        }

        public static void Clear(FixedString32Bytes lockName)
        {
            Clear(lockName, RuntimeExecutionContext.WriteRealm);
        }

        public static void Clear(FixedString32Bytes lockName, StoreRealm realm)
        {
            _locks.Remove(new LockKey(lockName, realm));
        }

        public static void ClearAll(StoreRealm realm)
        {
            List<LockKey> keysToRemove = null;
            foreach (var key in _locks.Keys)
            {
                if (key.Realm != realm)
                    continue;

                keysToRemove ??= new List<LockKey>();
                keysToRemove.Add(key);
            }

            if (keysToRemove == null)
                return;

            foreach (var key in keysToRemove)
            {
                _locks.Remove(key);
            }
        }

        public static bool TryResolve(FixedString32Bytes lockName, GameAssetKey key, out GameAssetScriptableObject asset)
        {
            TryGet(lockName, out var assetLock);
            return GameAssetLibraryLockBuilder.TryResolve(key, assetLock, out asset);
        }

        public static bool TryResolve(FixedString32Bytes lockName, StoreRealm realm, GameAssetKey key, out GameAssetScriptableObject asset)
        {
            TryGet(lockName, realm, out var assetLock);
            return GameAssetLibraryLockBuilder.TryResolve(key, assetLock, out asset);
        }

        private static void ResetState()
        {
            _locks.Clear();
        }

        private readonly struct LockKey : IEquatable<LockKey>
        {
            public readonly FixedString32Bytes Name;
            public readonly StoreRealm Realm;

            public LockKey(FixedString32Bytes name, StoreRealm realm)
            {
                Name = name;
                Realm = realm;
            }

            public bool Equals(LockKey other)
            {
                return Name.Equals(other.Name) && Realm == other.Realm;
            }

            public override bool Equals(object obj)
            {
                return obj is LockKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                var hc = new HashCode();
                hc.Add(Name);
                hc.Add((byte)Realm);
                return hc.ToHashCode();
            }
        }
    }
}
