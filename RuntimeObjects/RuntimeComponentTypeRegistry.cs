using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    [Serializable, Preserve]
    public class Manifest
    {
        public int Version;
        public List<Entry> Types;
    }

    [Serializable, Preserve]
    public class Entry
    {
        public string AssemblyQualifiedName;
        public string CreatedAt;
        public int Order;
    }
    
    public static class RuntimeComponentTypeRegistry
    {
        public const string DEFAULT_MANIFEST_FILE_NAME = "runtime_component_types.json";

        private static readonly List<Type> _typesById = new();
        private static readonly Dictionary<Type, uint> _idByType = new();
        private static readonly Dictionary<uint, Type> _typeById = new();

        public static bool IsInitialized { get; private set; }
        public static int Count => _typesById.Count;
        public static IReadOnlyList<Type> TypesById => _typesById;
        
        public static void InitializeFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new ArgumentException("Manifest json is empty.", nameof(json));

            var manifest = JsonUtility.FromJson<Manifest>(json);
            if (manifest == null || manifest.Types == null)
                throw new InvalidOperationException("Failed to parse manifest json (Manifest/Types is null).");

            InitializeFromManifest(manifest);
        }
        
        public static IEnumerator InitializeFromStreamingAssets(string fileName = DEFAULT_MANIFEST_FILE_NAME)
        {
            var path = Path.Combine(Application.streamingAssetsPath, fileName);

            if (path.Contains("://") || path.Contains("jar:"))
            {
                using var req = UnityWebRequest.Get(path);
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success)
                    throw new IOException($"Failed to load manifest from StreamingAssets: {path}\n{req.error}");

                InitializeFromJson(req.downloadHandler.text);
            }
            else
            {
                InitializeFromJson(File.ReadAllText(path));
            }
        }

        public static bool TryGetId(Type type, out uint id)
        {
            if (!IsInitialized)
            {
                id = 0;
                return false;
            }

            return _idByType.TryGetValue(type, out id);
        }

        public static uint GetId(Type type)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("RuntimeComponentTypeRegistry is not initialized. Call InitializeFromJson/InitializeFromStreamingAssets first.");

            if (!_idByType.TryGetValue(type, out var id))
                throw new KeyNotFoundException($"Type is not present in manifest: {type.FullName}");

            return id;
        }

        public static bool TryGetType(uint id, out Type type)
        {
            if (!IsInitialized)
            {
                type = null;
                return false;
            }

            return _typeById.TryGetValue(id, out type);
        }

        public static Type GetType(uint id)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("RuntimeComponentTypeRegistry is not initialized. Call InitializeFromJson/InitializeFromStreamingAssets first.");

            if (!_typeById.TryGetValue(id, out var t))
                throw new KeyNotFoundException($"Unknown type id: {id}");

            return t;
        }

        private static void InitializeFromManifest(Manifest manifest)
        {
            _typesById.Clear();
            _idByType.Clear();
            _typeById.Clear();

            var ordered = manifest.Types.Where(e => e != null).OrderBy(e => e.Order).ToList();

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var e in ordered)
            {
                if (string.IsNullOrWhiteSpace(e.AssemblyQualifiedName))
                    throw new InvalidOperationException("Manifest entry has empty AssemblyQualifiedName.");

                if (!seen.Add(e.AssemblyQualifiedName))
                    continue;

                var type = ResolveType(e.AssemblyQualifiedName);
                if (type == null)
                    throw new TypeLoadException($"Failed to resolve type from manifest: {e.AssemblyQualifiedName}");

                var id = (uint)_typesById.Count;

                _typesById.Add(type);
                _idByType[type] = id;
                _typeById[id] = type;
            }

            IsInitialized = true;
        }

        private static Type ResolveType(string assemblyQualifiedName)
        {
            var t = Type.GetType(assemblyQualifiedName, throwOnError: false);
            if (t != null)
                return t;

            var fullName = assemblyQualifiedName.Split(',')[0].Trim();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(fullName, throwOnError: false);
                if (t != null)
                    return t;
            }

            return null;
        }
    }
}