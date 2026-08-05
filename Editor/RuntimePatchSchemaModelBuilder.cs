using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Mathematics;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor
{
    public enum RuntimePatchGeneratedValueKind
    {
        Boolean,
        Byte,
        SByte,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        String,
        Enum,
        Int2,
        Float2,
        Vector2Int,
        RuntimeInstance,
        Hash128,
        RuntimeObjectPatch,
        Struct,
        ListVector2Int,
        List,
    }

    public class RuntimePatchGeneratedMemberDescriptor
    {
        public FieldInfo Field;
        public RuntimePatchGeneratedTypeDescriptor ValueType;
    }

    public class RuntimePatchGeneratedTypeDescriptor
    {
        public Type RuntimeType;
        public Type EnumUnderlyingType;
        public RuntimePatchGeneratedValueKind Kind;
        public RuntimePatchGeneratedTypeDescriptor ElementType;
        public List<RuntimePatchGeneratedMemberDescriptor> Members = new();
    }

    public class RuntimePatchGeneratedFieldDescriptor
    {
        public FieldInfo Field;
        public RuntimePatchGeneratedTypeDescriptor ValueType;
        public RuntimePatchFieldSchema Schema;
    }

    public class RuntimePatchGeneratedComponentDescriptor
    {
        public Type RuntimeType;
        public RuntimePatchComponentSchema Schema;
        public List<RuntimePatchGeneratedFieldDescriptor> Fields = new();

        public bool UsesCustomCodec
        {
            get
            {
                for (var i = 0; i < Fields.Count; i++)
                {
                    if (Fields[i].Schema.Encoding == RuntimePatchFieldEncoding.CustomListVector2Int
                        || Fields[i].Schema.Encoding == RuntimePatchFieldEncoding.CustomList)
                        return true;
                }
                return false;
            }
        }
    }

    public class RuntimePatchSchemaDiscoveryResult
    {
        public string ComponentRegistryHash;
        public List<RuntimePatchGeneratedComponentDescriptor> Components = new();
    }

    public static class RuntimePatchSchemaDiscovery
    {
        public static RuntimePatchSchemaDiscoveryResult Discover(Manifest runtimeManifest)
        {
            if (runtimeManifest == null)
                throw new ArgumentNullException(nameof(runtimeManifest));

            var entries = NormalizeRuntimeEntries(runtimeManifest);
            var calculatedHash = RuntimeComponentTypeRegistry.CalculateRegistryHash(
                runtimeManifest.Types,
                runtimeManifest.ReservedIds);
            if (!string.IsNullOrWhiteSpace(runtimeManifest.RegistryHash)
                && !string.Equals(runtimeManifest.RegistryHash, calculatedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runtime component manifest hash '{runtimeManifest.RegistryHash}' does not match calculated hash '{calculatedHash}'.");
            }

            var entryByType = new Dictionary<Type, Entry>();
            var entryIds = new HashSet<int>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var type = ResolveRuntimeType(entry);
                if (type == null)
                    throw new TypeLoadException($"Active runtime component manifest entry {entry.Id} requires RuntimeType = typeof(T).");
                if (!entryIds.Add(entry.Id))
                    throw new InvalidOperationException($"Runtime component manifest has duplicate id {entry.Id}.");
                if (!entryByType.TryAdd(type, entry))
                    throw new InvalidOperationException($"Runtime component manifest contains duplicate type '{type.FullName}'.");
            }

            var runtimeTypes = entryByType.Keys
                .Where(IsRuntimePatchComponentType)
                .OrderBy(type => entryByType[type].Id)
                .ToArray();
            var result = new RuntimePatchSchemaDiscoveryResult
            {
                ComponentRegistryHash = calculatedHash,
            };
            for (var i = 0; i < runtimeTypes.Length; i++)
            {
                var type = runtimeTypes[i];
                if (!entryByType.TryGetValue(type, out var entry))
                {
                    throw new InvalidOperationException(
                        $"Runtime component '{type.FullName}' has no stable entry in the compiled GRC type ledger.");
                }
                result.Components.Add(DescribeComponent(type, entry.Id));
            }

            result.Components.Sort(CompareComponents);
            return result;
        }

        public static RuntimePatchGeneratedComponentDescriptor DescribeComponent(
            Type runtimeType,
            int componentTypeId)
        {
            if (runtimeType == null)
                throw new ArgumentNullException(nameof(runtimeType));
            if (componentTypeId < 0)
                throw new ArgumentOutOfRangeException(nameof(componentTypeId));
            if (runtimeType.IsAbstract || !typeof(GameRuntimeComponent).IsAssignableFrom(runtimeType))
                throw new InvalidOperationException($"Type '{runtimeType.FullName}' is not a concrete GameRuntimeComponent.");

            var descriptor = new RuntimePatchGeneratedComponentDescriptor
            {
                RuntimeType = runtimeType,
                Schema = new RuntimePatchComponentSchema
                {
                    ComponentTypeId = componentTypeId,
                    RuntimeType = runtimeType,
                },
            };

            var fields = CollectSerializableFields(runtimeType);
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                ValidateDirectFieldAccess(field, runtimeType);
                var valueType = DescribeValueType(field.FieldType);
                var fieldDescriptor = new RuntimePatchGeneratedFieldDescriptor
                {
                    Field = field,
                    ValueType = valueType,
                    Schema = new RuntimePatchFieldSchema
                    {
                        FieldId = i,
                        Encoding = valueType.Kind == RuntimePatchGeneratedValueKind.RuntimeInstance
                            ? RuntimePatchFieldEncoding.RuntimeReference
                            : valueType.Kind == RuntimePatchGeneratedValueKind.ListVector2Int
                                ? RuntimePatchFieldEncoding.CustomListVector2Int
                                : ContainsKind(valueType, RuntimePatchGeneratedValueKind.List)
                                  || ContainsKind(valueType, RuntimePatchGeneratedValueKind.ListVector2Int)
                                    ? RuntimePatchFieldEncoding.CustomList
                                    : RuntimePatchFieldEncoding.Value,
                    },
                };
                descriptor.Fields.Add(fieldDescriptor);
                descriptor.Schema.Fields.Add(CloneFieldSchema(fieldDescriptor.Schema));
            }

            return descriptor;
        }

        public static RuntimePatchGeneratedTypeDescriptor DescribeValueType(Type type)
        {
            return DescribeValueType(type, new HashSet<Type>());
        }

        public static List<Entry> NormalizeRuntimeEntries(Manifest runtimeManifest)
        {
            if (runtimeManifest == null)
                throw new ArgumentNullException(nameof(runtimeManifest));
            if (runtimeManifest.Types == null)
                throw new InvalidOperationException("Runtime component manifest has no Types collection.");

            var result = new List<Entry>(runtimeManifest.Types.Count);
            for (var i = 0; i < runtimeManifest.Types.Count; i++)
            {
                var source = runtimeManifest.Types[i];
                if (source == null)
                    continue;
                var type = ResolveRuntimeType(source);
                if (type == null)
                {
                    throw new TypeLoadException(
                        $"Active runtime component manifest entry at index {i} requires RuntimeType = typeof(T).");
                }
                result.Add(source);
            }
            result.Sort((first, second) => first.Id.CompareTo(second.Id));
            return result;
        }

        public static Type ResolveRuntimeType(Entry entry)
        {
            return entry?.RuntimeType;
        }

        public static bool IsRuntimePatchComponentType(Type type)
        {
            if (type == null || type.IsAbstract || type == typeof(GameRuntimeComponent))
                return false;
            if (!typeof(GameRuntimeComponent).IsAssignableFrom(type))
                return false;
            if (typeof(ICommandLogic).IsAssignableFrom(type))
                return false;
            if (ContainsNamespaceSegment(type.Namespace, "Tests")
                || ContainsNamespaceSegment(type.Namespace, "Editor")
                || ContainsNamespaceSegment(type.Namespace, "Examples"))
                return false;
            return IsPlayerRuntimeAssembly(type.Assembly);
        }

        public static bool IsPlayerRuntimeAssembly(Assembly assembly)
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

        private static RuntimePatchGeneratedTypeDescriptor DescribeValueType(Type type, HashSet<Type> traversal)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            var primitive = DescribePrimitive(type);
            if (primitive != null)
                return primitive;
            if (type.IsEnum)
            {
                var underlying = Enum.GetUnderlyingType(type);
                if (DescribePrimitive(underlying) == null)
                    throw new InvalidOperationException($"Enum '{type.FullName}' has unsupported underlying type '{underlying.FullName}'.");
                return new RuntimePatchGeneratedTypeDescriptor
                {
                    RuntimeType = type,
                    EnumUnderlyingType = underlying,
                    Kind = RuntimePatchGeneratedValueKind.Enum,
                };
            }
            if (type == typeof(int2))
                return CreateLeaf(type, RuntimePatchGeneratedValueKind.Int2);
            if (type == typeof(float2))
                return CreateLeaf(type, RuntimePatchGeneratedValueKind.Float2);
            if (type == typeof(Vector2Int))
                return CreateLeaf(type, RuntimePatchGeneratedValueKind.Vector2Int);
            if (type == typeof(RuntimeInstance))
                return CreateLeaf(type, RuntimePatchGeneratedValueKind.RuntimeInstance);
            if (type == typeof(Hash128))
                return CreateLeaf(type, RuntimePatchGeneratedValueKind.Hash128);
            if (type == typeof(RuntimeObjectPatch))
                return CreateLeaf(type, RuntimePatchGeneratedValueKind.RuntimeObjectPatch);
            if (type == typeof(List<Vector2Int>))
                return CreateLeaf(type, RuntimePatchGeneratedValueKind.ListVector2Int);
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            {
                if (!traversal.Add(type))
                    throw new InvalidOperationException($"Runtime patch collection '{type.FullName}' contains a recursive field graph.");
                try
                {
                    var elementRuntimeType = type.GetGenericArguments()[0];
                    var elementType = DescribeValueType(elementRuntimeType, traversal);
                    if (elementType.Kind == RuntimePatchGeneratedValueKind.List
                        || elementType.Kind == RuntimePatchGeneratedValueKind.ListVector2Int
                        || elementType.Kind == RuntimePatchGeneratedValueKind.RuntimeObjectPatch)
                    {
                        throw new InvalidOperationException(
                            $"Runtime patch collection '{type.FullName}' must contain a deterministic atom, enum, or value struct.");
                    }
                    return new RuntimePatchGeneratedTypeDescriptor
                    {
                        RuntimeType = type,
                        Kind = RuntimePatchGeneratedValueKind.List,
                        ElementType = elementType,
                    };
                }
                finally
                {
                    traversal.Remove(type);
                }
            }
            if (!type.IsValueType || type.IsGenericType)
                throw new InvalidOperationException($"Runtime patch field type '{type.FullName}' is unsupported.");
            if (!traversal.Add(type))
                throw new InvalidOperationException($"Runtime patch struct '{type.FullName}' contains a recursive field graph.");

            try
            {
                var fields = CollectSerializableFields(type);
                if (fields.Count == 0)
                    throw new InvalidOperationException($"Runtime patch struct '{type.FullName}' has no supported serialized fields.");
                var result = new RuntimePatchGeneratedTypeDescriptor
                {
                    RuntimeType = type,
                    Kind = RuntimePatchGeneratedValueKind.Struct,
                };
                for (var i = 0; i < fields.Count; i++)
                {
                    ValidateDirectFieldAccess(fields[i], type);
                    result.Members.Add(new RuntimePatchGeneratedMemberDescriptor
                    {
                        Field = fields[i],
                        ValueType = DescribeValueType(fields[i].FieldType, traversal),
                    });
                }
                return result;
            }
            finally
            {
                traversal.Remove(type);
            }
        }

        private static RuntimePatchGeneratedTypeDescriptor DescribePrimitive(Type type)
        {
            if (type == typeof(bool)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.Boolean);
            if (type == typeof(byte)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.Byte);
            if (type == typeof(sbyte)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.SByte);
            if (type == typeof(short)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.Int16);
            if (type == typeof(ushort)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.UInt16);
            if (type == typeof(int)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.Int32);
            if (type == typeof(uint)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.UInt32);
            if (type == typeof(long)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.Int64);
            if (type == typeof(ulong)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.UInt64);
            if (type == typeof(float)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.Single);
            if (type == typeof(double)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.Double);
            if (type == typeof(string)) return CreateLeaf(type, RuntimePatchGeneratedValueKind.String);
            return null;
        }

        private static RuntimePatchGeneratedTypeDescriptor CreateLeaf(Type type, RuntimePatchGeneratedValueKind kind)
        {
            return new RuntimePatchGeneratedTypeDescriptor
            {
                RuntimeType = type,
                Kind = kind,
            };
        }

        private static bool ContainsKind(
            RuntimePatchGeneratedTypeDescriptor descriptor,
            RuntimePatchGeneratedValueKind kind)
        {
            if (descriptor.Kind == kind)
                return true;
            if (descriptor.ElementType != null && ContainsKind(descriptor.ElementType, kind))
                return true;
            for (var i = 0; i < descriptor.Members.Count; i++)
            {
                if (ContainsKind(descriptor.Members[i].ValueType, kind))
                    return true;
            }
            return false;
        }

        private static List<FieldInfo> CollectSerializableFields(Type type)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var result = new List<FieldInfo>();
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (field.IsStatic
                    || field.IsLiteral
                    || field.GetCustomAttribute<NonSerializedAttribute>(inherit: false) != null)
                    continue;
                if (!field.IsPublic
                    && field.GetCustomAttribute<SerializeField>(inherit: false) == null
                    && field.GetCustomAttribute<SerializeReference>(inherit: false) == null)
                {
                    continue;
                }
                result.Add(field);
            }
            result.Sort((first, second) => string.CompareOrdinal(first.Name, second.Name));
            return result;
        }

        private static void ValidateDirectFieldAccess(FieldInfo field, Type ownerType)
        {
            if (!field.IsPublic)
            {
                throw new InvalidOperationException(
                    $"Serialized runtime patch field '{ownerType.FullName}.{field.Name}' must be public for generated direct access.");
            }
            if (field.IsInitOnly)
            {
                throw new InvalidOperationException(
                    $"Serialized runtime patch field '{ownerType.FullName}.{field.Name}' cannot be readonly.");
            }
        }

        private static bool ContainsNamespaceSegment(string value, string segment)
        {
            var parts = value.Split('.');
            for (var i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static RuntimePatchFieldSchema CloneFieldSchema(RuntimePatchFieldSchema source)
        {
            return new RuntimePatchFieldSchema
            {
                FieldId = source.FieldId,
                Encoding = source.Encoding,
            };
        }

        private static int CompareComponents(
            RuntimePatchGeneratedComponentDescriptor first,
            RuntimePatchGeneratedComponentDescriptor second)
        {
            return first.Schema.ComponentTypeId.CompareTo(second.Schema.ComponentTypeId);
        }
    }

    public static class RuntimePatchSchemaReconciler
    {
        public const int FORMAT_VERSION = 2;

        public static RuntimePatchSchemaManifest Reconcile(
            IReadOnlyList<RuntimePatchComponentSchema> discovered,
            string componentRegistryHash,
            int codecVersion)
        {
            if (discovered == null)
                throw new ArgumentNullException(nameof(discovered));
            if (string.IsNullOrWhiteSpace(componentRegistryHash))
                throw new ArgumentException("Component registry hash is required.", nameof(componentRegistryHash));
            if (codecVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(codecVersion));
            var resultComponents = CloneComponents(discovered);
            ValidateComponents(resultComponents, "discovered");
            SortComponents(resultComponents);
            var result = new RuntimePatchSchemaManifest
            {
                FormatVersion = FORMAT_VERSION,
                CodecVersion = codecVersion,
                ComponentRegistryHash = componentRegistryHash,
                Components = resultComponents,
            };
            result.SchemaHash = CalculateSchemaHash(result);
            return result;
        }

        public static string CalculateSchemaHash(RuntimePatchSchemaManifest manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            if (manifest.Components == null)
                throw new InvalidOperationException("Runtime patch schema has no Components collection.");

            var components = CloneComponents(manifest.Components);
            ValidateComponents(components, "hash");
            SortComponents(components);
            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteInt32(manifest.FormatVersion);
            writer.WriteInt32(manifest.CodecVersion);
            writer.WriteString(manifest.ComponentRegistryHash);
            writer.WriteInt32(components.Count);
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                writer.WriteInt32(component.ComponentTypeId);
                writer.WriteInt32(component.Fields.Count);
                for (var fieldIndex = 0; fieldIndex < component.Fields.Count; fieldIndex++)
                {
                    var field = component.Fields[fieldIndex];
                    writer.WriteInt32(field.FieldId);
                    writer.WriteByte((byte)field.Encoding);
                }
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(writer.ToArray());
            var builder = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }
            return builder.ToString();
        }

        private static void ValidateComponents(
            List<RuntimePatchComponentSchema> components,
            string source)
        {
            var ids = new HashSet<int>();
            var types = new HashSet<Type>();
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component.ComponentTypeId < 0)
                    throw new InvalidOperationException($"{source} runtime patch component has negative id {component.ComponentTypeId}.");
                if (!ids.Add(component.ComponentTypeId))
                    throw new InvalidOperationException($"{source} runtime patch schema has duplicate component id {component.ComponentTypeId}.");
                if (component.RuntimeType == null)
                    throw new InvalidOperationException($"{source} runtime patch component {component.ComponentTypeId} requires RuntimeType = typeof(T).");
                if (!types.Add(component.RuntimeType))
                    throw new InvalidOperationException($"{source} runtime patch schema has duplicate runtime type '{component.RuntimeType.FullName}'.");
                component.Fields ??= new List<RuntimePatchFieldSchema>();
                ValidateFields(component.Fields, component.ComponentTypeId, source);
            }
        }

        private static void ValidateFields(
            List<RuntimePatchFieldSchema> fields,
            int componentTypeId,
            string source)
        {
            var ids = new HashSet<int>();
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (field.FieldId < 0)
                    throw new InvalidOperationException($"{source} component {componentTypeId} has invalid field id {field.FieldId}.");
                if (!ids.Add(field.FieldId))
                    throw new InvalidOperationException($"{source} component {componentTypeId} has duplicate field id {field.FieldId}.");
            }
        }

        private static List<RuntimePatchComponentSchema> CloneComponents(IReadOnlyList<RuntimePatchComponentSchema> source)
        {
            var result = new List<RuntimePatchComponentSchema>();
            if (source == null)
                return result;
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] == null)
                    throw new InvalidOperationException("Runtime patch schema cannot contain a null component entry.");
                result.Add(CloneComponent(source[i]));
            }
            return result;
        }

        private static RuntimePatchComponentSchema CloneComponent(RuntimePatchComponentSchema source)
        {
            return new RuntimePatchComponentSchema
            {
                ComponentTypeId = source.ComponentTypeId,
                RuntimeType = source.RuntimeType,
                Fields = CloneFields(source.Fields),
            };
        }

        private static List<RuntimePatchFieldSchema> CloneFields(IReadOnlyList<RuntimePatchFieldSchema> source)
        {
            var result = new List<RuntimePatchFieldSchema>();
            if (source == null)
                return result;
            for (var i = 0; i < source.Count; i++)
            {
                if (source[i] == null)
                    throw new InvalidOperationException("Runtime patch schema cannot contain a null field entry.");
                result.Add(CloneField(source[i]));
            }
            return result;
        }

        private static RuntimePatchFieldSchema CloneField(RuntimePatchFieldSchema source)
        {
            return new RuntimePatchFieldSchema
            {
                FieldId = source.FieldId,
                Encoding = source.Encoding,
            };
        }

        private static void SortComponents(List<RuntimePatchComponentSchema> components)
        {
            components.Sort((first, second) =>
                first.ComponentTypeId.CompareTo(second.ComponentTypeId));
            for (var i = 0; i < components.Count; i++)
            {
                components[i].Fields.Sort(CompareFields);
            }
        }

        private static int CompareFields(RuntimePatchFieldSchema first, RuntimePatchFieldSchema second)
        {
            return first.FieldId.CompareTo(second.FieldId);
        }
    }
}
