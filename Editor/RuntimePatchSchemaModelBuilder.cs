using System;
using System.Collections.Generic;
using System.Globalization;
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
        Struct,
        ListVector2Int,
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
                    if (Fields[i].Schema.Encoding == RuntimePatchFieldEncoding.CustomListVector2Int)
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
            var calculatedHash = RuntimeComponentTypeRegistry.CalculateRegistryHash(entries, runtimeManifest.ReservedIds);
            if (!string.IsNullOrWhiteSpace(runtimeManifest.RegistryHash)
                && !string.Equals(runtimeManifest.RegistryHash, calculatedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runtime component manifest hash '{runtimeManifest.RegistryHash}' does not match calculated hash '{calculatedHash}'.");
            }

            var entryByType = new Dictionary<Type, Entry>();
            var entryIds = new HashSet<int>();
            var entryKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                var type = ResolveRuntimeType(entry);
                if (type == null)
                    throw new TypeLoadException($"Runtime component manifest entry {entry.Id} '{entry.Key}' cannot be resolved.");
                if (!entryIds.Add(entry.Id))
                    throw new InvalidOperationException($"Runtime component manifest has duplicate id {entry.Id}.");
                if (!entryKeys.Add(entry.Key))
                    throw new InvalidOperationException($"Runtime component manifest has duplicate key '{entry.Key}'.");
                if (!entryByType.TryAdd(type, entry))
                    throw new InvalidOperationException($"Runtime component manifest contains duplicate type '{type.FullName}'.");
            }

            var runtimeTypes = entryByType.Keys
                .Where(IsRuntimePatchComponentType)
                .OrderBy(RuntimeComponentTypeRegistry.GetKey)
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
                        $"Runtime component '{type.FullName}' has no stable entry in {RuntimeComponentTypeRegistry.DEFAULT_MANIFEST_FILE_NAME}.");
                }
                result.Components.Add(DescribeComponent(type, entry.Id, entry.Key));
            }

            result.Components.Sort(CompareComponents);
            return result;
        }

        public static RuntimePatchGeneratedComponentDescriptor DescribeComponent(
            Type runtimeType,
            int componentTypeId,
            string componentTypeKey)
        {
            if (runtimeType == null)
                throw new ArgumentNullException(nameof(runtimeType));
            if (componentTypeId < 0)
                throw new ArgumentOutOfRangeException(nameof(componentTypeId));
            if (string.IsNullOrWhiteSpace(componentTypeKey))
                throw new ArgumentException("Stable component type key is required.", nameof(componentTypeKey));
            if (runtimeType.IsAbstract || !typeof(GameRuntimeComponent).IsAssignableFrom(runtimeType))
                throw new InvalidOperationException($"Type '{runtimeType.FullName}' is not a concrete GameRuntimeComponent.");

            var descriptor = new RuntimePatchGeneratedComponentDescriptor
            {
                RuntimeType = runtimeType,
                Schema = new RuntimePatchComponentSchema
                {
                    ComponentTypeId = componentTypeId,
                    ComponentTypeKey = componentTypeKey,
                    RuntimeTypeName = runtimeType.FullName,
                    AssemblyName = runtimeType.Assembly.GetName().Name,
                    Tombstone = false,
                },
            };

            var fields = CollectSerializableFields(runtimeType);
            var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                ValidateDirectFieldAccess(field, runtimeType);
                var valueType = DescribeValueType(field.FieldType);
                if (valueType.Kind != RuntimePatchGeneratedValueKind.ListVector2Int
                    && ContainsKind(valueType, RuntimePatchGeneratedValueKind.ListVector2Int))
                {
                    throw new InvalidOperationException(
                        $"Runtime patch collection '{runtimeType.FullName}.{field.Name}' must be a direct component field so it can use an explicit atomic codec.");
                }
                var keyAttribute = field.GetCustomAttribute<RuntimePatchFieldKeyAttribute>(inherit: false);
                var fieldKey = keyAttribute == null
                    ? $"{componentTypeKey}/{field.Name}"
                    : keyAttribute.Key?.Trim();
                if (string.IsNullOrWhiteSpace(fieldKey))
                    throw new InvalidOperationException($"Runtime patch field '{runtimeType.FullName}.{field.Name}' has an empty stable key.");
                if (!fieldKeys.Add(fieldKey))
                    throw new InvalidOperationException($"Runtime patch component '{componentTypeKey}' has duplicate field key '{fieldKey}'.");

                var fieldDescriptor = new RuntimePatchGeneratedFieldDescriptor
                {
                    Field = field,
                    ValueType = valueType,
                    Schema = new RuntimePatchFieldSchema
                    {
                        FieldId = -1,
                        FieldKey = fieldKey,
                        FieldName = field.Name,
                        FieldTypeSignature = CreateTypeSignature(valueType),
                        Encoding = valueType.Kind == RuntimePatchGeneratedValueKind.RuntimeInstance
                            ? RuntimePatchFieldEncoding.RuntimeReference
                            : valueType.Kind == RuntimePatchGeneratedValueKind.ListVector2Int
                                ? RuntimePatchFieldEncoding.CustomListVector2Int
                                : RuntimePatchFieldEncoding.Value,
                        Tombstone = false,
                    },
                };
                descriptor.Fields.Add(fieldDescriptor);
                descriptor.Schema.Fields.Add(CloneFieldSchema(fieldDescriptor.Schema));
            }

            descriptor.Fields.Sort(CompareFieldsByKey);
            descriptor.Schema.Fields.Sort(CompareFieldSchemasByKey);
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
            var legacy = runtimeManifest.Version < RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION;
            for (var i = 0; i < runtimeManifest.Types.Count; i++)
            {
                var source = runtimeManifest.Types[i];
                if (source == null)
                    continue;
                var type = ResolveRuntimeType(source);
                if (type == null)
                {
                    throw new TypeLoadException(
                        $"Runtime component manifest entry at index {i} cannot resolve '{source.AssemblyQualifiedName ?? source.TypeName}'.");
                }

                var id = legacy ? i : source.Id;
                var entry = RuntimeComponentTypeRegistry.CreateEntry(id, type, source.CreatedAt);
                if (!string.IsNullOrWhiteSpace(source.Key)
                    && !string.Equals(source.Key.Trim(), entry.Key, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Runtime component manifest key '{source.Key}' does not match runtime key '{entry.Key}' for '{type.FullName}'.");
                }
                result.Add(entry);
            }
            result.Sort((first, second) => first.Id.CompareTo(second.Id));
            return result;
        }

        public static Type ResolveRuntimeType(Entry entry)
        {
            if (entry == null)
                return null;

            if (!string.IsNullOrWhiteSpace(entry.TypeName) && !string.IsNullOrWhiteSpace(entry.AssemblyName))
            {
                var resolved = Type.GetType($"{entry.TypeName}, {entry.AssemblyName}", throwOnError: false);
                if (resolved != null)
                    return resolved;
            }
            if (!string.IsNullOrWhiteSpace(entry.AssemblyQualifiedName))
            {
                var resolved = Type.GetType(entry.AssemblyQualifiedName, throwOnError: false);
                if (resolved != null)
                    return resolved;
            }

            var fullName = !string.IsNullOrWhiteSpace(entry.TypeName)
                ? entry.TypeName.Trim()
                : entry.AssemblyQualifiedName?.Split(',')[0].Trim();
            if (string.IsNullOrWhiteSpace(fullName))
                return null;
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];
                if (!string.IsNullOrWhiteSpace(entry.AssemblyName)
                    && !string.Equals(assembly.GetName().Name, entry.AssemblyName, StringComparison.Ordinal))
                {
                    continue;
                }
                var resolved = assembly.GetType(fullName, throwOnError: false);
                if (resolved != null)
                    return resolved;
            }
            return null;
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

        public static string CreateTypeSignature(RuntimePatchGeneratedTypeDescriptor descriptor)
        {
            if (descriptor == null)
                throw new ArgumentNullException(nameof(descriptor));
            switch (descriptor.Kind)
            {
                case RuntimePatchGeneratedValueKind.Boolean: return "bool";
                case RuntimePatchGeneratedValueKind.Byte: return "byte";
                case RuntimePatchGeneratedValueKind.SByte: return "sbyte";
                case RuntimePatchGeneratedValueKind.Int16: return "int16";
                case RuntimePatchGeneratedValueKind.UInt16: return "uint16";
                case RuntimePatchGeneratedValueKind.Int32: return "int32";
                case RuntimePatchGeneratedValueKind.UInt32: return "uint32";
                case RuntimePatchGeneratedValueKind.Int64: return "int64";
                case RuntimePatchGeneratedValueKind.UInt64: return "uint64";
                case RuntimePatchGeneratedValueKind.Single: return "float32";
                case RuntimePatchGeneratedValueKind.Double: return "float64";
                case RuntimePatchGeneratedValueKind.String: return "string";
                case RuntimePatchGeneratedValueKind.Int2: return "Unity.Mathematics.int2(x:int32,y:int32)";
                case RuntimePatchGeneratedValueKind.Float2: return "Unity.Mathematics.float2(x:float32,y:float32)";
                case RuntimePatchGeneratedValueKind.Vector2Int: return "UnityEngine.Vector2Int(x:int32,y:int32)";
                case RuntimePatchGeneratedValueKind.RuntimeInstance: return "runtime-reference:v1";
                case RuntimePatchGeneratedValueKind.ListVector2Int: return "list-atomic:UnityEngine.Vector2Int:v1";
                case RuntimePatchGeneratedValueKind.Enum:
                    return CreateEnumSignature(descriptor.RuntimeType, descriptor.EnumUnderlyingType);
                case RuntimePatchGeneratedValueKind.Struct:
                    var builder = new StringBuilder();
                    builder.Append("struct:")
                        .Append(descriptor.RuntimeType.Assembly.GetName().Name)
                        .Append(':')
                        .Append(descriptor.RuntimeType.FullName)
                        .Append('{');
                    for (var i = 0; i < descriptor.Members.Count; i++)
                    {
                        if (i > 0)
                            builder.Append(';');
                        builder.Append(descriptor.Members[i].Field.Name)
                            .Append(':')
                            .Append(CreateTypeSignature(descriptor.Members[i].ValueType));
                    }
                    builder.Append('}');
                    return builder.ToString();
                default:
                    throw new InvalidOperationException($"Unsupported generated patch value kind {descriptor.Kind}.");
            }
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
            if (type == typeof(List<Vector2Int>))
                return CreateLeaf(type, RuntimePatchGeneratedValueKind.ListVector2Int);
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

        private static string CreateEnumSignature(Type enumType, Type underlyingType)
        {
            var names = Enum.GetNames(enumType);
            Array.Sort(names, StringComparer.Ordinal);
            var builder = new StringBuilder();
            builder.Append("enum:")
                .Append(enumType.Assembly.GetName().Name)
                .Append(':')
                .Append(enumType.FullName)
                .Append(':')
                .Append(underlyingType.FullName)
                .Append('{');
            for (var i = 0; i < names.Length; i++)
            {
                if (i > 0)
                    builder.Append(';');
                var value = Enum.Parse(enumType, names[i]);
                builder.Append(names[i])
                    .Append('=')
                    .Append(ConvertEnumValue(value, underlyingType));
            }
            builder.Append('}');
            return builder.ToString();
        }

        private static string ConvertEnumValue(object value, Type underlyingType)
        {
            if (underlyingType == typeof(byte)
                || underlyingType == typeof(ushort)
                || underlyingType == typeof(uint)
                || underlyingType == typeof(ulong))
            {
                return Convert.ToUInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }

        private static RuntimePatchFieldSchema CloneFieldSchema(RuntimePatchFieldSchema source)
        {
            return new RuntimePatchFieldSchema
            {
                FieldId = source.FieldId,
                FieldKey = source.FieldKey,
                FieldName = source.FieldName,
                FieldTypeSignature = source.FieldTypeSignature,
                Encoding = source.Encoding,
                Tombstone = source.Tombstone,
            };
        }

        private static int CompareComponents(
            RuntimePatchGeneratedComponentDescriptor first,
            RuntimePatchGeneratedComponentDescriptor second)
        {
            var byId = first.Schema.ComponentTypeId.CompareTo(second.Schema.ComponentTypeId);
            return byId != 0 ? byId : string.CompareOrdinal(first.Schema.ComponentTypeKey, second.Schema.ComponentTypeKey);
        }

        private static int CompareFieldsByKey(
            RuntimePatchGeneratedFieldDescriptor first,
            RuntimePatchGeneratedFieldDescriptor second)
        {
            return string.CompareOrdinal(first.Schema.FieldKey, second.Schema.FieldKey);
        }

        private static int CompareFieldSchemasByKey(RuntimePatchFieldSchema first, RuntimePatchFieldSchema second)
        {
            return string.CompareOrdinal(first.FieldKey, second.FieldKey);
        }
    }

    public static class RuntimePatchSchemaReconciler
    {
        public const int FORMAT_VERSION = 1;

        public static RuntimePatchSchemaManifest Reconcile(
            RuntimePatchSchemaManifest existing,
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
            if (existing != null && existing.FormatVersion != 0 && existing.FormatVersion != FORMAT_VERSION)
            {
                throw new InvalidOperationException(
                    $"Runtime patch schema format {existing.FormatVersion} is unsupported; expected {FORMAT_VERSION}.");
            }

            var existingComponents = CloneComponents(existing?.Components);
            ValidateComponents(existingComponents, "existing");
            var currentComponents = CloneComponents(discovered);
            ValidateComponents(currentComponents, "discovered", requireAssignedFieldIds: false);

            var resultComponents = CloneComponents(existingComponents);
            var resultByKey = resultComponents.ToDictionary(
                component => component.ComponentTypeKey,
                component => component,
                StringComparer.Ordinal);
            var resultById = resultComponents.ToDictionary(
                component => component.ComponentTypeId,
                component => component);
            var currentKeys = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < currentComponents.Count; i++)
            {
                var current = currentComponents[i];
                currentKeys.Add(current.ComponentTypeKey);
                if (resultByKey.TryGetValue(current.ComponentTypeKey, out var target))
                {
                    if (target.Tombstone)
                    {
                        throw new InvalidOperationException(
                            $"Runtime patch component key '{current.ComponentTypeKey}' is tombstoned and cannot be reused.");
                    }
                    if (target.ComponentTypeId != current.ComponentTypeId)
                    {
                        throw new InvalidOperationException(
                            $"Runtime patch component '{current.ComponentTypeKey}' changed id from {target.ComponentTypeId} to {current.ComponentTypeId}.");
                    }
                    target.RuntimeTypeName = current.RuntimeTypeName;
                    target.AssemblyName = current.AssemblyName;
                    target.Tombstone = false;
                    target.Fields = ReconcileFields(target.Fields, current.Fields, current.ComponentTypeKey);
                    continue;
                }

                if (resultById.TryGetValue(current.ComponentTypeId, out var idOwner))
                {
                    throw new InvalidOperationException(
                        $"Runtime patch component id {current.ComponentTypeId} is already reserved by '{idOwner.ComponentTypeKey}'.");
                }
                var added = CloneComponent(current);
                added.Tombstone = false;
                added.Fields = ReconcileFields(null, current.Fields, current.ComponentTypeKey);
                resultComponents.Add(added);
                resultByKey.Add(added.ComponentTypeKey, added);
                resultById.Add(added.ComponentTypeId, added);
            }

            for (var i = 0; i < resultComponents.Count; i++)
            {
                var component = resultComponents[i];
                if (currentKeys.Contains(component.ComponentTypeKey))
                    continue;
                component.Tombstone = true;
                for (var fieldIndex = 0; fieldIndex < component.Fields.Count; fieldIndex++)
                {
                    component.Fields[fieldIndex].Tombstone = true;
                }
            }

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
                writer.WriteString(component.ComponentTypeKey);
                writer.WriteString(component.RuntimeTypeName);
                writer.WriteString(component.AssemblyName);
                writer.WriteBoolean(component.Tombstone);
                writer.WriteInt32(component.Fields.Count);
                for (var fieldIndex = 0; fieldIndex < component.Fields.Count; fieldIndex++)
                {
                    var field = component.Fields[fieldIndex];
                    writer.WriteInt32(field.FieldId);
                    writer.WriteString(field.FieldKey);
                    writer.WriteString(field.FieldName);
                    writer.WriteString(field.FieldTypeSignature);
                    writer.WriteByte((byte)field.Encoding);
                    writer.WriteBoolean(field.Tombstone);
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

        private static List<RuntimePatchFieldSchema> ReconcileFields(
            IReadOnlyList<RuntimePatchFieldSchema> existing,
            IReadOnlyList<RuntimePatchFieldSchema> discovered,
            string componentKey)
        {
            var result = CloneFields(existing);
            ValidateFields(result, componentKey, "existing");
            var current = CloneFields(discovered);
            ValidateFields(current, componentKey, "discovered", requireAssignedIds: false);
            current.Sort((first, second) => string.CompareOrdinal(first.FieldKey, second.FieldKey));
            var byKey = result.ToDictionary(field => field.FieldKey, field => field, StringComparer.Ordinal);
            var currentKeys = new HashSet<string>(StringComparer.Ordinal);
            var nextId = -1;
            for (var i = 0; i < result.Count; i++)
            {
                nextId = Math.Max(nextId, result[i].FieldId);
            }

            for (var i = 0; i < current.Count; i++)
            {
                var source = current[i];
                currentKeys.Add(source.FieldKey);
                if (byKey.TryGetValue(source.FieldKey, out var target))
                {
                    if (target.Tombstone)
                    {
                        throw new InvalidOperationException(
                            $"Runtime patch field key '{source.FieldKey}' is tombstoned and cannot be reused.");
                    }
                    target.FieldName = source.FieldName;
                    target.FieldTypeSignature = source.FieldTypeSignature;
                    target.Encoding = source.Encoding;
                    target.Tombstone = false;
                    continue;
                }

                var added = CloneField(source);
                added.FieldId = ++nextId;
                added.Tombstone = false;
                result.Add(added);
                byKey.Add(added.FieldKey, added);
            }

            for (var i = 0; i < result.Count; i++)
            {
                if (!currentKeys.Contains(result[i].FieldKey))
                    result[i].Tombstone = true;
            }
            result.Sort(CompareFields);
            return result;
        }

        private static void ValidateComponents(
            List<RuntimePatchComponentSchema> components,
            string source,
            bool requireAssignedFieldIds = true)
        {
            var ids = new HashSet<int>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component.ComponentTypeId < 0)
                    throw new InvalidOperationException($"{source} runtime patch component has negative id {component.ComponentTypeId}.");
                if (string.IsNullOrWhiteSpace(component.ComponentTypeKey))
                    throw new InvalidOperationException($"{source} runtime patch component {component.ComponentTypeId} has no key.");
                if (!ids.Add(component.ComponentTypeId))
                    throw new InvalidOperationException($"{source} runtime patch schema has duplicate component id {component.ComponentTypeId}.");
                if (!keys.Add(component.ComponentTypeKey))
                    throw new InvalidOperationException($"{source} runtime patch schema has duplicate component key '{component.ComponentTypeKey}'.");
                component.Fields ??= new List<RuntimePatchFieldSchema>();
                ValidateFields(component.Fields, component.ComponentTypeKey, source, requireAssignedFieldIds);
            }
        }

        private static void ValidateFields(
            List<RuntimePatchFieldSchema> fields,
            string componentKey,
            string source,
            bool requireAssignedIds = true)
        {
            var ids = new HashSet<int>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (requireAssignedIds && field.FieldId < 0)
                    throw new InvalidOperationException($"{source} field '{field.FieldKey}' in '{componentKey}' has no assigned id.");
                if (!requireAssignedIds && field.FieldId < -1)
                    throw new InvalidOperationException($"{source} field '{field.FieldKey}' in '{componentKey}' has invalid id {field.FieldId}.");
                if (string.IsNullOrWhiteSpace(field.FieldKey))
                    throw new InvalidOperationException($"{source} field in '{componentKey}' has no stable key.");
                if (!keys.Add(field.FieldKey))
                    throw new InvalidOperationException($"{source} component '{componentKey}' has duplicate field key '{field.FieldKey}'.");
                if (field.FieldId >= 0 && !ids.Add(field.FieldId))
                    throw new InvalidOperationException($"{source} component '{componentKey}' has duplicate field id {field.FieldId}.");
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
                ComponentTypeKey = source.ComponentTypeKey,
                RuntimeTypeName = source.RuntimeTypeName,
                AssemblyName = source.AssemblyName,
                Tombstone = source.Tombstone,
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
                FieldKey = source.FieldKey,
                FieldName = source.FieldName,
                FieldTypeSignature = source.FieldTypeSignature,
                Encoding = source.Encoding,
                Tombstone = source.Tombstone,
            };
        }

        private static void SortComponents(List<RuntimePatchComponentSchema> components)
        {
            components.Sort((first, second) =>
            {
                var byId = first.ComponentTypeId.CompareTo(second.ComponentTypeId);
                return byId != 0 ? byId : string.CompareOrdinal(first.ComponentTypeKey, second.ComponentTypeKey);
            });
            for (var i = 0; i < components.Count; i++)
            {
                components[i].Fields.Sort(CompareFields);
            }
        }

        private static int CompareFields(RuntimePatchFieldSchema first, RuntimePatchFieldSchema second)
        {
            var byId = first.FieldId.CompareTo(second.FieldId);
            return byId != 0 ? byId : string.CompareOrdinal(first.FieldKey, second.FieldKey);
        }
    }
}
