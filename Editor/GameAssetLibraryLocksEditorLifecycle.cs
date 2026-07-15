using DingoGameObjectsCMS;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using UnityEditor;

namespace DingoGameObjectsCMS.Editor
{
    public static class GameAssetLibraryLocksEditorLifecycle
    {
        [InitializeOnLoadMethod]
        private static void InstallPlayModeReset()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.ExitingEditMode && state != PlayModeStateChange.ExitingPlayMode)
                return;

            GameAssetLibraryManifest.ClearRuntimeCaches();
            GameAssetLibraryLocks.ClearAll(StoreRealm.Server);
            GameAssetLibraryLocks.ClearAll(StoreRealm.Client);
        }
    }
}
