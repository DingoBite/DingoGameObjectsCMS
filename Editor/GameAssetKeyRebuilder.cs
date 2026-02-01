#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Editor
{
    public static class GameAssetKeyRebuilder
    {
        private const string DEFAULT_ROOT = "Assets/GameAssets";
        private const string DEFAULT_VERSION = "0.0.0";

        private enum LayoutKind
        {
            LegacyFlat,
            KeyFolder,
            Canonical,
            Unknown
        }

        [MenuItem("Assets/Game Assets/Rebuild Keys + Normalize Layout", false, 2000)]
        private static void RebuildKeysAndNormalizeSelectedFolders()
        {
            var folders = GetSelectedFolderPaths();
            if (folders.Count == 0)
            {
                NormalizeUnder(DEFAULT_ROOT, convertLegacyForceZeroVersion: false);
                return;
            }

            foreach (var f in folders)
            {
                NormalizeUnder(f, convertLegacyForceZeroVersion: false);
            }
        }

        [MenuItem("Assets/Game Assets/Convert Legacy -> <key>/<0.0.0>/<key>.asset", false, 2001)]
        private static void ConvertLegacySelectedFolders()
        {
            var folders = GetSelectedFolderPaths();
            if (folders.Count == 0)
            {
                NormalizeUnder(DEFAULT_ROOT, convertLegacyForceZeroVersion: true);
                return;
            }

            foreach (var f in folders)
            {
                NormalizeUnder(f, convertLegacyForceZeroVersion: true);
            }
        }

        [MenuItem("Assets/Game Assets/Rebuild Keys + Normalize Layout", true)]
        private static bool ValidateRebuildKeysAndNormalizeSelectedFolders() => HasAnySelectedFolder();

        [MenuItem("Assets/Game Assets/Convert Legacy -> <key>/<0.0.0>/<key>.asset", true)]
        private static bool ValidateConvertLegacySelectedFolders() => HasAnySelectedFolder();

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

        private static void NormalizeUnder(string scopeFolder, bool convertLegacyForceZeroVersion)
        {
            var scope = NormalizeSlashes(scopeFolder);
            var root = NormalizeSlashes(DEFAULT_ROOT);

            if (!IsUnder(root, scope))
            {
                Debug.LogError($"Scope must be inside {root}. Current scope: {scope}");
                return;
            }

            var guids = AssetDatabase.FindAssets("t:ScriptableObject", new[] { scope });

            var foldersToEnsure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folderRenames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var guid in guids)
            {
                var path = NormalizeSlashes(AssetDatabase.GUIDToAssetPath(guid));
                if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryParseFromDefaultRoot(path, out var modRaw, out var typeRaw, out var keyRaw, out var versionFromPath, out var layout))
                    continue;

                var asset = AssetDatabase.LoadAssetAtPath<AssetObjects.GameAssetScriptableObject>(path);
                if (asset == null)
                    continue;

                var modNorm = NormalizeSegment(modRaw);
                var typeNorm = NormalizeSegment(typeRaw);

                var keyNorm = layout == LayoutKind.LegacyFlat ? NormalizeSegment(Path.GetFileNameWithoutExtension(path)) : NormalizeSegment(keyRaw);

                var version = layout == LayoutKind.Canonical ? NormalizeVersion(versionFromPath) : (convertLegacyForceZeroVersion ? DEFAULT_VERSION : ReadVersionOrDefault(asset));

                if (layout is LayoutKind.Canonical or LayoutKind.KeyFolder)
                {
                    var currentKeyFolder = layout == LayoutKind.Canonical
                        ? NormalizeSlashes(Path.GetDirectoryName(Path.GetDirectoryName(path)!)!)
                        : NormalizeSlashes(Path.GetDirectoryName(path)!);

                    var desiredKeyFolder = $"{root}/{modNorm}/{typeNorm}/{keyNorm}";

                    if (!string.Equals(currentKeyFolder, desiredKeyFolder, StringComparison.Ordinal))
                        folderRenames[currentKeyFolder] = desiredKeyFolder;
                }

                var desiredFolder = $"{root}/{modNorm}/{typeNorm}/{keyNorm}/{version}";
                foldersToEnsure.Add(desiredFolder);
            }

            foreach (var kv in folderRenames)
            {
                var toParent = NormalizeSlashes(Path.GetDirectoryName(kv.Value)!);
                EnsureFolderRecursive(toParent);
            }

            foreach (var kv in folderRenames)
            {
                var from = kv.Key;
                var to = kv.Value;

                if (!AssetDatabase.IsValidFolder(from))
                    continue;

                if (!string.Equals(from, to, StringComparison.OrdinalIgnoreCase) && AssetDatabase.IsValidFolder(to))
                {
                    Debug.LogError($"Target key-folder already exists, skip:\n  from: {from}\n  to:   {to}");
                    continue;
                }

                var err = MoveAssetCaseSafe(from, to);
                if (!string.IsNullOrEmpty(err))
                    Debug.LogError($"Key-folder rename failed: {err}\n  from: {from}\n  to:   {to}");
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            foreach (var f in foldersToEnsure)
            {
                EnsureFolderRecursive(f);
            }

            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

            var scanned = 0;
            var changedKeys = 0;
            var moved = 0;
            var skipped = 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var guid in guids)
                {
                    var path = NormalizeSlashes(AssetDatabase.GUIDToAssetPath(guid));
                    if (string.IsNullOrEmpty(path) || !path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var asset = AssetDatabase.LoadAssetAtPath<AssetObjects.GameAssetScriptableObject>(path);
                    if (asset == null)
                        continue;

                    scanned++;

                    if (!TryParseFromDefaultRoot(path, out var modRaw, out var typeRaw, out var keyRaw, out var versionFromPath, out var layout))
                    {
                        skipped++;
                        continue;
                    }

                    var mod = NormalizeSegment(modRaw);
                    var type = NormalizeSegment(typeRaw);

                    var key = layout == LayoutKind.LegacyFlat ? NormalizeSegment(Path.GetFileNameWithoutExtension(path)) : NormalizeSegment(keyRaw);

                    var version = layout == LayoutKind.Canonical ? NormalizeVersion(versionFromPath) : (convertLegacyForceZeroVersion ? DEFAULT_VERSION : ReadVersionOrDefault(asset));

                    var desiredFolder = $"{root}/{mod}/{type}/{key}/{version}";
                    var desiredPath = $"{desiredFolder}/{key}.asset";

                    if (!string.Equals(path, desiredPath, StringComparison.Ordinal))
                    {
                        if (!string.Equals(path, desiredPath, StringComparison.OrdinalIgnoreCase) && AssetDatabase.LoadAssetAtPath<Object>(desiredPath) != null)
                        {
                            Debug.LogError($"Destination already exists, skip:\n  src: {path}\n  dst: {desiredPath}");
                            skipped++;
                            continue;
                        }

                        var err = MoveAssetCaseSafe(path, desiredPath);
                        if (!string.IsNullOrEmpty(err))
                        {
                            Debug.LogError($"MoveAsset failed: {err}\n  src: {path}\n  dst: {desiredPath}");
                            skipped++;
                            continue;
                        }

                        moved++;
                        path = desiredPath;
                    }

                    if (ApplyKey(asset, mod, type, key, version))
                        changedKeys++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            Debug.Log($"Normalize done. Scanned: {scanned}, Moved: {moved}, KeysChanged: {changedKeys}, Skipped: {skipped}\nScope: {scope}");
        }

        private static bool TryParseFromDefaultRoot(string assetPath, out string mod, out string type, out string key, out string version, out LayoutKind layout)
        {
            mod = type = key = version = null;
            layout = LayoutKind.Unknown;

            var path = NormalizeSlashes(assetPath);
            var root = NormalizeSlashes(DEFAULT_ROOT);

            if (!IsUnder(root, path))
                return false;

            var rel = path[root.Length..].TrimStart('/');
            var parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 3 || !parts[^1].EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return false;

            mod = parts[0];
            type = parts[1];

            if (parts.Length == 3)
            {
                key = Path.GetFileNameWithoutExtension(parts[2]);
                layout = LayoutKind.LegacyFlat;
                return true;
            }

            if (parts.Length == 4)
            {
                key = parts[2];
                layout = LayoutKind.KeyFolder;
                return true;
            }

            if (parts.Length == 5)
            {
                key = parts[2];
                version = parts[3];
                layout = LayoutKind.Canonical;
                return true;
            }

            return false;
        }

        private static string ReadVersionOrDefault(Object asset)
        {
            var so = new SerializedObject(asset);
            var keyProp = so.FindProperty("_key");
            var vProp = keyProp?.FindPropertyRelative("Version");
            var v = vProp?.stringValue;
            return NormalizeVersion(string.IsNullOrWhiteSpace(v) ? DEFAULT_VERSION : v);
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
            var verProp = keyProp.FindPropertyRelative("Version");

            if (modProp == null || typeProp == null || keyProp2 == null || verProp == null)
                return false;

            var already = modProp.stringValue == mod && typeProp.stringValue == type && keyProp2.stringValue == key && verProp.stringValue == version;

            if (already)
                return false;

            modProp.stringValue = mod;
            typeProp.stringValue = type;
            keyProp2.stringValue = key;
            verProp.stringValue = version;

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
            return true;
        }

        private static string MoveAssetCaseSafe(string from, string to)
        {
            from = NormalizeSlashes(from);
            to = NormalizeSlashes(to);

            if (string.Equals(from, to, StringComparison.Ordinal))
                return "";

            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
            {
                var tmp = AssetDatabase.GenerateUniqueAssetPath(to + "__tmp__");
                var err1 = AssetDatabase.MoveAsset(from, tmp);
                if (!string.IsNullOrEmpty(err1))
                    return err1;

                var err2 = AssetDatabase.MoveAsset(tmp, to);
                return err2 ?? "";
            }

            return AssetDatabase.MoveAsset(from, to);
        }

        private static void EnsureFolderRecursive(string folderPath)
        {
            var p = NormalizeSlashes(folderPath).TrimEnd('/');
            if (p == "Assets" || AssetDatabase.IsValidFolder(p))
                return;

            var parent = NormalizeSlashes(Path.GetDirectoryName(p) ?? "Assets").TrimEnd('/');
            var name = Path.GetFileName(p);

            EnsureFolderRecursive(parent);

            if (!AssetDatabase.IsValidFolder(p))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static string NormalizeSlashes(string s) => (s ?? "").Replace('\\', '/');

        private static bool IsUnder(string parent, string path)
        {
            parent = NormalizeSlashes(parent).TrimEnd('/');
            path = NormalizeSlashes(path);
            return path.Equals(parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSegment(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = s.Trim().ToLowerInvariant();

            foreach (var c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c.ToString(), "");
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