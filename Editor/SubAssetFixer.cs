#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor
{
    internal static class SubAssetFixer
    {
        public static void RebuildSubAssets(ScriptableObject root, string rootAssetPath, bool clearExistingSubAssets)
        {
            if (clearExistingSubAssets)
            {
                var all = AssetDatabase.LoadAllAssetsAtPath(rootAssetPath);
                foreach (var o in all)
                {
                    if (o == null)
                        continue;
                    if (ReferenceEquals(o, root))
                        continue;

                    UnityEngine.Object.DestroyImmediate(o, allowDestroyingAssets: true);
                }
            }

            var toAttach = CollectNonPersistentScriptableObjects(root);

            foreach (var so in toAttach)
            {
                if (so == null)
                    continue;
                if (ReferenceEquals(so, root))
                    continue;

                if (EditorUtility.IsPersistent(so))
                    continue;

                if (string.IsNullOrWhiteSpace(so.name))
                    so.name = so.GetType().Name;

                so.name = MakeUniqueName(rootAssetPath, so.name);

                AssetDatabase.AddObjectToAsset(so, rootAssetPath);
                EditorUtility.SetDirty(so);
            }

            EditorUtility.SetDirty(root);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(rootAssetPath, ImportAssetOptions.ForceSynchronousImport);
        }

        private static List<ScriptableObject> CollectNonPersistentScriptableObjects(object root)
        {
            var result = new List<ScriptableObject>();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

            void Walk(object obj)
            {
                if (obj == null)
                    return;
                if (!visited.Add(obj))
                    return;

                if (obj is ScriptableObject so)
                {
                    if (!EditorUtility.IsPersistent(so))
                        result.Add(so);
                }

                var t = obj.GetType();
                if (t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal))
                    return;

                if (obj is IEnumerable en && t != typeof(string))
                {
                    foreach (var el in en)
                        Walk(el);
                    return;
                }

                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var f in t.GetFields(flags))
                {
                    if (f.IsStatic)
                        continue;

                    if (!knowSerializableField(f))
                        continue;

                    Walk(f.GetValue(obj));
                }
            }

            Walk(root);

            return result;

            static bool knowSerializableField(FieldInfo f)
            {
                if (f.IsPublic)
                    return true;
                if (f.GetCustomAttribute<SerializeField>() != null)
                    return true;
                if (f.GetCustomAttribute<SerializeReference>() != null)
                    return true;
                if (f.GetCustomAttribute<Newtonsoft.Json.JsonPropertyAttribute>() != null)
                    return true;
                return false;
            }
        }

        private static string MakeUniqueName(string assetPath, string baseName)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(assetPath))
            {
                if (o != null && !string.IsNullOrWhiteSpace(o.name))
                    used.Add(o.name);
            }

            if (!used.Contains(baseName))
                return baseName;

            for (var i = 1; i < 100000; i++)
            {
                var n = $"{baseName}_{i}";
                if (!used.Contains(n))
                    return n;
            }

            return $"{baseName}_{Guid.NewGuid():N}";
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
#endif