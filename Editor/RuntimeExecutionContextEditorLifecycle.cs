using DingoGameObjectsCMS.Stores;
using UnityEditor;

namespace DingoGameObjectsCMS.Editor
{
    public static class RuntimeExecutionContextEditorLifecycle
    {
        [InitializeOnLoadMethod]
        private static void Install()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                RuntimeExecutionContext.ResetState();
        }
    }
}
