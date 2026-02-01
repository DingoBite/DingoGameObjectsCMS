#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using DingoGameObjectsCMS.RuntimeObjects;

namespace DingoGameObjectsCMS.Editor
{
    public static class GameAssetVersioningTools
    {
        private const string DefaultRoot = "Assets/GameAssets";

        private enum BumpKind
        {
            Patch,
            Minor,
            Major
        }

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Version Folder (Patch)", false, 2000)]
        private static void DuplicateSelectedFoldersPatch() => DuplicateSelectedVersionFolders(BumpKind.Patch);

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Version Folder (Patch)", true)]
        private static bool ValidateDuplicateSelectedFoldersPatch() => HasAnySelectedCanonicalVersionFolder();

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Version Folder (Minor)", false, 2001)]
        private static void DuplicateSelectedFoldersMinor() => DuplicateSelectedVersionFolders(BumpKind.Minor);

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Version Folder (Minor)", true)]
        private static bool ValidateDuplicateSelectedFoldersMinor() => HasAnySelectedCanonicalVersionFolder();

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Version Folder (Major)", false, 2002)]
        private static void DuplicateSelectedFoldersMajor() => DuplicateSelectedVersionFolders(BumpKind.Major);

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Version Folder (Major)", true)]
        private static bool ValidateDuplicateSelectedFoldersMajor() => HasAnySelectedCanonicalVersionFolder();

