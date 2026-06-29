using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Stores
{
    [Serializable, Preserve]
    public class Manifest
    {
        public int Version;
        public string RegistryHash;
        public List<Entry> Types;
    }

    [Serializable, Preserve]
    public class Entry
    {
        public int Id;
        public string Key;
        public string TypeName;
        public string AssemblyName;
        public string AssemblyQualifiedName;
        public string CreatedAt;
    }
    
    public static class RuntimeComponentTypeRegistry
    {
        public const string DEFAULT_MANIFEST_FILE_NAME = "runtime_component_types.json";
        public const int CURRENT_MANIFEST_VERSION = 2;

        private static readonly List<Type> _typesById = new();
        private static readonly Dictionary<Type, uint> _idByType = new();
        private static readonly Dictionary<uint, Type> _typeById = new();

        public static bool IsInitialized { get; private set; }
        public static int Count => _typeById.Count;
        public static string RegistryHash { get; private set; }
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

        public static uint GetId(this Type type)
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

        public static Type GetRegisteredType(this uint id)
        {
            if (!IsInitialized)
                throw new InvalidOperationException("RuntimeComponentTypeRegistry is not initialized. Call InitializeFromJson/InitializeFromStreamingAssets first.");

            if (!_typeById.TryGetValue(id, out var t))
                throw new KeyNotFoundException($"Unknown type id: {id}");

            return t;
        }
        
        public static Entry CreateEntry(int id, Type type, string createdAt = null)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (string.IsNullOrWhiteSpace(type.AssemblyQualifiedName))
                throw new InvalidOperationException($"Runtime component type has no assembly-qualified name: {type.FullName}");

            return new Entry
            {
                Id = id,
                Key = GetKey(type),
                TypeName = type.FullName,
                AssemblyName = type.Assembly.GetName().Name,
                AssemblyQualifiedName = type.AssemblyQualifiedName,
                CreatedAt = createdAt
            };
        }

        public static string GetKey(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var key = type.GetCustomAttribute<RuntimeComponentKeyAttribute>(inherit: false)?.Key;
            if (!string.IsNullOrWhiteSpace(key))
                return key.Trim();

            return $"{type.Assembly.GetName().Name}:{type.FullName}";
        }

        public static string CalculateRegistryHash(IEnumerable<Entry> entries)
        {
            var builder = new StringBuilder();
            foreach (var entry in entries.Where(e => e != null).OrderBy(e => e.Id))
            {
                builder
                    .Append(entry.Id)
                    .Append('|')
                    .Append(NormalizeHashPart(entry.Key))
                    .Append('|')
                    .Append(NormalizeHashPart(entry.AssemblyName))
                    .Append('|')
                    .Append(NormalizeHashPart(entry.TypeName))
                    .Append('\n');
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var hex = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++)
                hex.Append(hash[i].ToString("x2"));

            return hex.ToString();
        }

        private static void InitializeFromManifest(Manifest manifest)
        {
            _typesById.Clear();
            _idByType.Clear();
            _typeById.Clear();

            var ordered = NormalizeEntries(manifest);
            var calculatedHash = CalculateRegistryHash(ordered);
            if (!string.IsNullOrWhiteSpace(manifest.RegistryHash) && !string.Equals(manifest.RegistryHash, calculatedHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Runtime component registry hash mismatch. Manifest={manifest.RegistryHash}, calculated={calculatedHash}.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var e in ordered)
            {
                if (e.Id < 0)
                    throw new InvalidOperationException($"Runtime component registry entry has negative id: {e.Id}.");
                if (string.IsNullOrWhiteSpace(e.Key))
                    throw new InvalidOperationException($"Runtime component registry entry {e.Id} has empty key.");
                if (string.IsNullOrWhiteSpace(e.TypeName) && string.IsNullOrWhiteSpace(e.AssemblyQualifiedName))
                    throw new InvalidOperationException($"Runtime component registry entry {e.Id} has no type name.");

                if (!seenKeys.Add(e.Key))
                    throw new InvalidOperationException($"Runtime component registry has duplicate key: {e.Key}.");

                var type = ResolveType(e);
                if (type == null)
                    throw new TypeLoadException($"Failed to resolve runtime component type for entry id={e.Id}, key={e.Key}, type={e.TypeName}.");
                if (!seen.Add(type.AssemblyQualifiedName))
                    throw new InvalidOperationException($"Runtime component registry has duplicate type: {type.FullName}.");

                var id = (uint)e.Id;
                EnsureTypeSlot(e.Id);
                if (_typesById[e.Id] != null)
                    throw new InvalidOperationException($"Runtime component registry has duplicate id: {e.Id}.");

                _typesById[e.Id] = type;
                _idByType[type] = id;
                _typeById[id] = type;
            }

            RegistryHash = calculatedHash;
            IsInitialized = true;
        }

        private static List<Entry> NormalizeEntries(Manifest manifest)
        {
            var entries = manifest.Types.Where(e => e != null).ToList();
            var version = manifest.Version <= 0 ? 1 : manifest.Version;
            if (version < CURRENT_MANIFEST_VERSION)
            {
                for (var i = 0; i < entries.Count; i++)
                    entries[i].Id = i;
            }

            foreach (var entry in entries)
            {
                var type = ResolveType(entry);
                if (type == null)
                    continue;

                if (string.IsNullOrWhiteSpace(entry.Key))
                    entry.Key = GetKey(type);
                if (string.IsNullOrWhiteSpace(entry.TypeName))
                    entry.TypeName = type.FullName;
                if (string.IsNullOrWhiteSpace(entry.AssemblyName))
                    entry.AssemblyName = type.Assembly.GetName().Name;
                if (string.IsNullOrWhiteSpace(entry.AssemblyQualifiedName))
                    entry.AssemblyQualifiedName = type.AssemblyQualifiedName;
            }

            return entries.OrderBy(e => e.Id).ToList();
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

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.IsNullOrWhiteSpace(entry.AssemblyName) && !string.Equals(asm.GetName().Name, entry.AssemblyName, StringComparison.Ordinal))
                    continue;

                var t = asm.GetType(fullName, throwOnError: false);
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

        private static string NormalizeHashPart(string value) => value?.Trim() ?? string.Empty;

        private static void EnsureTypeSlot(int id)
        {
            while (_typesById.Count <= id)
                _typesById.Add(null);
        }
    }
}
