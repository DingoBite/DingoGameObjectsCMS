#if UNITY_EDITOR && NEWTONSOFT_EXISTS
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Modding.Editor
{
    public static class ModImporter
    {
        private const string GAME_ASSETS_ROOT = "Assets/GameAssets";
        private const bool COPY_JSON_FILES_INTO_PROJECT = false;
        private const bool UPDATE_EXISTING_ASSETS = true;

        [MenuItem("Assets/Game Assets/Import Mod From Folder...", false, 2300)]
        private static void ImportModFromFolderMenu()
        {
            var dstModRoot = GetSelectedModRoot();
            if (string.IsNullOrEmpty(dstModRoot))
            {
                Debug.LogError($"Select a folder inside {GAME_ASSETS_ROOT}/<mod>.");
                return;
            }

            var srcFolderAbs = EditorUtility.OpenFolderPanel("Select exported mod folder", "", "");
            if (string.IsNullOrEmpty(srcFolderAbs) || !Directory.Exists(srcFolderAbs))
                return;

            var dstModRootAbs = UnityPathToAbsolute(dstModRoot);

            CopyNonMetaFiles(srcFolderAbs, dstModRootAbs, includeJson: COPY_JSON_FILES_INTO_PROJECT);

            var importedAssets = new List<ScriptableObject>(256);

            try
            {
                EditorUtility.DisplayProgressBar("Mod Import", "Scanning JSON...", 0f);

                var jsonFiles = Directory.EnumerateFiles(srcFolderAbs, "*.json", SearchOption.AllDirectories).ToList();
                for (var i = 0; i < jsonFiles.Count; i++)
                {
                    var jsonPathAbs = jsonFiles[i];

                    if (!LooksLikeAssetJson(jsonPathAbs))
                        continue;

                    var rel = Path.GetRelativePath(srcFolderAbs, jsonPathAbs);
                    var relAsset = Path.ChangeExtension(rel, ".asset");
                    var dstUnityPath = Normalize($"{dstModRoot}/{relAsset}");
                    var dstAbsPath = UnityPathToAbsolute(dstUnityPath);

                    Directory.CreateDirectory(Path.GetDirectoryName(dstAbsPath)!);

                    EditorUtility.DisplayProgressBar("Mod Import", $"Importing {rel}", (float)(i + 1) / Math.Max(1, jsonFiles.Count));

                    var json = File.ReadAllText(jsonPathAbs);

                    var soType = ResolveRootTypeFromJson(json) ?? typeof(GameAsset);

                    var so = CreateOrUpdateAsset(dstUnityPath, soType, json);
                    if (so != null)
                        importedAssets.Add(so);
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            var byGuid = BuildGuidIndex(importedAssets);

            foreach (var a in importedAssets)
            {
                PatchAssetObjectReferences(a, byGuid);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"Mod import complete: {dstModRoot}");
        }

        [MenuItem("Assets/Game Assets/Import Mod From Folder...", true)]
        private static bool ValidateImportModFromFolderMenu() => !string.IsNullOrEmpty(GetSelectedModRoot());

        private static ScriptableObject CreateOrUpdateAsset(string unityAssetPath, Type soType, string json)
        {
            unityAssetPath = Normalize(unityAssetPath);

            ScriptableObject asset = null;

            if (UPDATE_EXISTING_ASSETS)
                asset = AssetDatabase.LoadAssetAtPath(unityAssetPath, soType) as ScriptableObject;

            if (asset == null)
            {
                asset = ScriptableObject.CreateInstance(soType);
                AssetDatabase.CreateAsset(asset, unityAssetPath);
            }

            JsonConvert.PopulateObject(json, asset, GameAssetJsonRuntime.Settings);
            SubAssetFixer.RebuildSubAssets(
                root: asset,
                rootAssetPath: unityAssetPath,
                clearExistingSubAssets: true
            );
            
            EditorUtility.SetDirty(asset);
            return asset;
        }

        private static Type ResolveRootTypeFromJson(string json)
        {
            try
            {
                var jo = JObject.Parse(json);
                var typeToken = jo["$type"]?.Value<string>();
                if (string.IsNullOrWhiteSpace(typeToken))
                    return typeof(GameAsset);

                SplitTypeName(typeToken, out var typeName, out var assemblyName);

                var binder = GameAssetJsonRuntime.Settings.SerializationBinder;
#if NEWTONSOFT_JSON_ISERIALIZATIONBINDER
                return binder.BindToType(assemblyName, typeName) ?? typeof(DingoGameObjectsCMS.AssetObjects.GameAsset);
#else
                return binder.BindToType(assemblyName, typeName) ?? typeof(GameAsset);
#endif
            }
            catch
            {
                return typeof(GameAsset);
            }
        }

        private static void SplitTypeName(string s, out string typeName, out string assemblyName)
        {
            typeName = s;
            assemblyName = null;

            var comma = s.IndexOf(',');
            if (comma < 0)
                return;

            typeName = s.Substring(0, comma).Trim();
            assemblyName = s.Substring(comma + 1).Trim();
        }


        private static bool LooksLikeAssetJson(string jsonPathAbs)
        {
            try
            {
                var text = File.ReadAllText(jsonPathAbs);

                if (!text.Contains("\"Key\"") || !text.Contains("\"GUID\""))
                    return false;

                var jo = JObject.Parse(text);
                return jo["Key"] is JObject && jo["GUID"] != null;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<Hash128, GameAssetScriptableObject> BuildGuidIndex(List<ScriptableObject> assets)
        {
            var dict = new Dictionary<Hash128, GameAssetScriptableObject>();

            foreach (var a in assets)
            {
                if (a is not GameAssetScriptableObject ga)
                    continue;

                if (!ga.GUID.isValid)
                    continue;

                dict[ga.GUID] = ga;
            }

            return dict;
        }

        private static void PatchAssetObjectReferences(ScriptableObject root, Dictionary<Hash128, GameAssetScriptableObject> byGuid)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            PatchObjectGraph(root, byGuid, visited);

            EditorUtility.SetDirty(root);
        }

        private static void PatchObjectGraph(object obj, Dictionary<Hash128, GameAssetScriptableObject> byGuid, HashSet<object> visited)
        {
            if (obj == null)
                return;
            if (!visited.Add(obj))
                return;

            var t = obj.GetType();

            if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal))
                return;

            if (obj is GameAssetScriptableObject gaRef)
                return;

            if (obj is IEnumerable enumerable && t != typeof(string))
            {
                if (obj is IList list)
                {
                    for (var i = 0; i < list.Count; i++)
                    {
                        var el = list[i];
                        if (TryResolveRef(el, byGuid, out var resolved))
                            list[i] = resolved;
                        else
                            PatchObjectGraph(el, byGuid, visited);
                    }

                    return;
                }

                foreach (var el in enumerable)
                {
                    PatchObjectGraph(el, byGuid, visited);
                }

                return;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var f in t.GetFields(flags))
            {
                if (f.IsStatic)
                    continue;

                if (!FieldIsSerializable(f))
                    continue;

                var v = f.GetValue(obj);

                if (TryResolveRef(v, byGuid, out var resolved))
                {
                    f.SetValue(obj, resolved);
                    continue;
                }

                if (v is Array arr)
                {
                    for (var i = 0; i < arr.Length; i++)
                    {
                        var el = arr.GetValue(i);
                        if (TryResolveRef(el, byGuid, out var resEl))
                            arr.SetValue(resEl, i);
                        else
                            PatchObjectGraph(el, byGuid, visited);
                    }

                    continue;
                }

                PatchObjectGraph(v, byGuid, visited);
            }
        }

        private static bool TryResolveRef(object value, Dictionary<Hash128, GameAssetScriptableObject> byGuid, out object resolved)
        {
            resolved = null;
            if (value is not GameAssetScriptableObject ga)
                return false;

            var id = ga.GUID;
            if (!id.isValid)
                return false;

            if (byGuid.TryGetValue(id, out var real))
            {
                resolved = real;
                return true;
            }

            return false;
        }

        private static bool FieldIsSerializable(FieldInfo f)
        {
            if (f.IsPublic)
                return true;
            if (f.GetCustomAttribute<SerializeField>() != null)
                return true;
            if (f.GetCustomAttribute<SerializeReference>() != null)
                return true;
            if (f.GetCustomAttribute<JsonPropertyAttribute>() != null)
                return true;
            return false;
        }

        private static void CopyNonMetaFiles(string srcRootAbs, string dstRootAbs, bool includeJson)
        {
            srcRootAbs = Path.GetFullPath(srcRootAbs);
            dstRootAbs = Path.GetFullPath(dstRootAbs);

            foreach (var srcAbs in Directory.EnumerateFiles(srcRootAbs, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(srcAbs).ToLowerInvariant();
                if (ext == ".meta")
                    continue;

                if (!includeJson && ext == ".json")
                    continue;

                var rel = Path.GetRelativePath(srcRootAbs, srcAbs);
                var dstAbs = Path.Combine(dstRootAbs, rel);

                Directory.CreateDirectory(Path.GetDirectoryName(dstAbs)!);

                FileUtil.CopyFileOrDirectory(Normalize(srcAbs), Normalize(dstAbs));
            }
        }

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

        private static string UnityPathToAbsolute(string unityPath)
        {
            unityPath = Normalize(unityPath);

            var projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, unityPath));
        }

        private static string Normalize(string s) => (s ?? "").Replace('\\', '/');

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
#endif