        private static bool HasAnySelectedCanonicalVersionFolder()
        {
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = NormalizeSlashes(AssetDatabase.GUIDToAssetPath(guid));
                if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path) && TryParseCanonicalFolder(path, out _, out _, out _, out _))
                    return true;
            }

            return false;
        }

        private static List<string> GetSelectedCanonicalVersionFolders()
        {
            var result = new List<string>();
            foreach (var guid in Selection.assetGUIDs)
            {
                var path = NormalizeSlashes(AssetDatabase.GUIDToAssetPath(guid));
                if (string.IsNullOrEmpty(path) || !AssetDatabase.IsValidFolder(path))
                    continue;

                if (TryParseCanonicalFolder(path, out _, out _, out _, out _))
                    result.Add(path);
            }

            return result;
        }

        private static void DuplicateSelectedVersionFolders(BumpKind bump)
        {
            var srcDirs = GetSelectedCanonicalVersionFolders();
            if (srcDirs.Count == 0)
            {
                Debug.LogError("Select a canonical version folder: Assets/GameAssets/<mod>/<type>/<key>/<version>");
                return;
            }

            foreach (var srcDir in srcDirs)
            {
                if (!TryParseCanonicalFolder(srcDir, out var mod, out var type, out var key, out var oldVersion))
                {
                    Debug.LogError($"Not in canonical folder: {srcDir}");
                    continue;
                }

                var newVersion = BumpSemver(oldVersion, bump);
                if (string.IsNullOrWhiteSpace(newVersion))
                {
                    Debug.LogError($"Cannot bump version '{oldVersion}' in {srcDir}");
                    continue;
                }

                DuplicateFolderAsVersion(srcDir, mod, type, key, oldVersion, newVersion);
            }
        }

        private static void DuplicateFolderAsVersion(string srcDir, string mod, string type, string key, string oldVersion, string newVersion)
        {
            var dstDir = $"{DefaultRoot}/{mod}/{type}/{key}/{newVersion}".Replace('\\', '/');

            if (AssetDatabase.IsValidFolder(dstDir))
            {
                Debug.LogError($"Destination folder already exists: {dstDir}");
                return;
            }

            EnsureFolderRecursive(dstDir);

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var guids = AssetDatabase.FindAssets("", new[] { srcDir });
            foreach (var g in guids)
            {
                var p = NormalizeSlashes(AssetDatabase.GUIDToAssetPath(g));
                if (AssetDatabase.IsValidFolder(p))
                    continue;

                var rel = p[srcDir.Length..].TrimStart('/');
                var dstPath = NormalizeSlashes($"{dstDir}/{rel}");

                EnsureFolderRecursive(NormalizeSlashes(Path.GetDirectoryName(dstPath)!));

                if (AssetDatabase.LoadAssetAtPath<Object>(dstPath) != null)
                {
                    Debug.LogError($"Destination already exists: {dstPath}");
                    continue;
                }

                if (!AssetDatabase.CopyAsset(p, dstPath))
                    Debug.LogError($"CopyAsset failed:\n  {p}\n  -> {dstPath}");
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var newGuids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { dstDir });
            foreach (var g in newGuids)
            {
                var p = NormalizeSlashes(AssetDatabase.GUIDToAssetPath(g));
                var soObj = AssetDatabase.LoadAssetAtPath<AssetObjects.GameAssetScriptableObject>(p);
                if (soObj == null)
                    continue;

                ApplyVersionAndRegenerateGuid(soObj, newVersion);

                if (soObj is AssetObjects.GameAsset ga)
                {
                    var srcRootPath = $"{srcDir}/{key}.asset".Replace('\\', '/');
                    var srcRoot = AssetDatabase.LoadAssetAtPath<AssetObjects.GameAsset>(srcRootPath);
                    if (srcRoot != null)
                        ApplySourceGuid(ga, srcRoot.GUID);
                }
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"Duplicated {mod}:{type}:{key} {oldVersion} -> {newVersion}");
        }

        private static bool TryParseCanonicalFolder(string dir, out string mod, out string type, out string key, out string version)
        {
            mod = type = key = version = null;

            dir = NormalizeSlashes(dir);
            var root = NormalizeSlashes(DefaultRoot).TrimEnd('/');

            if (!dir.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return false;

            var rel = dir.Substring(root.Length).TrimStart('/');
            var parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length != 4)
                return false;

            mod = parts[0];
            type = parts[1];
            key = parts[2];
            version = parts[3];
            return true;
        }

        private static void ApplyVersionAndRegenerateGuid(Object asset, string version)
        {
            var so = new SerializedObject(asset);

            var keyProp = so.FindProperty("_key");
            var verProp = keyProp?.FindPropertyRelative("Version");
            if (verProp != null)
                verProp.stringValue = version;

            var guidProp = so.FindProperty("_guid");
            if (guidProp != null)
                guidProp.hash128Value = IdUtils.NewHash128FromGuid();

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void ApplySourceGuid(Object asset, Hash128 sourceGuid)
        {
            var so = new SerializedObject(asset);
            var srcProp = so.FindProperty("_sourceAssetGUID");
            if (srcProp != null)
                srcProp.hash128Value = sourceGuid;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void EnsureFolderRecursive(string folderPath)
        {
            folderPath = NormalizeSlashes(folderPath).TrimEnd('/');
            if (folderPath == "Assets" || AssetDatabase.IsValidFolder(folderPath))
                return;

            var parent = NormalizeSlashes(Path.GetDirectoryName(folderPath)!);
            var name = Path.GetFileName(folderPath);

            EnsureFolderRecursive(parent);

            if (!AssetDatabase.IsValidFolder(folderPath))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static string NormalizeSlashes(string s) => (s ?? "").Replace('\\', '/');

        private static string BumpSemver(string version, BumpKind bump)
        {
            if (!TryParseSemver(version, out var major, out var minor, out var patch))
                return null;

            switch (bump)
            {
                case BumpKind.Major:
                    major++;
                    minor = 0;
                    patch = 0;
                    break;
                case BumpKind.Minor:
                    minor++;
                    patch = 0;
                    break;
                case BumpKind.Patch:
                    patch++;
                    break;
            }

            return $"{major}.{minor}.{patch}";
        }

        private static bool TryParseSemver(string s, out int major, out int minor, out int patch)
        {
            major = minor = patch = 0;
            if (string.IsNullOrWhiteSpace(s))
                return false;

            var parts = s.Trim().Split('.');
            if (parts.Length != 3)
                return false;

            return int.TryParse(parts[0], out major) && int.TryParse(parts[1], out minor) && int.TryParse(parts[2], out patch) && major >= 0 && minor >= 0 && patch >= 0;
        }
    }
}
#endif