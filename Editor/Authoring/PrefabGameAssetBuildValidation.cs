using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    public static class PrefabGameAssetBuildValidation
    {
        public static IReadOnlyList<string> CollectErrors()
        {
            var errors = new List<string>();
            if (!PrefabGameAssetIndexConfiguration.IsConfigured)
                return errors;
            foreach (var guid in AssetDatabase.FindAssets("t:PrefabGameAssetIndex", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (PrefabGameAssetIndexConfiguration.Includes(path) && path != PrefabGameAssetIndexConfiguration.IndexAssetPath)
                    errors.Add(path + ": only the configured production PrefabGameAssetIndex may exist.");
            }
            foreach (var path in FindProductionAssets("t:Prefab"))
            {
                try
                {
                    // Reading the persistent prefab avoids creating a live GameObject or running its lifecycle.
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                        ValidateHierarchy(prefab, path, errors);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    errors.Add(path + ": prefab validation failed: " + error.Message);
                }
            }
            foreach (var path in FindProductionAssets("t:Scene"))
            {
                var scene = default(UnityEngine.SceneManagement.Scene);
                try
                {
                    // Preview scenes do not replace, modify or save the user's open scenes.
                    scene = EditorSceneManager.OpenPreviewScene(path);
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        ValidateHierarchy(root, path, errors);
                    }
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    errors.Add(path + ": scene validation failed: " + error.Message);
                }
                finally
                {
                    if (scene.IsValid())
                        EditorSceneManager.ClosePreviewScene(scene);
                }
            }
            return errors;
        }

        public static void Validate()
        {
            var errors = CollectErrors();
            if (errors.Count > 0)
                throw new BuildFailedException("Prefab GameAsset authoring is invalid:\n" + string.Join("\n", errors));
        }

        private static IEnumerable<string> FindProductionAssets(string filter)
        {
            return AssetDatabase.FindAssets(filter, new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Where(PrefabGameAssetIndexConfiguration.Includes).OrderBy(path => path, StringComparer.Ordinal);
        }

        private static void ValidateHierarchy(GameObject root, string path, ICollection<string> errors)
        {
            foreach (var authoring in root.GetComponentsInChildren<GameAssetAuthoring>(true))
            {
                var identity = path + " [" + HierarchyPath(authoring.transform) + "]";
                if (!GameAssetAuthoringEditorUtility.TryCollect(authoring, out _, out var error))
                {
                    errors.Add(identity + ": " + error);
                    continue;
                }
                if (GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out var definition, out _, out _) && !PrefabGameAssetIndexConfiguration.Includes(AssetDatabase.GetAssetPath(definition)))
                    errors.Add(identity + ": the source definition is excluded from the production prefab GA index.");
            }
        }

        private static string HierarchyPath(Transform transform)
        {
            var names = new Stack<string>();
            while (transform != null)
            {
                names.Push(transform.name);
                transform = transform.parent;
            }
            return string.Join("/", names);
        }
    }

    public class PrefabGameAssetBuildProcessor : BuildPlayerProcessor
    {
        public override int callbackOrder => -1;

        public override void PrepareForBuild(BuildPlayerContext context)
        {
            PrefabGameAssetIndexEditorUtility.Reconcile();
            PrefabGameAssetBuildValidation.Validate();
        }
    }
}
