using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        public const string PATH = RuntimeComponentTypeRegistry.DEFAULT_MANIFEST_FILE_NAME;
        
        public int callbackOrder => 0;

        private static string ManifestPath => Path.Combine(Application.streamingAssetsPath, PATH);
        
        public void OnPreprocessBuild(BuildReport report) => Generate();

        [MenuItem("Tools/Runtime Types/Regenerate Manifest")]
        public static void Regenerate()
        {
            if (File.Exists(ManifestPath))
                File.Delete(ManifestPath);
            Generate();
        }
        
        [MenuItem("Tools/Runtime Types/Generate Manifest")]
        public static void Generate()
        {
            var path = ManifestPath;
            var manifest = File.Exists(path) ? JsonUtility.FromJson<Manifest>(File.ReadAllText(path)) : new Manifest { Version = 1, Types = new List<Entry>() };

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
            }).Where(t => t != null && !t.IsAbstract && typeof(GameRuntimeComponent).IsAssignableFrom(t));

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