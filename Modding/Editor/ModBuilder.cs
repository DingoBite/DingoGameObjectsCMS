#if UNITY_EDITOR && NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Serialization;
using DingoUnityExtensions.Utils;
using Newtonsoft.Json;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Modding.Editor
{
    public static class ModBuilder
    {
        private const string GAME_ASSETS_ROOT = "Assets/GameAssets";
        private const bool COPY_NON_GAME_ASSET_DOT_ASSET_FILES = true;

        private sealed class ExportItem
        {
            public string UnityPath;
            public GameAssetScriptableObject Asset;
            public string RelativeJsonPath;
        }

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

                var exportItems = FindAllExportableAssets(modRoot);

                var exportAssetPaths = new HashSet<string>(exportItems.Select(x => x.UnityPath), StringComparer.OrdinalIgnoreCase);

                CopyAllFilesExceptMetaAndExportedAssets(modRoot, dstModRootAbs, exportAssetPaths);

                ExportAssetsToJson(modRoot, dstModRootAbs, exportItems);

                WriteManifest(dstModRootAbs, modName, exportItems);

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

        private static List<ExportItem> FindAllExportableAssets(string modRootUnityPath)
        {
            var guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { modRootUnityPath });

            var list = new List<ExportItem>(guids.Length);

            foreach (var g in guids)
            {
                var p = Normalize(AssetDatabase.GUIDToAssetPath(g));
                if (string.IsNullOrEmpty(p) || !p.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    continue;

                var so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(p);
                if (so is not GameAssetScriptableObject ga)
                    continue;

                list.Add(new ExportItem
                {
                    UnityPath = p,
                    Asset = ga
                });
            }

            list.Sort((a, b) => string.Compare(a.UnityPath, b.UnityPath, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        private static void CopyAllFilesExceptMetaAndExportedAssets(string modRootUnityPath, string dstModRootAbs, HashSet<string> exportedAssetUnityPaths)
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

                    if (exportedAssetUnityPaths.Contains(unityPath))
                        continue;

                    if (!COPY_NON_GAME_ASSET_DOT_ASSET_FILES)
                        continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);
                File.Copy(srcAbs, dstAbs, overwrite: true);
            }
        }

        private static void ExportAssetsToJson(string modRootUnityPath, string dstModRootAbs, List<ExportItem> items)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var it = items[i];
                var unityPath = Normalize(it.UnityPath);

                EditorUtility.DisplayProgressBar("Mod Build", $"Exporting {Path.GetFileNameWithoutExtension(unityPath)}", (float)(i + 1) / Math.Max(1, items.Count));

                var relUnity = unityPath[modRootUnityPath.TrimEnd('/').Length..].TrimStart('/');
                var relJson = Path.ChangeExtension(relUnity, ".json");

                it.RelativeJsonPath = relJson.NormalizePath();

                var dstAbs = Path.Combine(dstModRootAbs, relJson);
                Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);

                var json = it.Asset.ToJson();
                File.WriteAllText(dstAbs, json);
            }
        }

        private static void WriteManifest(string dstModRootAbs, string modName, List<ExportItem> items)
        {
            var m = new ModManifest
            {
                Mod = modName,
                GeneratedUtc = DateTime.UtcNow.ToString("O"),
                ManifestVersion = 1,
                Assets = items.Select(it => new ModManifestEntry
                {
                    Key = it.Asset.Key,
                    GUID = it.Asset.GUID,
                    RelativeJsonPath = it.RelativeJsonPath,
                    SoType = it.Asset.GetType().FullName
                }).OrderBy(e => e.RelativeJsonPath, StringComparer.OrdinalIgnoreCase).ToList()
            };

            var json = JsonConvert.SerializeObject(m, Formatting.Indented, GameAssetJsonRuntime.Settings);

            File.WriteAllText(Path.Combine(dstModRootAbs, "manifest.json"), json);
        }

        private static string UnityPathToAbsolute(string unityPath)
        {
            unityPath = Normalize(unityPath);
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, unityPath));
        }

        private static string Normalize(string s) => (s ?? "").Replace('\\', '/');
    }
}
#endif