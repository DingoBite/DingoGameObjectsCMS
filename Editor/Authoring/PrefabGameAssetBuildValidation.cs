using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    public static class PrefabGameAssetBuildValidation
    {
        public static IReadOnlyList<string> CollectErrors()
        {
            var errors = new List<string>();
            var infrastructureErrors = new List<string>();
            CollectErrors(errors, infrastructureErrors);
            return errors.Concat(infrastructureErrors).ToArray();
        }

        private static void CollectErrors(ICollection<string> errors, ICollection<string> infrastructureErrors)
        {
            if (!PrefabGameAssetIndexConfiguration.IsConfigured)
                return;
            var hashCache = new PrefabGameAssetContentHashCache();
            var paths = PrefabGameAssetOverrideEditorUtility.CollectHierarchyAssetPaths(Array.Empty<PrefabGameAssetIndexEntry>());
            foreach (var guid in AssetDatabase.FindAssets("t:PrefabGameAssetIndex", new[] { "Assets" }))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (PrefabGameAssetIndexConfiguration.Includes(path) && path != PrefabGameAssetIndexConfiguration.IndexAssetPath)
                    infrastructureErrors.Add(path + ": only the configured production PrefabGameAssetIndex may exist.");
            }
            foreach (var path in paths.Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    // Reading the persistent prefab avoids creating a live GameObject or running its lifecycle.
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                        ValidateHierarchy(prefab, path, errors, hashCache);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    infrastructureErrors.Add(path + ": prefab validation failed: " + error.Message);
                }
            }
            foreach (var path in paths.Where(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)))
            {
                var scene = default(UnityEngine.SceneManagement.Scene);
                try
                {
                    // Preview scenes do not replace, modify or save the user's open scenes.
                    scene = EditorSceneManager.OpenPreviewScene(path);
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        ValidateHierarchy(root, path, errors, hashCache);
                    }
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    infrastructureErrors.Add(path + ": scene validation failed: " + error.Message);
                }
                finally
                {
                    if (scene.IsValid())
                        EditorSceneManager.ClosePreviewScene(scene);
                }
            }
        }

        public static void Validate()
        {
            var errors = new List<string>();
            var infrastructureErrors = new List<string>();
            CollectErrors(errors, infrastructureErrors);
            foreach (var error in errors.Distinct(StringComparer.Ordinal))
            {
                Debug.LogError(error);
            }
            if (infrastructureErrors.Count > 0)
                throw new BuildFailedException("Prefab GameAsset infrastructure is invalid:\n" + string.Join("\n", infrastructureErrors));
        }

        private static void ValidateHierarchy(GameObject root, string path, ICollection<string> errors, PrefabGameAssetContentHashCache hashCache)
        {
            foreach (var authoring in root.GetComponentsInChildren<GameAssetAuthoring>(true))
            {
                var identity = path + " [" + HierarchyPath(authoring.transform) + "]";
                if (!GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out var definition, out var guid, out var isDefinition, out var error))
                {
                    errors.Add(ContentError(identity, null, error, "an unresolved reference with a safe Try* out default"));
                    continue;
                }
                if (!PrefabGameAssetIndexConfiguration.Includes(AssetDatabase.GetAssetPath(definition)))
                {
                    errors.Add(ContentError(identity, guid, "The source definition is excluded from the production prefab GA index.", "an unresolved reference with a safe Try* out default"));
                    continue;
                }
                if (isDefinition)
                {
                    if (!GameAssetAuthoringEditorUtility.TryCollect(authoring, out _, out error))
                        errors.Add(ContentError(identity, guid, error, "an empty inert definition retaining the prefab key and GUID"));
                }
                else if (!PrefabGameAssetOverrideEditorUtility.TryCreateOverride(authoring, path, out _, out _, out error, hashCache))
                    errors.Add(ContentError(identity, guid, error, "the source prefab definition with discarded instance overrides; a malformed source uses its empty inert definition"));
            }
        }

        private static string ContentError(string identity, string guid, string reason, string fallback) => $"[Prefab GameAsset content] {identity} (source GUID '{guid ?? "unresolved"}'): {reason} Fallback: {fallback}.";

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
        }
    }

    public class PrefabGameAssetBuildFinalizer : IPreprocessBuildWithReport
    {
        public int callbackOrder => int.MaxValue;

        public void OnPreprocessBuild(BuildReport report)
        {
            // BuildPlayerProcessor prepares references for content discovery. Other preprocessors
            // can still change dependencies (for example, clear dynamic font caches) before packing Resources.
            AssetDatabase.SaveAssets();
            PrefabGameAssetIndexEditorUtility.Reconcile();
        }
    }
}
