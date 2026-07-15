using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
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
            Generate();
        }
        
        [MenuItem("Tools/Runtime Types/Generate Manifest")]
        public static void Generate()
        {
            var path = ManifestPath;
            var manifest = File.Exists(path) ? JsonUtility.FromJson<Manifest>(File.ReadAllText(path)) : CreateEmptyManifest();

            var types = CollectRuntimeComponentTypes();
            var entries = NormalizeExistingEntries(manifest);
            var reservedIds = NormalizeReservedIds(manifest);
            var usedIds = new HashSet<int>(reservedIds);
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);
            var usedTypes = new HashSet<string>(StringComparer.Ordinal);
            var nextId = -1;
            var nextEntries = new List<Entry>();

            foreach (var entry in entries.OrderBy(e => e.Id))
            {
                var type = ResolveType(entry);
                if (type == null || !IsRuntimeComponentType(type))
                {
                    if (!reservedIds.Add(entry.Id))
                        throw new InvalidOperationException($"Runtime component id {entry.Id} is already reserved.");
                    usedIds.Add(entry.Id);
                    continue;
                }

                var nextEntry = RuntimeComponentTypeRegistry.CreateEntry(entry.Id, type, entry.CreatedAt);
                AddEntry(nextEntries, nextEntry, usedIds, usedKeys, usedTypes);
                nextId = Math.Max(nextId, nextEntry.Id);
            }

            nextId = usedIds.Count == 0 ? -1 : usedIds.Max();
            foreach (var type in types)
            {
                if (string.IsNullOrWhiteSpace(type.AssemblyQualifiedName) || usedTypes.Contains(type.AssemblyQualifiedName))
                    continue;

                var entry = RuntimeComponentTypeRegistry.CreateEntry(++nextId, type, DateTime.UtcNow.ToString("O"));
                AddEntry(nextEntries, entry, usedIds, usedKeys, usedTypes);
            }

            manifest.Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION;
            manifest.Types = nextEntries.OrderBy(e => e.Id).ToList();
            manifest.ReservedIds = reservedIds.OrderBy(id => id).ToList();
            manifest.RegistryHash = RuntimeComponentTypeRegistry.CalculateRegistryHash(manifest.Types, manifest.ReservedIds);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonUtility.ToJson(manifest, true));
            Debug.Log($"Saved manifest: {path} (count={manifest.Types.Count}, reserved={manifest.ReservedIds.Count}, hash={manifest.RegistryHash})");
        }

        private static Manifest CreateEmptyManifest() => new()
        {
            Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION,
            Types = new List<Entry>(),
            ReservedIds = new List<int>(),
        };

        private static IReadOnlyList<Type> CollectRuntimeComponentTypes()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(IsPlayerRuntimeAssembly)
                .SelectMany(a =>
                {
                    try
                    {
                        return a.GetTypes();
                    }
                    catch
                    {
                        return Array.Empty<Type>();
                    }
                })
                .Where(IsRuntimeComponentType)
                .OrderBy(RuntimeComponentTypeRegistry.GetKey)
                .ThenBy(t => t.FullName)
                .ToArray();
        }

        private static bool IsRuntimeComponentType(Type type)
        {
            if (type == null || type.IsAbstract || type == typeof(GameRuntimeComponent))
                return false;
            if (!typeof(GameRuntimeComponent).IsAssignableFrom(type))
                return false;
            if (typeof(ICommandLogic).IsAssignableFrom(type))
                return false;
            if (!IsPlayerRuntimeAssembly(type.Assembly))
                return false;
            return !ContainsNamespaceSegment(type.Namespace, "Editor")
                   && !ContainsNamespaceSegment(type.Namespace, "Tests")
                   && !ContainsNamespaceSegment(type.Namespace, "Examples")
                   && !ContainsNamespaceSegment(type.Namespace, "Samples");
        }

        private static bool IsPlayerRuntimeAssembly(System.Reflection.Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic)
                return false;
            var name = assembly.GetName().Name;
            if (string.IsNullOrWhiteSpace(name))
                return false;
            return !name.EndsWith("-Editor", StringComparison.OrdinalIgnoreCase)
                   && !name.EndsWith(".Editor", StringComparison.OrdinalIgnoreCase)
                   && name.IndexOf("Test", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool ContainsNamespaceSegment(string value, string segment)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;
            var parts = value.Split('.');
            for (var i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static List<Entry> NormalizeExistingEntries(Manifest manifest)
        {
            var entries = manifest?.Types?.Where(e => e != null).ToList() ?? new List<Entry>();
            var version = manifest == null || manifest.Version <= 0 ? 1 : manifest.Version;
            if (version >= RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION)
                return entries;

            for (var i = 0; i < entries.Count; i++)
                entries[i].Id = i;

            return entries;
        }

        private static HashSet<int> NormalizeReservedIds(Manifest manifest)
        {
            var source = manifest?.ReservedIds ?? new List<int>();
            var result = new HashSet<int>();
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] < 0)
                    throw new InvalidOperationException($"Runtime component manifest has negative reserved id: {source[i]}.");
                if (!result.Add(source[i]))
                    throw new InvalidOperationException($"Runtime component manifest has duplicate reserved id: {source[i]}.");
            }
            return result;
        }

        private static void AddEntry(
            List<Entry> entries,
            Entry entry,
            HashSet<int> usedIds,
            HashSet<string> usedKeys,
            HashSet<string> usedTypes)
        {
            if (entry.Id < 0)
                throw new InvalidOperationException($"Runtime component manifest entry has negative id: {entry.Id}.");
            if (string.IsNullOrWhiteSpace(entry.Key))
                throw new InvalidOperationException($"Runtime component manifest entry {entry.Id} has empty key.");
            if (string.IsNullOrWhiteSpace(entry.AssemblyQualifiedName))
                throw new InvalidOperationException($"Runtime component manifest entry {entry.Id} has empty assembly-qualified name.");
            if (!usedIds.Add(entry.Id))
                throw new InvalidOperationException($"Runtime component manifest has duplicate id: {entry.Id}.");
            if (!usedKeys.Add(entry.Key))
                throw new InvalidOperationException($"Runtime component manifest has duplicate key: {entry.Key}.");
            if (!usedTypes.Add(entry.AssemblyQualifiedName))
                throw new InvalidOperationException($"Runtime component manifest has duplicate type: {entry.AssemblyQualifiedName}.");

            entries.Add(entry);
        }

        private static Type ResolveType(Entry entry)
        {
            if (entry == null)
                return null;

            if (!string.IsNullOrWhiteSpace(entry.TypeName) && !string.IsNullOrWhiteSpace(entry.AssemblyName))
            {
                var t = Type.GetType($"{entry.TypeName}, {entry.AssemblyName}", throwOnError: false);
                if (t != null)
                    return t;
            }

            if (!string.IsNullOrWhiteSpace(entry.AssemblyQualifiedName))
            {
                var t = Type.GetType(entry.AssemblyQualifiedName, throwOnError: false);
                if (t != null)
                    return t;
            }

            var fullName = ResolveFullName(entry);
            if (string.IsNullOrWhiteSpace(fullName))
                return null;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrWhiteSpace(entry.AssemblyName) && !string.Equals(assembly.GetName().Name, entry.AssemblyName, StringComparison.Ordinal))
                    continue;

                var t = assembly.GetType(fullName, throwOnError: false);
                if (t != null)
                    return t;
            }

            return null;
        }

        private static string ResolveFullName(Entry entry)
        {
            if (!string.IsNullOrWhiteSpace(entry.TypeName))
                return entry.TypeName.Trim();
            if (string.IsNullOrWhiteSpace(entry.AssemblyQualifiedName))
                return null;

            return entry.AssemblyQualifiedName.Split(',')[0].Trim();
        }
    }
}
