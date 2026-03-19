#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Editor
{
    public static class GameAssetKeyRebuilder
    {
        private const string DEFAULT_ROOT = "Assets/GameAssets";
        private const string DEFAULT_VERSION = "0.0.0";

        private sealed class NormalizationPlan
        {
            public string SourcePath;
            public AssetObjects.GameAssetScriptableObject Asset;
            public string Mod;
            public string Type;
            public string Key;
            public string Version;
        }

        [MenuItem("Assets/Game Assets/Rebuild Keys + Normalize Layout", false, 2000)]
        private static void RebuildKeysAndNormalizeSelectedFolders()
        {
            var folders = GetSelectedFolderPaths();
            if (folders.Count == 0)
            {
                NormalizeUnder(DEFAULT_ROOT);
                return;
            }

            foreach (var folder in folders)
            {
                NormalizeUnder(folder);
            }
        }

        [MenuItem("Tools/Game Assets/Rebuild Keys + Normalize Layout (All)", false, 2000)]
        private static void RebuildKeysAndNormalizeAll()
        {
            NormalizeUnder(DEFAULT_ROOT);
        }


        [MenuItem("Assets/Game Assets/Rebuild Keys + Normalize Layout", true)]
        private static bool ValidateRebuildKeysAndNormalizeSelectedFolders() => Selection.assetGUIDs.Length == 0 || HasAnySelectedFolder();

        private static bool HasAnySelectedFolder()
        {
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                    return true;
            }

            return false;
        }

        private static List<string> GetSelectedFolderPaths()
        {
            var result = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                    result.Add(NormalizeSlashes(path));
            }

            return result;
        }

        private static void NormalizeUnder(string scopeFolder)
        {
            var scope = NormalizeSlashes(scopeFolder);
            var root = NormalizeSlashes(DEFAULT_ROOT);

            if (!IsUnder(root, scope))
            {
                Debug.LogError($"Scope must be inside {root}. Current scope: {scope}");
                return;
            }

            var guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { scope });
            var plans = new List<NormalizationPlan>(guids.Length);
            var foldersToEnsure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var scanned = 0;
            var skipped = 0;

            foreach (var guid in guids)
            {
                var path = NormalizeSlashes(AssetDatabase.GUIDToAssetPath(guid));
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    continue;

                var asset = AssetDatabase.LoadAssetAtPath<AssetObjects.GameAssetScriptableObject>(path);
                if (asset == null)
                    continue;

                scanned++;

                if (!TryParseFromDefaultRoot(path, out var modRaw, out var typeRaw, out var keyRaw, out var versionFromPath))
                {
                    skipped++;
                    continue;
                }

                var mod = NormalizeSegment(modRaw);
                var type = NormalizeSegment(typeRaw);
                var key = NormalizeSegment(keyRaw);
                if (string.IsNullOrWhiteSpace(key))
                {
                    skipped++;
                    continue;
                }

                var version = NormalizeVersion(versionFromPath);
                if (string.IsNullOrWhiteSpace(version))
                    version = DEFAULT_VERSION;

                plans.Add(new NormalizationPlan
                {
                    SourcePath = path,
                    Asset = asset,
                    Mod = mod,
                    Type = type,
                    Key = key,
                    Version = version
                });

                foldersToEnsure.Add(BuildKeyFolderPath(root, mod, type, key));
            }

            foreach (var folder in foldersToEnsure.OrderBy(x => x.Length))
            {
                EnsureFolderRecursive(folder);
            }

            var moved = 0;
            var changedKeys = 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var plan in plans)
                {
                    var desiredPath = BuildCanonicalAssetPath(root, plan.Mod, plan.Type, plan.Key, plan.Version);
                    var finalPath = desiredPath;

                    if (!string.Equals(plan.SourcePath, desiredPath, StringComparison.Ordinal))
                    {
                        if (!string.Equals(plan.SourcePath, desiredPath, StringComparison.OrdinalIgnoreCase) && AssetDatabase.LoadAssetAtPath<Object>(desiredPath) != null)
                        {
                            Debug.LogError($"Destination already exists, skip:\n  src: {plan.SourcePath}\n  dst: {desiredPath}");
                            skipped++;
                            continue;
                        }

                        var err = MoveAssetCaseSafe(plan.SourcePath, desiredPath);
                        if (!string.IsNullOrEmpty(err))
                        {
                            Debug.LogError($"MoveAsset failed: {err}\n  src: {plan.SourcePath}\n  dst: {desiredPath}");
                            skipped++;
                            continue;
                        }

                        moved++;
                    }
                    else
                    {
                        finalPath = plan.SourcePath;
                    }

                    var movedAsset = AssetDatabase.LoadAssetAtPath<AssetObjects.GameAssetScriptableObject>(finalPath) ?? plan.Asset;
                    if (ApplyKey(movedAsset, plan.Mod, plan.Type, plan.Key, plan.Version))
                        changedKeys++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            DeleteEmptyFoldersUnder(scope);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            Debug.Log($"Normalize done. Scanned: {scanned}, Moved: {moved}, KeysChanged: {changedKeys}, Skipped: {skipped}\nScope: {scope}");
        }

        private static bool TryParseFromDefaultRoot(string assetPath, out string mod, out string type, out string key, out string version)
        {
            mod = type = key = version = null;

            var path = NormalizeSlashes(assetPath);
            var root = NormalizeSlashes(DEFAULT_ROOT);

            if (!IsUnder(root, path))
                return false;

            var rel = path[root.Length..].TrimStart('/');
            var parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 4 || !parts[^1].EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return false;

            mod = parts[0];
            type = parts[1];
            key = parts[2];
            var fileName = Path.GetFileNameWithoutExtension(parts[3]);
            if (!TryParseVersionedAssetFileName(fileName, key, out version))
                return false;

            return true;
        }

        private static bool ApplyKey(Object asset, string mod, string type, string key, string version)
        {
            var so = new SerializedObject(asset);
            var keyProp = so.FindProperty("_key");
            if (keyProp == null)
                return false;

            var modProp = keyProp.FindPropertyRelative("Mod");
            var typeProp = keyProp.FindPropertyRelative("Type");
            var keyProp2 = keyProp.FindPropertyRelative("Key");
            var versionProp = keyProp.FindPropertyRelative("Version");

            if (modProp == null || typeProp == null || keyProp2 == null || versionProp == null)
                return false;

            var alreadyMatches =
                modProp.stringValue == mod &&
                typeProp.stringValue == type &&
                keyProp2.stringValue == key &&
                versionProp.stringValue == version;

            if (alreadyMatches)
                return false;

            modProp.stringValue = mod;
            typeProp.stringValue = type;
            keyProp2.stringValue = key;
            versionProp.stringValue = version;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return true;
        }

        private static string BuildKeyFolderPath(string root, string mod, string type, string key)
        {
            return $"{root}/{mod}/{type}/{key}";
        }

        private static string BuildCanonicalAssetPath(string root, string mod, string type, string key, string version)
        {
            var fileName = $"{key}@{NormalizeVersion(version)}.asset";
            return $"{BuildKeyFolderPath(root, mod, type, key)}/{fileName}";
        }

        private static bool TryParseVersionedAssetFileName(string fileNameWithoutExtension, string key, out string version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension) || string.IsNullOrWhiteSpace(key))
                return false;

            var prefix = key + "@";
            if (!fileNameWithoutExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            version = NormalizeVersion(fileNameWithoutExtension[prefix.Length..]);
            return !string.IsNullOrWhiteSpace(version);
        }

        private static string MoveAssetCaseSafe(string from, string to)
        {
            from = NormalizeSlashes(from);
            to = NormalizeSlashes(to);

            if (string.Equals(from, to, StringComparison.Ordinal))
                return string.Empty;

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                var tmp = AssetDatabase.GenerateUniqueAssetPath(to + "__tmp__");
                var err1 = AssetDatabase.MoveAsset(from, tmp);
                if (!string.IsNullOrEmpty(err1))
                    return err1;

                var err2 = AssetDatabase.MoveAsset(tmp, to);
                return err2 ?? string.Empty;
            }

            return AssetDatabase.MoveAsset(from, to);
        }

        private static void DeleteEmptyFoldersUnder(string scopeFolder)
        {
            var scopeAbs = UnityPathToAbsolute(scopeFolder);
            if (!Directory.Exists(scopeAbs))
                return;

            var dirs = Directory.GetDirectories(scopeAbs, "*", SearchOption.AllDirectories)
                .OrderByDescending(x => x.Length)
                .ToArray();

            foreach (var dirAbs in dirs)
            {
                if (Directory.EnumerateFileSystemEntries(dirAbs).Any())
                    continue;

                var unityPath = AbsolutePathToUnityPath(dirAbs);
                if (string.IsNullOrWhiteSpace(unityPath) || !AssetDatabase.IsValidFolder(unityPath))
                    continue;

                AssetDatabase.DeleteAsset(unityPath);
            }
        }

        private static string UnityPathToAbsolute(string unityPath)
        {
            unityPath = NormalizeSlashes(unityPath);
            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, unityPath));
        }

        private static string AbsolutePathToUnityPath(string absPath)
        {
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var normalizedRoot = NormalizeSlashes(projectRoot).TrimEnd('/');
            var normalizedAbs = NormalizeSlashes(Path.GetFullPath(absPath));
            if (!normalizedAbs.StartsWith(normalizedRoot + "/", StringComparison.OrdinalIgnoreCase))
                return null;

            return normalizedAbs[(normalizedRoot.Length + 1)..];
        }

        private static void EnsureFolderRecursive(string folderPath)
        {
            var path = NormalizeSlashes(folderPath).TrimEnd('/');
            if (path == "Assets" || AssetDatabase.IsValidFolder(path))
                return;

            var parent = NormalizeSlashes(Path.GetDirectoryName(path) ?? "Assets").TrimEnd('/');
            var name = Path.GetFileName(path);

            EnsureFolderRecursive(parent);

            if (!AssetDatabase.IsValidFolder(path))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static string NormalizeSlashes(string s) => (s ?? string.Empty).Replace('\\', '/');

        private static bool IsUnder(string parent, string path)
        {
            parent = NormalizeSlashes(parent).TrimEnd('/');
            path = NormalizeSlashes(path);
            return path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSegment(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            s = s.Trim().ToLowerInvariant();
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c.ToString(), string.Empty);
            }

            s = s.Replace(' ', '_');
            return s;
        }

        private static string NormalizeVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return DEFAULT_VERSION;

            return s.Trim();
        }
    }
}
#endif
