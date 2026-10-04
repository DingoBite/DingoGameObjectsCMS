using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    public static class PrefabGameAssetIndexConfiguration
    {
        public static string IndexAssetPath { get; private set; }
        public static Func<string, bool> IncludeAssetPath { get; private set; }
        public static Func<IReadOnlyList<GameAssetComponent>, IEnumerable<string>> ResourceDependencies { get; private set; }
        public static bool IsConfigured => !string.IsNullOrEmpty(IndexAssetPath);

        public static void Configure(string indexAssetPath, Func<string, bool> includeAssetPath, Func<IReadOnlyList<GameAssetComponent>, IEnumerable<string>> resourceDependencies = null)
        {
            IndexAssetPath = indexAssetPath?.Replace('\\', '/');
            IncludeAssetPath = includeAssetPath;
            ResourceDependencies = resourceDependencies;
            if (IsConfigured)
                PrefabGameAssetIndexEditorUtility.RequestReconcile();
        }

        public static bool Includes(string assetPath)
        {
            var path = assetPath?.Replace('\\', '/');
            return IsConfigured && !string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) && (IncludeAssetPath == null || IncludeAssetPath(path));
        }
    }

    [InitializeOnLoad]
    public static class PrefabGameAssetIndexEditorUtility
    {
        private static bool _scheduled;
        private static bool _reconciling;

        static PrefabGameAssetIndexEditorUtility()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            RequestReconcile();
        }

        public static IReadOnlyList<PrefabGameAssetIndexEntry> CollectEntries()
        {
            return CollectEntries(true, new PrefabGameAssetContentHashCache());
        }

        private static IReadOnlyList<PrefabGameAssetIndexEntry> CollectEntries(bool collectHashes, PrefabGameAssetContentHashCache cache)
        {
            var entries = new List<PrefabGameAssetIndexEntry>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }).OrderBy(value => value, StringComparer.Ordinal))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!PrefabGameAssetIndexConfiguration.Includes(path))
                    continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.TryGetComponent<GameAssetAuthoring>(out var authoring))
                    entries.Add(new PrefabGameAssetIndexEntry(guid, authoring, collectHashes ? PrefabGameAssetContentHashUtility.Compute(path, cache) : null));
            }
            return entries;
        }

        public static PrefabGameAssetIndex Reconcile()
        {
            if (!PrefabGameAssetIndexConfiguration.IsConfigured || _reconciling)
                return null;
            _reconciling = true;
            try
            {
                var path = PrefabGameAssetIndexConfiguration.IndexAssetPath;
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The prefab GA index must use an Assets/.../*.asset path.");
                var index = AssetDatabase.LoadAssetAtPath<PrefabGameAssetIndex>(path);
                if (index == null)
                {
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null)
                        throw new InvalidOperationException("The prefab GA index path contains another asset: " + path);
                    EnsureFolder(path.Substring(0, path.LastIndexOf('/')));
                    index = ScriptableObject.CreateInstance<PrefabGameAssetIndex>();
                    AssetDatabase.CreateAsset(index, path);
                }
                var roots = CollectEntries(false, null);
                PrefabGameAssetOverrideEditorUtility.BakeSavedAssets(roots.Select(entry => AssetDatabase.GetAssetPath(entry.Authoring)));
                // Play, ordinary reconciliation and builds use the same content diagnostics and fallback policy.
                PrefabGameAssetBuildValidation.Validate();
                var hashCache = new PrefabGameAssetContentHashCache();
                var overrides = PrefabGameAssetOverrideEditorUtility.CollectOverrides(roots, hashCache);
                var refreshedRoots = roots.Select(entry => new PrefabGameAssetIndexEntry(entry.GUID, entry.Authoring, PrefabGameAssetContentHashUtility.Compute(AssetDatabase.GUIDToAssetPath(entry.GUID), hashCache)));
                var changed = index.ReplaceEntries(refreshedRoots);
                changed |= index.ReplaceOverrides(overrides);
                if (changed)
                {
                    EditorUtility.SetDirty(index);
                    AssetDatabase.SaveAssetIfDirty(index);
                }
                return index;
            }
            finally
            {
                _reconciling = false;
            }
        }

        public static void RequestReconcile()
        {
            if (_scheduled || _reconciling)
                return;
            _scheduled = true;
            EditorApplication.update += ReconcileDeferred;
        }

        private static void ReconcileDeferred()
        {
            if (!PrefabGameAssetIndexConfiguration.IsConfigured || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorApplication.update -= ReconcileDeferred;
                _scheduled = false;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;
            EditorApplication.update -= ReconcileDeferred;
            _scheduled = false;
            try
            {
                Reconcile();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode)
            {
                EditorApplication.update -= ReconcileDeferred;
                _scheduled = false;
                try
                {
                    Reconcile();
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    EditorApplication.isPlaying = false;
                }
            }
            else if (state == PlayModeStateChange.EnteredEditMode)
                RequestReconcile();
        }

        private static void EnsureFolder(string folder)
        {
            var current = "Assets";
            foreach (var segment in folder.Split('/').Skip(1))
            {
                var next = current + "/" + segment;
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, segment);
                current = next;
            }
        }
    }

    public class PrefabGameAssetIndexImportHook : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (importedAssets.Concat(deletedAssets).Concat(movedAssets).Concat(movedFromAssetPaths).Any(IsRelevant))
                PrefabGameAssetIndexEditorUtility.RequestReconcile();
        }

        private static bool IsRelevant(string path) => PrefabGameAssetIndexConfiguration.Includes(path) || (!string.IsNullOrEmpty(path) && path.StartsWith("Packages/", StringComparison.Ordinal));
    }

    public class PrefabGameAssetIndexSaveHook : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                PrefabGameAssetOverrideEditorUtility.BakeSavedAssets(paths);
            if (paths.Any(path => PrefabGameAssetIndexConfiguration.Includes(path) || path.StartsWith("Packages/", StringComparison.Ordinal)))
                PrefabGameAssetIndexEditorUtility.RequestReconcile();
            return paths;
        }
    }
}
