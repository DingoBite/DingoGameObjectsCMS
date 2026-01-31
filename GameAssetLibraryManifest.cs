using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoUnityExtensions.MonoBehaviours.Singletons;
using NaughtyAttributes;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS
{
#if UNITY_EDITOR
    public static class GameAssetManifestPlayHook
    {
        [InitializeOnLoadMethod]
        private static void Install()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingEditMode)
                    GameAssetLibraryManifest.GetNoCheck()?.RebuildCacheInEditor();
            };
        }
    }
#endif
    
    public enum GameAssetFolderType
    {
        BuildIn,
    }
    
    [Serializable, Preserve]
    public class GameAssetFolderPath
    {
        public string Name;
        public GameAssetFolderType FolderType;
        public string SubPath;
    }

    [CreateAssetMenu(menuName = MENU_PREFIX + nameof(GameAssetLibraryManifest), fileName = S_PREFIX + nameof(GameAssetLibraryManifest), order = 0)]
    public class GameAssetLibraryManifest : ProtectedSingletonScriptableObject<GameAssetLibraryManifest>
    {
        private const string MENU_PREFIX = "Game Assets/";

        [SerializeField] private List<GameAssetFolderPath> _assetFolderPaths;
        [SerializeField] private List<GameAsset> _cachedAssets = new();

        public static void AddFolder(GameAssetFolderPath gameAssetFolderPath)
        {
            GetNoCheck()?._assetFolderPaths.Add(gameAssetFolderPath);
        }

        public static void RemoveFolder(GameAssetFolderPath gameAssetFolderPath)
        {
            GetNoCheck()?._assetFolderPaths.Remove(gameAssetFolderPath);
        }

        public static Dictionary<Hash128, GameAsset> CollectAllGameAssets()
        {
            var dict = new Dictionary<Hash128, GameAsset>();

            var cachedAssets = GetNoCheck()?._cachedAssets;
            if (cachedAssets == null)
                return dict;
            
            foreach (var e in cachedAssets)
            {
                if (e == null)
                    continue;

                if (!dict.TryAdd(e.GUID, e))
                    Debug.LogWarning($"Duplicate GameAsset GUID: {e.GUID}", e);
            }

            return dict;
        }

#if UNITY_EDITOR
        [Button]
        public void RebuildCacheInEditor()
        {
            _cachedAssets.Clear();
            
            foreach (var folder in _assetFolderPaths)
            {
                var root = ResolveProjectFolder(folder);
                if (!AssetDatabase.IsValidFolder(root))
                    continue;

                var guids = AssetDatabase.FindAssets($"t:{nameof(GameAsset)}", new[] { root });
                foreach (var guidStr in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guidStr);
                    var asset = AssetDatabase.LoadAssetAtPath<GameAsset>(path);
                    if (asset == null)
                        continue;
                    _cachedAssets.Add(asset);
                }
            }

            _cachedAssets = _cachedAssets.OrderBy(x => x.GUID.ToString()).ToList();

            EditorUtility.SetDirty(this);
            AssetDatabase.SaveAssets();
        }
        
        private static string ResolveProjectFolder(GameAssetFolderPath folder)
        {
            if (folder.FolderType != GameAssetFolderType.BuildIn)
                return string.Empty;

            var sub = (folder.SubPath ?? string.Empty).Replace('\\', '/').Trim('/');
            
            if (string.IsNullOrEmpty(sub))
                return "Assets";
            
            if (sub.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) || sub.Equals("Assets", StringComparison.OrdinalIgnoreCase) || sub.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase) || sub.Equals("Packages", StringComparison.OrdinalIgnoreCase))
            {
                return sub;
            }

            return "Assets/" + sub;
        }
#endif
    }
}