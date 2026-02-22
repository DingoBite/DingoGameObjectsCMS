using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor
{
    public class RuntimeComponentTypeManifestGenerator : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report) => Generate();

        [MenuItem("Tools/Runtime Types/Generate Manifest")]
        public static void Generate()
        {
            const string path = "Assets/StreamingAssets/runtime_component_types.json";
            var manifest = File.Exists(path) ? JsonUtility.FromJson<Manifest>(File.ReadAllText(path)) : new Manifest { Version = 1, Types = new List<Entry>() };

            var targetBase = typeof(GameRuntimeComponent);
            var all = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch
                {
                    return Array.Empty<Type>();
                }
            }).Where(t => t != null && !t.IsAbstract && (typeof(GameRuntimeComponent).IsAssignableFrom(t) || typeof(GameAssetParameter).IsAssignableFrom(t)));

            var maxOrder = manifest.Types.Count == 0 ? -1 : manifest.Types.Max(e => e.Order);
            foreach (var t in all)
            {
                manifest.Types.Add(new Entry
                {
                    AssemblyQualifiedName = t.AssemblyQualifiedName,
                    CreatedAt = DateTime.UtcNow.ToString("O"),
                    Order = ++maxOrder
                });
            }

            manifest.Types = manifest.Types.OrderBy(e => e.Order).ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonUtility.ToJson(manifest, true));
            Debug.Log($"Saved manifest: {path} (count={manifest.Types.Count})");
        }
    }
}