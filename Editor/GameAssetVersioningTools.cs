#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Editor
{
    public static class GameAssetVersioningTools
    {
        private const string DefaultRoot = "Assets/GameAssets";
        private const string CanonicalAssetHint = "Assets/GameAssets/<mod>/<type>/<key>/<key>@<version>.asset";

        private readonly struct VersionAssetSelection
        {
            public readonly string AssetPath;
            public readonly string Mod;
            public readonly string Type;
            public readonly string Key;
            public readonly string Version;

            public VersionAssetSelection(string assetPath, string mod, string type, string key, string version)
            {
                AssetPath = assetPath;
                Mod = mod;
                Type = type;
                Key = key;
                Version = version;
            }
        }

        private enum BumpKind
        {
            Patch,
            Minor,
            Major
        }

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Asset Version (Patch)", false, 2000)]
        private static void DuplicateSelectedAssetsPatch() => DuplicateSelectedVersionAssets(BumpKind.Patch);

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Asset Version (Patch)", true)]
        private static bool ValidateDuplicateSelectedAssetsPatch() => HasAnySelectedVersionAsset();

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Asset Version (Minor)", false, 2001)]
        private static void DuplicateSelectedAssetsMinor() => DuplicateSelectedVersionAssets(BumpKind.Minor);

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Asset Version (Minor)", true)]
        private static bool ValidateDuplicateSelectedAssetsMinor() => HasAnySelectedVersionAsset();

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Asset Version (Major)", false, 2002)]
        private static void DuplicateSelectedAssetsMajor() => DuplicateSelectedVersionAssets(BumpKind.Major);

        [MenuItem("Assets/Game Assets/Versioning/Duplicate Asset Version (Major)", true)]
        private static bool ValidateDuplicateSelectedAssetsMajor() => HasAnySelectedVersionAsset();

        private static bool HasAnySelectedVersionAsset()
        {
            return GetSelectedVersionAssets().Count > 0;
        }

        private static List<VersionAssetSelection> GetSelectedVersionAssets()
        {
            var result = new List<VersionAssetSelection>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var guid in Selection.assetGUIDs)
            {
                var path = NormalizeSlashes(AssetDatabase.GUIDToAssetPath(guid));
                if (string.IsNullOrEmpty(path))
                    continue;

                if (!TryBuildSelection(path, out var selection))
                    continue;

                if (seenPaths.Add(selection.AssetPath))
                    result.Add(selection);
            }

            return result;
        }

        private static bool TryBuildSelection(string path, out VersionAssetSelection selection)
        {
            selection = default;

            if (!path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return false;

            if (TryParseCanonicalAssetPath(path, out var canonicalMod, out var canonicalType, out var canonicalKey, out var canonicalVersion))
            {
                if (AssetDatabase.LoadAssetAtPath<AssetObjects.GameAssetScriptableObject>(path) == null)
                    return false;

                selection = new VersionAssetSelection(path, canonicalMod, canonicalType, canonicalKey, canonicalVersion);
                return true;
            }

            return false;
        }

        private static void DuplicateSelectedVersionAssets(BumpKind bump)
        {
            var sources = GetSelectedVersionAssets();
            if (sources.Count == 0)
            {
                Debug.LogError($"Select a versioned asset: {CanonicalAssetHint}.");
                return;
            }

            foreach (var source in sources)
            {
                var newVersion = BumpSemver(source.Version, bump);
                if (string.IsNullOrWhiteSpace(newVersion))
                {
                    Debug.LogError($"Cannot bump version '{source.Version}' for {source.AssetPath}");
                    continue;
                }

                DuplicateAssetAsVersion(source, newVersion);
            }
        }

        private static void DuplicateAssetAsVersion(VersionAssetSelection source, string newVersion)
        {
            var dstDir = $"{DefaultRoot}/{source.Mod}/{source.Type}/{source.Key}".Replace('\\', '/');
            var dstPath = $"{dstDir}/{source.Key}@{newVersion}.asset".Replace('\\', '/');

            if (AssetDatabase.LoadAssetAtPath<Object>(dstPath) != null)
            {
                Debug.LogError($"Destination asset already exists: {dstPath}");
                return;
            }

            EnsureFolderRecursive(dstDir);

            if (!AssetDatabase.CopyAsset(source.AssetPath, dstPath))
            {
                Debug.LogError($"CopyAsset failed:\n  {source.AssetPath}\n  -> {dstPath}");
                return;
            }

            AssetDatabase.ImportAsset(dstPath, ImportAssetOptions.ForceSynchronousImport);

            var soObj = AssetDatabase.LoadAssetAtPath<AssetObjects.GameAssetScriptableObject>(dstPath);
            if (soObj == null)
            {
                Debug.LogError($"Duplicated asset cannot be loaded: {dstPath}");
                return;
            }

            ApplyVersionAndRegenerateGuid(soObj, newVersion);
            AssetDatabase.SaveAssets();
            Debug.Log($"Duplicated {source.Mod}:{source.Type}:{source.Key} {source.Version} -> {newVersion}");
        }

        private static bool TryParseCanonicalAssetPath(string assetPath, out string mod, out string type, out string key, out string version)
        {
            mod = type = key = version = null;

            assetPath = NormalizeSlashes(assetPath);
            var root = NormalizeSlashes(DefaultRoot).TrimEnd('/');
            if (!assetPath.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return false;

            var rel = assetPath[root.Length..].TrimStart('/');
            var parts = rel.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 4 || !parts[^1].EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
                return false;

            mod = parts[0];
            type = parts[1];
            key = parts[2];

            var fileName = Path.GetFileNameWithoutExtension(parts[3]);
            var prefix = key + "@";
            if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            version = fileName[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(version);
        }

        private static void ApplyVersionAndRegenerateGuid(Object asset, string version)
        {
            var so = new SerializedObject(asset);

            var keyProp = so.FindProperty("_key");
            var versionProp = keyProp?.FindPropertyRelative("Version");
            if (versionProp != null)
                versionProp.stringValue = version;

            var guidProp = so.FindProperty("_guid");
            if (guidProp != null)
                guidProp.hash128Value = IdUtils.NewHash128FromGuid();

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(asset);
        }

        private static void EnsureFolderRecursive(string folderPath)
        {
            folderPath = NormalizeSlashes(folderPath).TrimEnd('/');
            if (folderPath == "Assets" || AssetDatabase.IsValidFolder(folderPath))
                return;

            var parent = NormalizeSlashes(Path.GetDirectoryName(folderPath) ?? "Assets").TrimEnd('/');
            var name = Path.GetFileName(folderPath);

            EnsureFolderRecursive(parent);

            if (!AssetDatabase.IsValidFolder(folderPath))
                AssetDatabase.CreateFolder(parent, name);
        }

        private static string NormalizeSlashes(string s) => (s ?? string.Empty).Replace('\\', '/');

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
