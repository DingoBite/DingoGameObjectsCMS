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
        public static event Action Changed;

        public static string IndexAssetPath { get; private set; }
        public static Func<string, bool> IncludeAssetPath { get; private set; }
        public static Func<IReadOnlyList<GameAssetComponent>, IEnumerable<string>> ResourceDependencies { get; private set; }
        public static bool IsConfigured => !string.IsNullOrEmpty(IndexAssetPath);

        public static void Configure(string indexAssetPath, Func<string, bool> includeAssetPath, Func<IReadOnlyList<GameAssetComponent>, IEnumerable<string>> resourceDependencies = null)
        {
            IndexAssetPath = indexAssetPath?.Replace('\\', '/');
            IncludeAssetPath = includeAssetPath;
            ResourceDependencies = resourceDependencies;
            Changed?.Invoke();
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
        private static HashSet<string> _contentDependencyPaths = new(StringComparer.Ordinal);

        static PrefabGameAssetIndexEditorUtility()
        {
            PrefabGameAssetIndexConfiguration.Changed += OnConfigurationChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            RequestReconcile();
        }

        public static IReadOnlyList<PrefabGameAssetIndexEntry> CollectEntries()
        {
            return CollectEntries(true, new PrefabGameAssetContentHashCache());
        }

        private static IReadOnlyList<PrefabGameAssetIndexEntry> CollectEntries(bool collectHashes, PrefabGameAssetContentHashCache cache, GameAssetAuthoringPreparation preparation = null)
        {
            preparation ??= new GameAssetAuthoringPreparation();
            var entries = new List<PrefabGameAssetIndexEntry>();
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" }).OrderBy(value => value, StringComparer.Ordinal))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!PrefabGameAssetIndexConfiguration.Includes(path))
                    continue;
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && prefab.TryGetComponent<GameAssetAuthoring>(out var authoring))
                {
                    var hash = default(string);
                    if (collectHashes)
                    {
                        GameAssetAuthoringEditorUtility.TryCollect(authoring, out var collection, out _, preparation);
                        hash = PrefabGameAssetContentHashUtility.Compute(path, collection?.Components, cache);
                    }
                    entries.Add(new PrefabGameAssetIndexEntry(guid, authoring, hash));
                }
            }
            return entries;
        }

        public static PrefabGameAssetIndex Reconcile()
        {
            if (!PrefabGameAssetIndexConfiguration.IsConfigured || _reconciling)
                return null;
            CancelScheduledReconcile();
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
                var preparation = new GameAssetAuthoringPreparation();
                var paths = PrefabGameAssetOverrideEditorUtility.CollectHierarchyAssetPaths(roots);
                PrefabGameAssetOverrideEditorUtility.BakeSavedAssets(paths.Where(assetPath => assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)), true, preparation);
                var hashCache = new PrefabGameAssetContentHashCache();
                var overrides = PrefabGameAssetBuildValidation.ValidateAndCollectOverrides(paths, preparation, hashCache);
                var refreshedRoots = CollectEntries(true, hashCache, preparation);
                // Prefab saves can reimport dependent assets, including this index.
                index = AssetDatabase.LoadAssetAtPath<PrefabGameAssetIndex>(path);
                var changed = index.ReplaceEntries(refreshedRoots);
                changed |= index.ReplaceOverrides(overrides);
                if (changed)
                {
                    EditorUtility.SetDirty(index);
                    AssetDatabase.SaveAssetIfDirty(index);
                }
                // Use the same roots and effective override dependencies that contributed to the hashes.
                // Keep old paths until reconciliation succeeds so moves and deletions still invalidate them.
                _contentDependencyPaths = new HashSet<string>(hashCache.DependencyPaths, StringComparer.Ordinal);
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

        public static void OnConfigurationChanged()
        {
            _contentDependencyPaths.Clear();
            CancelScheduledReconcile();
            if (PrefabGameAssetIndexConfiguration.IsConfigured)
                RequestReconcile();
        }

        public static bool IsRelevantAssetChange(string assetPath)
        {
            var path = assetPath?.Replace('\\', '/');
            if (!PrefabGameAssetIndexConfiguration.IsConfigured || string.IsNullOrEmpty(path) || string.Equals(path, PrefabGameAssetIndexConfiguration.IndexAssetPath, StringComparison.Ordinal))
                return false;
            if (_contentDependencyPaths.Contains(path))
                return true;
            // New authored hierarchies need discovery; unrelated resources need no index work.
            return PrefabGameAssetIndexConfiguration.Includes(path) && (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase));
        }

        private static void CancelScheduledReconcile()
        {
            EditorApplication.update -= ReconcileDeferred;
            _scheduled = false;
        }

        private static void ReconcileDeferred()
        {
            if (!PrefabGameAssetIndexConfiguration.IsConfigured || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                CancelScheduledReconcile();
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || PrefabGameAssetOverrideEditorUtility.IsBakingSavedAssets)
                return;
            CancelScheduledReconcile();
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
                CancelScheduledReconcile();
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
            if (importedAssets.Length + deletedAssets.Length + movedAssets.Length + movedFromAssetPaths.Length > 0)
                GameAssetAuthoringPreparation.NotifyAssetsChanged();
            if (importedAssets.Concat(deletedAssets).Concat(movedAssets).Concat(movedFromAssetPaths).Any(PrefabGameAssetIndexEditorUtility.IsRelevantAssetChange))
                PrefabGameAssetIndexEditorUtility.RequestReconcile();
        }
    }

    public class PrefabGameAssetIndexSaveHook : AssetModificationProcessor
    {
        private static string[] OnWillSaveAssets(string[] paths)
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode)
                PrefabGameAssetOverrideEditorUtility.BakeSavedAssets(paths, false);
            if (paths.Any(PrefabGameAssetIndexEditorUtility.IsRelevantAssetChange))
                PrefabGameAssetIndexEditorUtility.RequestReconcile();
            return paths;
        }
    }
}
