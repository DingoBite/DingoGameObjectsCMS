using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Stores
{
    [Serializable, Preserve]
    public class Manifest
    {
        public int Version;
        public string RegistryHash;
        public List<Entry> Types;
        public List<int> ReservedIds;
    }

    [Serializable, Preserve]
    public class Entry
    {
        public int Id;
        [NonSerialized] public Type RuntimeType;
    }

    public static class RuntimeComponentTypeRegistry
    {
        public const int CURRENT_MANIFEST_VERSION = 3;

        private static readonly List<Type> _typesById = new();
        private static readonly Dictionary<Type, uint> _idByType = new();
        private static readonly Dictionary<uint, Type> _typeById = new();

        public static bool IsInitialized { get; private set; }
        public static ulong InitializationVersion { get; private set; }
        public static int Count => _typeById.Count;
        public static string RegistryHash { get; private set; }
        public static IReadOnlyList<Type> TypesById => _typesById;

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
            {
                throw new InvalidOperationException("RuntimeComponentTypeRegistry is not initialized. Call the generated compiled registry initializer first.");
            }

            if (!_idByType.TryGetValue(type, out var id))
            {
                throw new KeyNotFoundException($"Type is not present in manifest: {type.FullName}");
            }

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
            {
                throw new InvalidOperationException("RuntimeComponentTypeRegistry is not initialized. Call the generated compiled registry initializer first.");
            }

            if (!_typeById.TryGetValue(id, out var type))
            {
                throw new KeyNotFoundException($"Unknown type id: {id}");
            }

            return type;
        }

        public static Entry CreateEntry(int id, Type type)
        {
            ValidateRuntimeComponentType(type);
            return new Entry
            {
                Id = id,
                RuntimeType = type,
            };
        }

        public static string CalculateRegistryHash(
            IEnumerable<Entry> entries,
            IEnumerable<int> reservedIds)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            var builder = new StringBuilder();
            foreach (var entry in entries.Where(entry => entry != null).OrderBy(entry => entry.Id))
            {
                var type = entry.RuntimeType
                           ?? throw new TypeLoadException($"Active compiled runtime component entry id={entry.Id} requires RuntimeType = typeof(T).");
                builder.Append(entry.Id)
                    .Append('|')
                    .Append(type.Assembly.GetName().Name)
                    .Append('|')
                    .Append(type.FullName)
                    .Append('\n');
            }

            if (reservedIds != null)
            {
                foreach (var reservedId in reservedIds.Distinct().OrderBy(id => id))
                {
                    builder.Append(reservedId).Append("|<reserved>\n");
                }
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            var hex = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++)
            {
                hex.Append(hash[i].ToString("x2"));
            }

            return hex.ToString();
        }

        public static void InitializeFromManifest(Manifest manifest)
        {
            if (manifest?.Types == null)
            {
                throw new ArgumentException("A compiled runtime component manifest with a type table is required.", nameof(manifest));
            }
            if (manifest.Version != CURRENT_MANIFEST_VERSION)
            {
                throw new InvalidOperationException($"Runtime component manifest version {manifest.Version} does not match required version {CURRENT_MANIFEST_VERSION}.");
            }

            _typesById.Clear();
            _idByType.Clear();
            _typeById.Clear();

            var reservedIds = manifest.ReservedIds ?? new List<int>();
            var reservedIdSet = ValidateReservedIds(reservedIds);
            var ordered = manifest.Types
                .Where(entry => entry != null)
                .OrderBy(entry => entry.Id)
                .ToArray();
            var calculatedHash = CalculateRegistryHash(ordered, reservedIdSet);
            if (!string.IsNullOrWhiteSpace(manifest.RegistryHash)
                && !string.Equals(manifest.RegistryHash, calculatedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Runtime component registry hash mismatch. Manifest={manifest.RegistryHash}, calculated={calculatedHash}.");
            }

            var seenTypes = new HashSet<Type>();
            var seenIds = new HashSet<int>();
            for (var i = 0; i < ordered.Length; i++)
            {
                var entry = ordered[i];
                if (entry.Id < 0)
                {
                    throw new InvalidOperationException($"Runtime component registry entry has negative id: {entry.Id}.");
                }
                if (!seenIds.Add(entry.Id))
                {
                    throw new InvalidOperationException($"Runtime component registry has duplicate id: {entry.Id}.");
                }
                if (reservedIdSet.Contains(entry.Id))
                {
                    throw new InvalidOperationException($"Runtime component registry id {entry.Id} is both active and reserved.");
                }

                var type = entry.RuntimeType
                           ?? throw new TypeLoadException($"Active compiled runtime component entry id={entry.Id} requires RuntimeType = typeof(T).");
                ValidateRuntimeComponentType(type);
                if (!seenTypes.Add(type))
                {
                    throw new InvalidOperationException($"Runtime component registry has duplicate type: {type.FullName}.");
                }

                EnsureTypeSlot(entry.Id);
                var id = (uint)entry.Id;
                _typesById[entry.Id] = type;
                _idByType.Add(type, id);
                _typeById.Add(id, type);
            }

            foreach (var reservedId in reservedIdSet)
            {
                EnsureTypeSlot(reservedId);
            }

            RegistryHash = calculatedHash;
            IsInitialized = true;
            InitializationVersion++;
        }

        private static HashSet<int> ValidateReservedIds(IReadOnlyList<int> reservedIds)
        {
            var result = new HashSet<int>();
            for (var i = 0; i < reservedIds.Count; i++)
            {
                var id = reservedIds[i];
                if (id < 0)
                {
                    throw new InvalidOperationException($"Runtime component registry has negative reserved id: {id}.");
                }
                if (!result.Add(id))
                {
                    throw new InvalidOperationException($"Runtime component registry has duplicate reserved id: {id}.");
                }
            }

            return result;
        }

        private static void ValidateRuntimeComponentType(Type type)
        {
            if (type == null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            if (type.IsAbstract
                || !typeof(GameRuntimeComponent).IsAssignableFrom(type))
            {
                throw new ArgumentException($"Runtime component type '{type.FullName}' must be a concrete {nameof(GameRuntimeComponent)}.", nameof(type));
            }
        }

        private static void EnsureTypeSlot(int id)
        {
            while (_typesById.Count <= id)
            {
                _typesById.Add(null);
            }
        }
    }
}
