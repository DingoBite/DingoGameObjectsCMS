using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using NaughtyAttributes;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests
{
    public class ModSmokeTest : MonoBehaviour
    {
        [SerializeField] private Object _loadedObject;
        [SerializeField] private GameAssetKey _loadTest;
        
        public void Start()
        {
            var man = GameAssetLibraryManifest.GetNoCheck();
            if (man == null)
                return;
            man.RebuildRuntimeCache();
        }

        [Button]
        private void Load()
        {
            var key = _loadTest;

            if (GameAssetLibraryManifest.TryResolve(key, out var so))
            {
                _loadedObject = so;
                Debug.Log($"OK: {key.Mod}:{key.Type}:{key.Key}:{key.Version} -> {so.GetType().Name} GUID={so.GUID}");
            }
            else
            {
                Debug.LogError($"FAIL: cannot resolve {key.Mod}:{key.Type}:{key.Key}:{key.Version}");
            }
        }
    }
}