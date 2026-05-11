using System;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Collections;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public static class A
    {
        public static bool TryGet(GameAssetKey key, out GameAssetScriptableObject asset)
        {
            if (!HasFullKey(key))
            {
                asset = null;
                return false;
            }

            return GameAssetLibraryManifest.TryResolve(key, out asset);
        }

        public static bool TryGet(FixedString32Bytes lockName, GameAssetKey key, out GameAssetScriptableObject asset)
        {
            return GameAssetLibraryLocks.TryResolve(lockName, key, out asset);
        }

        public static GameAssetScriptableObject Get(GameAssetKey key)
        {
            if (TryGet(key, out var asset))
                return asset;

            throw new InvalidOperationException($"Cannot resolve asset by full key: {key}");
        }

        public static GameAssetScriptableObject Get(FixedString32Bytes lockName, GameAssetKey key)
        {
            if (TryGet(lockName, key, out var asset))
                return asset;

            throw new InvalidOperationException($"Cannot resolve asset by lock '{lockName}' and key: {key}");
        }

        private static bool HasFullKey(GameAssetKey key)
        {
            return !string.IsNullOrWhiteSpace(key.Mod)
                   && !string.IsNullOrWhiteSpace(key.Type)
                   && !string.IsNullOrWhiteSpace(key.Key)
                   && !string.IsNullOrWhiteSpace(key.Version);
        }
    }
}
