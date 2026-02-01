#if UNITY_EDITOR && NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Serialization;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor
{
    public static class ModBuilder
    {
        private const string GAME_ASSETS_ROOT = "Assets/GameAssets";
        private const bool COPY_NON_GAME_ASSET_DOT_ASSET_FILES = true;

        [MenuItem("Assets/Game Assets/Build Mod To Folder...", false, 2200)]
        private static void BuildModToFolderMenu()
        {
            var modRoot = GetSelectedModRoot();
            if (string.IsNullOrEmpty(modRoot))
            {
                Debug.LogError($"Select a folder inside {GAME_ASSETS_ROOT}/<mod>/...");
                return;
            }

            var modName = Path.GetFileName(modRoot.TrimEnd('/'));
            var dst = EditorUtility.SaveFolderPanel("Build Mod To Folder", "", modName);
            if (string.IsNullOrEmpty(dst))
                return;

            var dstModRootAbs = dst;

            if (Directory.Exists(dstModRootAbs) && Directory.EnumerateFileSystemEntries(dstModRootAbs).Any())
            {
                var ok = EditorUtility.DisplayDialog("Destination is not empty", "Destination folder is not empty. Overwrite its contents?", "Overwrite", "Cancel");

                if (!ok)
                    return;

                Directory.Delete(dstModRootAbs, recursive: true);
            }

            Directory.CreateDirectory(dstModRootAbs);

            try
            {
                EditorUtility.DisplayProgressBar("Mod Build", "Preparing...", 0f);

                var gameAssetPaths = FindAllGameAssetPaths(modRoot);

                CopyAllFilesExceptMetaAndGameAssetAssets(modRoot, dstModRootAbs, gameAssetPaths);

                ExportGameAssetsToJson(modRoot, dstModRootAbs, gameAssetPaths);

                WriteManifest(dstModRootAbs, modName, gameAssetPaths);

                Debug.Log($"Mod build complete: {dstModRootAbs}");
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        [MenuItem("Assets/Game Assets/Build Mod To Folder...", true)]
        private static bool ValidateBuildModToFolderMenu() => !string.IsNullOrEmpty(GetSelectedModRoot());

        private static string GetSelectedModRoot()
        {
            foreach (var guid in Selection.assetGUIDs)
            {
                var p = Normalize(AssetDatabase.GUIDToAssetPath(guid));
                if (string.IsNullOrEmpty(p) || !AssetDatabase.IsValidFolder(p))
                    continue;

                if (!p.StartsWith(GAME_ASSETS_ROOT + "/", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cur = p.TrimEnd('/');
                while (!string.Equals(cur, GAME_ASSETS_ROOT, StringComparison.OrdinalIgnoreCase))
                {
                    var parent = Normalize(Path.GetDirectoryName(cur) ?? "");
                    if (string.Equals(parent, GAME_ASSETS_ROOT, StringComparison.OrdinalIgnoreCase))
                        return cur;

                    cur = parent;
                    if (string.IsNullOrEmpty(cur))
                        break;
                }
            }

            return null;
        }

        private static HashSet<string> FindAllGameAssetPaths(string modRootUnityPath)
        {
            var guids = AssetDatabase.FindAssets("t:GameAsset", new[] { modRootUnityPath });
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in guids)
            {
                var p = Normalize(AssetDatabase.GUIDToAssetPath(g));
                if (!string.IsNullOrEmpty(p))
                    set.Add(p);
            }

            return set;
        }

        private static void CopyAllFilesExceptMetaAndGameAssetAssets(string modRootUnityPath, string dstModRootAbs, HashSet<string> gameAssetUnityPaths)
        {
            var modRootAbs = UnityPathToAbsolute(modRootUnityPath);

            var files = Directory.EnumerateFiles(modRootAbs, "*", SearchOption.AllDirectories).ToList();
            foreach (var srcAbs in files)
            {
                var ext = Path.GetExtension(srcAbs).ToLowerInvariant();

                if (ext == ".meta")
                    continue;

                var rel = Path.GetRelativePath(modRootAbs, srcAbs);
                var dstAbs = Path.Combine(dstModRootAbs, rel);

                if (ext == ".asset")
                {
                    var unityPath = Normalize($"{modRootUnityPath.TrimEnd('/')}/{rel}".Replace('\\', '/'));
                    if (gameAssetUnityPaths.Contains(unityPath))
                    {
                        continue;
                    }

                    if (!COPY_NON_GAME_ASSET_DOT_ASSET_FILES)
                        continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);
                File.Copy(srcAbs, dstAbs, overwrite: true);
            }
        }

        private static void ExportGameAssetsToJson(string modRootUnityPath, string dstModRootAbs, HashSet<string> gameAssetUnityPaths)
        {
            var list = gameAssetUnityPaths.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

            for (var i = 0; i < list.Count; i++)
            {
                var unityPath = Normalize(list[i]);
                var asset = AssetDatabase.LoadAssetAtPath<GameAsset>(unityPath);
                if (asset == null)
                    continue;

                EditorUtility.DisplayProgressBar("Mod Build", $"Exporting {Path.GetFileNameWithoutExtension(unityPath)}", (float)(i + 1) / Math.Max(1, list.Count));

                var relUnity = unityPath[modRootUnityPath.TrimEnd('/').Length..].TrimStart('/');
                var relJson = Path.ChangeExtension(relUnity, ".json");

                var dstAbs = Path.Combine(dstModRootAbs, relJson);
                Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);

                var json = asset.ToJson();
                File.WriteAllText(dstAbs, json);
            }
        }

        private static void WriteManifest(string dstModRootAbs, string modName, HashSet<string> gameAssetUnityPaths)
        {
            var manifest = new ModManifest
            {
                Mod = modName,
                GeneratedUtc = DateTime.UtcNow.ToString("O"),
                GameAssets = gameAssetUnityPaths.Select(p => new ModManifestEntry
                {
                    UnityPath = p,
                    RelativeJsonPath = Path.ChangeExtension(p[$"{GAME_ASSETS_ROOT}/{modName}".Length..].TrimStart('/'), ".json")
                }).OrderBy(e => e.RelativeJsonPath, StringComparer.OrdinalIgnoreCase).ToList()
            };

            var json = Newtonsoft.Json.JsonConvert.SerializeObject(manifest, Newtonsoft.Json.Formatting.Indented);

            File.WriteAllText(Path.Combine(dstModRootAbs, "manifest.json"), json);
        }

        private static string UnityPathToAbsolute(string unityPath)
        {
            unityPath = Normalize(unityPath);
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            var abs = Path.Combine(projectRoot, unityPath);
            return Path.GetFullPath(abs);
        }

        private static string Normalize(string s) => (s ?? "").Replace('\\', '/');
    }

    [Serializable]
    internal sealed class ModManifest
    {
        public string Mod;
        public string GeneratedUtc;
        public List<ModManifestEntry> GameAssets;
    }

    [Serializable]
    internal sealed class ModManifestEntry
    {
        public string UnityPath;
        public string RelativeJsonPath;
    }
}
#endif