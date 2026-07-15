using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    /// <summary>
    /// Converts between stable-key/canonical-JSON authored patches and the
    /// numeric/generated-binary patches consumed by runtime and network code.
    /// Reflection is deliberately confined to this authoring/materialization path.
    /// </summary>
    public sealed class RuntimeObjectPatchAuthoringCodec
    {
        private readonly RuntimePatchCodecRegistry _registry;
        private readonly Dictionary<string, ComponentMetadata> _metadataByKey = new(StringComparer.Ordinal);

        public RuntimeObjectPatchAuthoringCodec(RuntimePatchCodecRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public RuntimeObjectPatch BuildAuthoringPatch(
            IReadOnlyDictionary<uint, GameRuntimeComponent> baseline,
            IReadOnlyDictionary<uint, GameRuntimeComponent> current,
            RuntimePatchCodecContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var runtimePatch = new RuntimeObjectPatchEngine(_registry, context).BuildPatch(baseline, current);
            var result = new RuntimeObjectPatch(
                _registry.SchemaHash,
                RuntimeObjectPatchRepresentation.AuthoringCanonicalJson);
            for (var i = 0; i < runtimePatch.Components.Count; i++)
            {
                var runtimeComponent = runtimePatch.Components[i];
                var codec = _registry.Get(runtimeComponent.ComponentTypeId);
                var metadata = GetMetadata(codec);
                TryGetComponent(current, codec.ComponentTypeId, out var currentComponent);
                var authored = ComponentPatch.Authoring(codec.ComponentTypeKey, runtimeComponent.Kind);
                switch (runtimeComponent.Kind)
                {
                    case ComponentPatchKind.Add:
                    case ComponentPatchKind.Custom:
                        if (currentComponent == null)
                            throw new InvalidOperationException($"Authored {runtimeComponent.Kind} component '{codec.ComponentTypeKey}' has no current value.");
                        authored.CanonicalJson = EncodeComponent(metadata, currentComponent, context);
                        break;

                    case ComponentPatchKind.Remove:
                        break;

                    case ComponentPatchKind.Fields:
                        if (currentComponent == null)
                            throw new InvalidOperationException($"Authored fields component '{codec.ComponentTypeKey}' has no current value.");
                        var fields = runtimeComponent.Fields == null
                            ? new List<FieldPatch>()
                            : new List<FieldPatch>(runtimeComponent.Fields);
                        fields.Sort((left, right) => string.CompareOrdinal(left?.FieldKey, right?.FieldKey));
                        for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
                        {
                            var runtimeField = fields[fieldIndex]
                                ?? throw new InvalidOperationException($"Runtime component '{codec.ComponentTypeKey}' contains a null field patch.");
                            var field = metadata.GetField(runtimeField.FieldKey, runtimeField.FieldId);
                            authored.Fields.Add(FieldPatch.Authoring(
                                field.Key,
                                runtimeField.Kind,
                                runtimeField.Kind == FieldPatchKind.Set
                                    ? CanonicalJsonValueCodec.Encode(field.Field.FieldType, field.Field.GetValue(currentComponent), context)
                                    : null));
                        }
                        break;

                    default:
                        throw new InvalidOperationException($"Unsupported component patch kind {runtimeComponent.Kind}.");
                }

                result.Components.Add(authored);
            }

            result.Components.Sort((left, right) => string.CompareOrdinal(left.ComponentTypeKey, right.ComponentTypeKey));
            ValidateAuthoringShape(result);
            return result;
        }

        public RuntimeObjectPatch MaterializeRuntimePatch(
            IReadOnlyDictionary<uint, GameRuntimeComponent> baseline,
            RuntimeObjectPatch patch,
            RuntimePatchCodecContext context)
        {
            if (patch == null)
                throw new ArgumentNullException(nameof(patch));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (patch.Representation == RuntimeObjectPatchRepresentation.RuntimeBinary)
            {
                if (!string.Equals(patch.SchemaHash, _registry.SchemaHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Runtime-binary patch schema '{patch.SchemaHash}' does not match active schema '{_registry.SchemaHash}'.");
                }
                return patch;
            }
            if (patch.Representation != RuntimeObjectPatchRepresentation.AuthoringCanonicalJson)
                throw new InvalidOperationException($"Unsupported patch representation {patch.Representation}.");

            ValidateAuthoringShape(patch);
            var authoredComponents = new List<ComponentPatch>(patch.Components);
            authoredComponents.Sort((left, right) => string.CompareOrdinal(left.ComponentTypeKey, right.ComponentTypeKey));
            var result = new RuntimeObjectPatch(_registry.SchemaHash);
            for (var i = 0; i < authoredComponents.Count; i++)
            {
                var authored = authoredComponents[i];
                var codec = _registry.Get(authored.ComponentTypeKey);
                var metadata = GetMetadata(codec);
                TryGetComponent(baseline, codec.ComponentTypeId, out var baselineComponent);
                ValidateComponentValue(codec, baselineComponent, "baseline");

                ComponentPatch runtime;
                switch (authored.Kind)
                {
                    case ComponentPatchKind.Add:
                        if (baselineComponent != null)
                            throw new InvalidOperationException($"Authored add component '{codec.ComponentTypeKey}' already exists in the baseline.");
                        runtime = codec.BuildPatch(
                            null,
                            DecodeComponent(metadata, authored.CanonicalJson, context),
                            context);
                        RequireKind(runtime, ComponentPatchKind.Add, codec.ComponentTypeKey);
                        break;

                    case ComponentPatchKind.Remove:
                        if (baselineComponent == null)
                            throw new InvalidOperationException($"Authored remove component '{codec.ComponentTypeKey}' is absent from the baseline.");
                        runtime = codec.BuildPatch(baselineComponent, null, context);
                        RequireKind(runtime, ComponentPatchKind.Remove, codec.ComponentTypeKey);
                        break;

                    case ComponentPatchKind.Custom:
                        if (baselineComponent == null)
                            throw new InvalidOperationException($"Authored custom component '{codec.ComponentTypeKey}' is absent from the baseline.");
                        runtime = codec.BuildPatch(
                            baselineComponent,
                            DecodeComponent(metadata, authored.CanonicalJson, context),
                            context);
                        RequireKind(runtime, ComponentPatchKind.Custom, codec.ComponentTypeKey);
                        break;

                    case ComponentPatchKind.Fields:
                        if (baselineComponent == null)
                            throw new InvalidOperationException($"Authored fields component '{codec.ComponentTypeKey}' is absent from the baseline.");
                        var current = codec.Clone(baselineComponent);
                        var authoredFields = new List<FieldPatch>(authored.Fields);
                        authoredFields.Sort((left, right) => string.CompareOrdinal(left.FieldKey, right.FieldKey));
                        for (var fieldIndex = 0; fieldIndex < authoredFields.Count; fieldIndex++)
                        {
                            var authoredField = authoredFields[fieldIndex];
                            var field = metadata.GetField(authoredField.FieldKey);
                            var value = authoredField.Kind == FieldPatchKind.Remove
                                ? DefaultValue(field.Field.FieldType)
                                : CanonicalJsonValueCodec.Decode(
                                    field.Field.FieldType,
                                    authoredField.CanonicalJson,
                                    context);
                            field.Field.SetValue(current, value);
                        }

                        runtime = codec.BuildPatch(baselineComponent, current, context);
                        RequireKind(runtime, ComponentPatchKind.Fields, codec.ComponentTypeKey);
                        RequireSameFieldKeys(authoredFields, runtime.Fields, codec.ComponentTypeKey);
                        break;

                    default:
                        throw new InvalidOperationException($"Unsupported authored component patch kind {authored.Kind}.");
                }

                result.Components.Add(runtime);
            }

            result.Components.Sort((left, right) => left.ComponentTypeId.CompareTo(right.ComponentTypeId));
            return result;
        }

        public static RuntimeObjectPatch ClonePatch(RuntimeObjectPatch patch)
        {
            if (patch == null)
                return null;
            if (patch.Representation == RuntimeObjectPatchRepresentation.RuntimeBinary)
            {
                var binaryCodec = new RuntimeObjectPatchBinaryCodec();
                return binaryCodec.Decode(binaryCodec.Encode(patch));
            }
            if (patch.Representation != RuntimeObjectPatchRepresentation.AuthoringCanonicalJson)
                throw new InvalidOperationException($"Unsupported patch representation {patch.Representation}.");

            ValidateAuthoringShape(patch);
            var result = new RuntimeObjectPatch(patch.SchemaHash, patch.Representation);
            for (var componentIndex = 0; componentIndex < patch.Components.Count; componentIndex++)
            {
                var sourceComponent = patch.Components[componentIndex];
                var targetComponent = ComponentPatch.Authoring(
                    sourceComponent.ComponentTypeKey,
                    sourceComponent.Kind,
                    sourceComponent.CanonicalJson);
                for (var fieldIndex = 0; fieldIndex < sourceComponent.Fields.Count; fieldIndex++)
                {
                    var sourceField = sourceComponent.Fields[fieldIndex];
                    targetComponent.Fields.Add(FieldPatch.Authoring(
                        sourceField.FieldKey,
                        sourceField.Kind,
                        sourceField.CanonicalJson));
                }
                result.Components.Add(targetComponent);
            }
            return result;
        }

        private ComponentMetadata GetMetadata(RuntimeComponentPatchCodec codec)
        {
            if (_metadataByKey.TryGetValue(codec.ComponentTypeKey, out var cached))
                return cached;
            var created = ComponentMetadata.Create(codec);
            _metadataByKey.Add(codec.ComponentTypeKey, created);
            return created;
        }

        private static void ValidateAuthoringShape(RuntimeObjectPatch patch)
        {
            if (patch.Representation != RuntimeObjectPatchRepresentation.AuthoringCanonicalJson)
                throw new InvalidOperationException($"Expected an authoring JSON patch, received {patch.Representation}.");
            if (string.IsNullOrWhiteSpace(patch.SchemaHash))
                throw new InvalidOperationException("Authored patch schema provenance cannot be empty.");
            if (patch.Components == null)
                throw new InvalidOperationException("Authored patch components cannot be null.");

            var componentKeys = new HashSet<string>(StringComparer.Ordinal);
            string previousComponentKey = null;
            for (var i = 0; i < patch.Components.Count; i++)
            {
                var component = patch.Components[i]
                    ?? throw new InvalidOperationException("Authored patch cannot contain a null component.");
                if (component.ComponentTypeId != 0 || component.Payload != null)
                    throw new InvalidOperationException($"Authored component '{component.ComponentTypeKey}' cannot contain numeric ids or binary payload.");
                if (string.IsNullOrWhiteSpace(component.ComponentTypeKey)
                    || !componentKeys.Add(component.ComponentTypeKey))
                {
                    throw new InvalidOperationException($"Authored patch contains an empty or duplicate component key '{component.ComponentTypeKey}'.");
                }
                if (previousComponentKey != null
                    && string.CompareOrdinal(previousComponentKey, component.ComponentTypeKey) >= 0)
                {
                    throw new InvalidOperationException("Authored patch component keys must be strictly ordered.");
                }
                previousComponentKey = component.ComponentTypeKey;

                if (component.Fields == null)
                    throw new InvalidOperationException($"Authored component '{component.ComponentTypeKey}' fields cannot be null.");
                var fieldCount = component.Fields.Count;
                switch (component.Kind)
                {
                    case ComponentPatchKind.Add:
                    case ComponentPatchKind.Custom:
                        if (component.CanonicalJson == null || fieldCount != 0)
                            throw new InvalidOperationException($"Authored {component.Kind} component '{component.ComponentTypeKey}' requires full canonical JSON and no fields.");
                        break;
                    case ComponentPatchKind.Remove:
                        if (component.CanonicalJson != null || fieldCount != 0)
                            throw new InvalidOperationException($"Authored remove component '{component.ComponentTypeKey}' cannot contain values.");
                        break;
                    case ComponentPatchKind.Fields:
                        if (component.CanonicalJson != null || fieldCount == 0)
                            throw new InvalidOperationException($"Authored fields component '{component.ComponentTypeKey}' requires fields and no component JSON.");
                        ValidateAuthoringFields(component);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported authored component patch kind {component.Kind}.");
                }
            }
        }

        private static void ValidateAuthoringFields(ComponentPatch component)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            string previousFieldKey = null;
            for (var i = 0; i < component.Fields.Count; i++)
            {
                var field = component.Fields[i]
                    ?? throw new InvalidOperationException($"Authored component '{component.ComponentTypeKey}' contains a null field.");
                if (field.FieldId != 0 || field.Payload != null)
                    throw new InvalidOperationException($"Authored field '{field.FieldKey}' cannot contain numeric ids or binary payload.");
                if (string.IsNullOrWhiteSpace(field.FieldKey) || !keys.Add(field.FieldKey))
                    throw new InvalidOperationException($"Authored component '{component.ComponentTypeKey}' contains an empty or duplicate field key '{field.FieldKey}'.");
                if (previousFieldKey != null && string.CompareOrdinal(previousFieldKey, field.FieldKey) >= 0)
                {
                    throw new InvalidOperationException(
                        $"Authored component '{component.ComponentTypeKey}' field keys must be strictly ordered.");
                }
                previousFieldKey = field.FieldKey;
                if (field.Kind == FieldPatchKind.Set && field.CanonicalJson == null)
                    throw new InvalidOperationException($"Authored field '{field.FieldKey}' set requires canonical JSON.");
                if (field.Kind == FieldPatchKind.Remove && field.CanonicalJson != null)
                    throw new InvalidOperationException($"Authored field '{field.FieldKey}' remove cannot contain JSON.");
                if (field.Kind != FieldPatchKind.Set && field.Kind != FieldPatchKind.Remove)
                    throw new InvalidOperationException($"Unsupported authored field patch kind {field.Kind}.");
            }
        }

        private static void RequireKind(ComponentPatch patch, ComponentPatchKind expected, string componentKey)
        {
            if (patch == null || patch.Kind != expected)
            {
                throw new InvalidOperationException(
                    $"Authored component '{componentKey}' materialized as {patch?.Kind.ToString() ?? "no patch"}, expected {expected}. "
                    + "Redundant or schema-inconsistent overrides are not accepted.");
            }
        }

        private static void RequireSameFieldKeys(
            IReadOnlyList<FieldPatch> authored,
            IReadOnlyList<FieldPatch> runtime,
            string componentKey)
        {
            if (runtime == null || authored.Count != runtime.Count)
                throw new InvalidOperationException($"Authored component '{componentKey}' materialized a different field set.");
            var expected = new HashSet<string>(authored.Select(value => value.FieldKey), StringComparer.Ordinal);
            for (var i = 0; i < runtime.Count; i++)
            {
                if (runtime[i] == null || !expected.Remove(runtime[i].FieldKey))
                    throw new InvalidOperationException($"Authored component '{componentKey}' materialized unexpected field '{runtime[i]?.FieldKey}'.");
            }
            if (expected.Count != 0)
                throw new InvalidOperationException($"Authored component '{componentKey}' did not materialize all authored fields.");
        }

        private static void ValidateComponentValue(
            RuntimeComponentPatchCodec codec,
            GameRuntimeComponent component,
            string label)
        {
            if (component != null && component.GetType() != codec.ComponentRuntimeType)
            {
                throw new InvalidOperationException(
                    $"Component '{codec.ComponentTypeKey}' {label} has type '{component.GetType().FullName}', expected '{codec.ComponentRuntimeType.FullName}'.");
            }
        }

        private static string EncodeComponent(
            ComponentMetadata metadata,
            GameRuntimeComponent component,
            RuntimePatchCodecContext context)
        {
            if (component == null || component.GetType() != metadata.RuntimeType)
                throw new InvalidOperationException($"Cannot encode component '{metadata.ComponentKey}' from type '{component?.GetType().FullName ?? "null"}'.");
            var json = new JObject();
            for (var i = 0; i < metadata.Fields.Count; i++)
            {
                var field = metadata.Fields[i];
                json.Add(field.Key, CanonicalJsonValueCodec.Write(field.Field.FieldType, field.Field.GetValue(component), context));
            }
            return json.ToString(Formatting.None);
        }

        private static GameRuntimeComponent DecodeComponent(
            ComponentMetadata metadata,
            string canonicalJson,
            RuntimePatchCodecContext context)
        {
            var token = CanonicalJsonValueCodec.Parse(canonicalJson);
            var json = CanonicalJsonValueCodec.RequireObject(token, metadata.ComponentKey);
            CanonicalJsonValueCodec.RequireExactProperties(json, metadata.Fields.Select(field => field.Key), metadata.ComponentKey);
            if (Activator.CreateInstance(metadata.RuntimeType) is not GameRuntimeComponent result)
                throw new InvalidOperationException($"Component '{metadata.RuntimeType.FullName}' cannot be constructed from authoring JSON.");
            for (var i = 0; i < metadata.Fields.Count; i++)
            {
                var field = metadata.Fields[i];
                field.Field.SetValue(result, CanonicalJsonValueCodec.Read(field.Field.FieldType, json[field.Key], context));
            }
            var normalized = EncodeComponent(metadata, result, context);
            if (!string.Equals(normalized, canonicalJson, StringComparison.Ordinal))
                throw new FormatException($"Component '{metadata.ComponentKey}' JSON is valid but not canonical.");
            return result;
        }

        private static object DefaultValue(Type type) => type.IsValueType ? Activator.CreateInstance(type) : null;

        private static bool TryGetComponent(
            IReadOnlyDictionary<uint, GameRuntimeComponent> components,
            uint typeId,
            out GameRuntimeComponent component)
        {
            if (components != null && components.TryGetValue(typeId, out component))
                return true;
            component = null;
            return false;
        }

        private sealed class ComponentMetadata
        {
            private readonly Dictionary<string, FieldMetadata> _fieldsByKey;

            public string ComponentKey { get; }
            public Type RuntimeType { get; }
            public IReadOnlyList<FieldMetadata> Fields { get; }

            private ComponentMetadata(
                string componentKey,
                Type runtimeType,
                List<FieldMetadata> fields)
            {
                ComponentKey = componentKey;
                RuntimeType = runtimeType;
                fields.Sort((left, right) => string.CompareOrdinal(left.Key, right.Key));
                Fields = fields.AsReadOnly();
                _fieldsByKey = fields.ToDictionary(field => field.Key, field => field, StringComparer.Ordinal);
            }

            public static ComponentMetadata Create(RuntimeComponentPatchCodec codec)
            {
                if (codec.ComponentRuntimeType == null
                    || codec.ComponentRuntimeType.IsAbstract
                    || !typeof(GameRuntimeComponent).IsAssignableFrom(codec.ComponentRuntimeType))
                {
                    throw new InvalidOperationException($"Patch codec '{codec.ComponentTypeKey}' has invalid runtime type metadata.");
                }

                var fields = CollectSerializableFields(codec.ComponentRuntimeType);
                var result = new List<FieldMetadata>(fields.Count);
                for (var i = 0; i < fields.Count; i++)
                {
                    var field = fields[i];
                    var attribute = field.GetCustomAttribute<RuntimePatchFieldKeyAttribute>(inherit: false);
                    var key = attribute == null
                        ? $"{codec.ComponentTypeKey}/{field.Name}"
                        : attribute.Key?.Trim();
                    if (string.IsNullOrWhiteSpace(key)
                        || !codec.TryGetFieldId(key, out var fieldId)
                        || !codec.TryGetFieldKey(fieldId, out var roundTrip)
                        || !string.Equals(key, roundTrip, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException(
                            $"Generated codec '{codec.ComponentTypeKey}' has no exact active schema mapping for field '{field.Name}' key '{key}'.");
                    }
                    CanonicalJsonValueCodec.ValidateSupportedType(field.FieldType, new HashSet<Type>());
                    result.Add(new FieldMetadata(fieldId, key, field));
                }
                return new ComponentMetadata(codec.ComponentTypeKey, codec.ComponentRuntimeType, result);
            }

            public FieldMetadata GetField(string key)
            {
                if (_fieldsByKey.TryGetValue(key, out var field))
                    return field;
                throw new InvalidOperationException($"Component '{ComponentKey}' has no active authored field key '{key}'.");
            }

            public FieldMetadata GetField(string key, uint fieldId)
            {
                var field = GetField(key);
                if (field.Id != fieldId)
                    throw new InvalidOperationException($"Component '{ComponentKey}' field key '{key}' maps to id {field.Id}, not {fieldId}.");
                return field;
            }
        }

        private sealed class FieldMetadata
        {
            public uint Id { get; }
            public string Key { get; }
            public FieldInfo Field { get; }

            public FieldMetadata(uint id, string key, FieldInfo field)
            {
                Id = id;
                Key = key;
                Field = field;
            }
        }

        private static List<FieldInfo> CollectSerializableFields(Type type)
        {
            var reflected = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            var result = new List<FieldInfo>();
            for (var i = 0; i < reflected.Length; i++)
            {
                var field = reflected[i];
                if (field.IsStatic
                    || field.IsLiteral
                    || field.GetCustomAttribute<NonSerializedAttribute>(inherit: false) != null)
                {
                    continue;
                }
                var serialized = field.IsPublic
                                 || field.GetCustomAttribute<SerializeField>(inherit: false) != null
                                 || field.GetCustomAttribute<SerializeReference>(inherit: false) != null;
                if (!serialized)
                    continue;
                if (!field.IsPublic || field.IsInitOnly)
                {
                    throw new InvalidOperationException(
                        $"Canonical authoring field '{type.FullName}.{field.Name}' must be public and writable, matching generated codec rules.");
                }
                result.Add(field);
            }
            result.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            return result;
        }

        private static class CanonicalJsonValueCodec
        {
            private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

            public static string Encode(Type type, object value, RuntimePatchCodecContext context)
            {
                return Write(type, value, context).ToString(Formatting.None);
            }

            public static object Decode(Type type, string canonicalJson, RuntimePatchCodecContext context)
            {
                var value = Read(type, Parse(canonicalJson), context);
                var normalized = Encode(type, value, context);
                if (!string.Equals(normalized, canonicalJson, StringComparison.Ordinal))
                    throw new FormatException($"Authored JSON value for '{type.FullName}' is valid but not canonical.");
                return value;
            }

            public static JToken Parse(string canonicalJson)
            {
                if (canonicalJson == null)
                    throw new FormatException("Authored canonical JSON is null.");
                try
                {
                    return JToken.Parse(canonicalJson, new JsonLoadSettings
                    {
                        CommentHandling = CommentHandling.Load,
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error,
                        LineInfoHandling = LineInfoHandling.Ignore,
                    });
                }
                catch (JsonException exception)
                {
                    throw new FormatException("Authored canonical JSON is invalid.", exception);
                }
            }

            public static JToken Write(Type type, object value, RuntimePatchCodecContext context)
            {
                if (type == typeof(string))
                    return value == null ? JValue.CreateNull() : new JValue((string)value);
                if (type == typeof(bool)) return new JValue((bool)value);
                if (type == typeof(byte)) return new JValue((byte)value);
                if (type == typeof(sbyte)) return new JValue((sbyte)value);
                if (type == typeof(short)) return new JValue((short)value);
                if (type == typeof(ushort)) return new JValue((ushort)value);
                if (type == typeof(int)) return new JValue((int)value);
                if (type == typeof(uint)) return new JValue((uint)value);
                if (type == typeof(long)) return new JValue((long)value);
                if (type == typeof(ulong)) return new JValue((ulong)value);
                if (type == typeof(float))
                {
                    var number = (float)value;
                    RequireFinite(number, type);
                    return new JValue(number);
                }
                if (type == typeof(double))
                {
                    var number = (double)value;
                    RequireFinite(number, type);
                    return new JValue(number);
                }
                if (type.IsEnum)
                    return Write(Enum.GetUnderlyingType(type), Convert.ChangeType(value, Enum.GetUnderlyingType(type), Invariant), context);
                if (type == typeof(int2))
                {
                    var vector = (int2)value;
                    return Pair(new JValue(vector.x), new JValue(vector.y));
                }
                if (type == typeof(float2))
                {
                    var vector = (float2)value;
                    RequireFinite(vector.x, type);
                    RequireFinite(vector.y, type);
                    return Pair(new JValue(vector.x), new JValue(vector.y));
                }
                if (type == typeof(Vector2Int))
                {
                    var vector = (Vector2Int)value;
                    return Pair(new JValue(vector.x), new JValue(vector.y));
                }
                if (type == typeof(RuntimeInstance))
                    return WriteRuntimeInstance((RuntimeInstance)value, context);
                if (type == typeof(List<Vector2Int>))
                {
                    if (value == null)
                        return JValue.CreateNull();
                    var array = new JArray();
                    var list = (List<Vector2Int>)value;
                    for (var i = 0; i < list.Count; i++)
                        array.Add(Write(typeof(Vector2Int), list[i], context));
                    return array;
                }
                if (type.IsValueType && !type.IsGenericType)
                {
                    var result = new JObject();
                    var fields = CollectSerializableFields(type);
                    for (var i = 0; i < fields.Count; i++)
                        result.Add(fields[i].Name, Write(fields[i].FieldType, fields[i].GetValue(value), context));
                    return result;
                }
                throw new InvalidOperationException($"Canonical authoring JSON does not support '{type.FullName}'.");
            }

            public static object Read(Type type, JToken token, RuntimePatchCodecContext context)
            {
                if (type == typeof(string))
                {
                    if (token.Type == JTokenType.Null)
                        return null;
                    RequireType(token, JTokenType.String, type);
                    return token.Value<string>();
                }
                if (type == typeof(bool))
                {
                    RequireType(token, JTokenType.Boolean, type);
                    return token.Value<bool>();
                }
                if (type == typeof(byte)) return byte.Parse(IntegerText(token, type), NumberStyles.None, Invariant);
                if (type == typeof(sbyte)) return sbyte.Parse(IntegerText(token, type), NumberStyles.AllowLeadingSign, Invariant);
                if (type == typeof(short)) return short.Parse(IntegerText(token, type), NumberStyles.AllowLeadingSign, Invariant);
                if (type == typeof(ushort)) return ushort.Parse(IntegerText(token, type), NumberStyles.None, Invariant);
                if (type == typeof(int)) return int.Parse(IntegerText(token, type), NumberStyles.AllowLeadingSign, Invariant);
                if (type == typeof(uint)) return uint.Parse(IntegerText(token, type), NumberStyles.None, Invariant);
                if (type == typeof(long)) return long.Parse(IntegerText(token, type), NumberStyles.AllowLeadingSign, Invariant);
                if (type == typeof(ulong)) return ulong.Parse(IntegerText(token, type), NumberStyles.None, Invariant);
                if (type == typeof(float))
                {
                    var result = float.Parse(FloatText(token, type), NumberStyles.Float, Invariant);
                    RequireFinite(result, type);
                    return result;
                }
                if (type == typeof(double))
                {
                    var result = double.Parse(FloatText(token, type), NumberStyles.Float, Invariant);
                    RequireFinite(result, type);
                    return result;
                }
                if (type.IsEnum)
                    return Enum.ToObject(type, Read(Enum.GetUnderlyingType(type), token, context));
                if (type == typeof(int2))
                {
                    var pair = ReadPair(token, type);
                    return new int2((int)Read(typeof(int), pair.X, context), (int)Read(typeof(int), pair.Y, context));
                }
                if (type == typeof(float2))
                {
                    var pair = ReadPair(token, type);
                    return new float2((float)Read(typeof(float), pair.X, context), (float)Read(typeof(float), pair.Y, context));
                }
                if (type == typeof(Vector2Int))
                {
                    var pair = ReadPair(token, type);
                    return new Vector2Int((int)Read(typeof(int), pair.X, context), (int)Read(typeof(int), pair.Y, context));
                }
                if (type == typeof(RuntimeInstance))
                    return ReadRuntimeInstance(token, context);
                if (type == typeof(List<Vector2Int>))
                {
                    if (token.Type == JTokenType.Null)
                        return null;
                    if (token is not JArray array)
                        throw new FormatException($"Authored JSON for '{type.FullName}' must be an array or null.");
                    var list = new List<Vector2Int>(array.Count);
                    for (var i = 0; i < array.Count; i++)
                        list.Add((Vector2Int)Read(typeof(Vector2Int), array[i], context));
                    return list;
                }
                if (type.IsValueType && !type.IsGenericType)
                {
                    var json = RequireObject(token, type.FullName);
                    var fields = CollectSerializableFields(type);
                    RequireExactProperties(json, fields.Select(field => field.Name), type.FullName);
                    var result = Activator.CreateInstance(type);
                    for (var i = 0; i < fields.Count; i++)
                        fields[i].SetValue(result, Read(fields[i].FieldType, json[fields[i].Name], context));
                    return result;
                }
                throw new InvalidOperationException($"Canonical authoring JSON does not support '{type.FullName}'.");
            }

            public static void ValidateSupportedType(Type type, HashSet<Type> traversal)
            {
                if (type == typeof(bool)
                    || type == typeof(byte)
                    || type == typeof(sbyte)
                    || type == typeof(short)
                    || type == typeof(ushort)
                    || type == typeof(int)
                    || type == typeof(uint)
                    || type == typeof(long)
                    || type == typeof(ulong)
                    || type == typeof(float)
                    || type == typeof(double)
                    || type == typeof(string)
                    || type.IsEnum
                    || type == typeof(int2)
                    || type == typeof(float2)
                    || type == typeof(Vector2Int)
                    || type == typeof(RuntimeInstance)
                    || type == typeof(List<Vector2Int>))
                {
                    return;
                }
                if (!type.IsValueType || type.IsGenericType || !traversal.Add(type))
                    throw new InvalidOperationException($"Canonical authoring JSON does not support field graph '{type.FullName}'.");
                try
                {
                    var fields = CollectSerializableFields(type);
                    if (fields.Count == 0)
                        throw new InvalidOperationException($"Canonical authoring struct '{type.FullName}' has no serialized fields.");
                    for (var i = 0; i < fields.Count; i++)
                        ValidateSupportedType(fields[i].FieldType, traversal);
                }
                finally
                {
                    traversal.Remove(type);
                }
            }

            public static JObject RequireObject(JToken token, string label)
            {
                if (token is JObject value)
                    return value;
                throw new FormatException($"Authored JSON for '{label}' must be an object.");
            }

            public static void RequireExactProperties(JObject json, IEnumerable<string> expectedNames, string label)
            {
                var expected = new HashSet<string>(expectedNames, StringComparer.Ordinal);
                var actual = json.Properties().Select(property => property.Name).ToArray();
                if (actual.Length != expected.Count)
                    throw new FormatException($"Authored JSON for '{label}' has a missing or unknown property.");
                for (var i = 0; i < actual.Length; i++)
                {
                    if (!expected.Remove(actual[i]))
                        throw new FormatException($"Authored JSON for '{label}' has unknown property '{actual[i]}'.");
                }
                if (expected.Count != 0)
                    throw new FormatException($"Authored JSON for '{label}' is missing required properties.");
            }

            private static JToken WriteRuntimeInstance(RuntimeInstance value, RuntimePatchCodecContext context)
            {
                if (RuntimePersistentPatchCodecContext.IsDefaultRuntimeInstance(value))
                    return JValue.CreateNull();
                var persistent = RequirePersistentContext(context).EncodePersistentReference(value);
                return new JObject
                {
                    ["objectGuid"] = persistent.ObjectGuid.ToString(),
                    ["storeId"] = persistent.StoreId.ToString(),
                };
            }

            private static RuntimeInstance ReadRuntimeInstance(JToken token, RuntimePatchCodecContext context)
            {
                if (token.Type == JTokenType.Null)
                    return default;
                var json = RequireObject(token, nameof(RuntimeInstance));
                RequireExactProperties(json, new[] { "objectGuid", "storeId" }, nameof(RuntimeInstance));
                var guidText = RequireString(json["objectGuid"], "RuntimeInstance.objectGuid");
                var storeId = RequireString(json["storeId"], "RuntimeInstance.storeId");
                if (string.IsNullOrWhiteSpace(storeId))
                    throw new FormatException("RuntimeInstance.storeId cannot be empty.");
                var guid = ParseCanonicalHash128(guidText, "RuntimeInstance.objectGuid");
                return RequirePersistentContext(context).DecodePersistentReference(
                    new RuntimePatchObjectReference(new FixedString32Bytes(storeId), guid));
            }

            private static RuntimePersistentPatchCodecContext RequirePersistentContext(RuntimePatchCodecContext context)
            {
                if (context is RuntimePersistentPatchCodecContext persistent)
                    return persistent;
                throw new InvalidOperationException(
                    "Canonical RuntimeInstance JSON requires RuntimePersistentPatchCodecContext so it stores StoreId + object GUID without Epoch.");
            }

            private static JObject Pair(JToken x, JToken y)
            {
                return new JObject { ["x"] = x, ["y"] = y };
            }

            private static (JToken X, JToken Y) ReadPair(JToken token, Type type)
            {
                var json = RequireObject(token, type.FullName);
                RequireExactProperties(json, new[] { "x", "y" }, type.FullName);
                return (json["x"], json["y"]);
            }

            private static string IntegerText(JToken token, Type type)
            {
                RequireType(token, JTokenType.Integer, type);
                return token.ToString(Formatting.None);
            }

            private static string FloatText(JToken token, Type type)
            {
                if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
                    throw new FormatException($"Authored JSON for '{type.FullName}' must be a number.");
                return token.ToString(Formatting.None);
            }

            private static string RequireString(JToken token, string label)
            {
                if (token?.Type != JTokenType.String)
                    throw new FormatException($"Authored JSON '{label}' must be a string.");
                return token.Value<string>();
            }

            private static void RequireType(JToken token, JTokenType expected, Type type)
            {
                if (token == null || token.Type != expected)
                    throw new FormatException($"Authored JSON for '{type.FullName}' must be {expected}.");
            }

            private static Hash128 ParseCanonicalHash128(string value, string label)
            {
                if (value == null
                    || value.Length != 32
                    || value.Any(c => ((c < '0' || c > '9') && (c < 'a' || c > 'f'))))
                {
                    throw new FormatException($"{label} must be 32 lowercase hex characters.");
                }
                var parsed = Hash128.Parse(value);
                if (!parsed.isValid || !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
                    throw new FormatException($"{label} is not a canonical GUID.");
                return parsed;
            }

            private static void RequireFinite(float value, Type type)
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                    throw new InvalidOperationException($"Canonical JSON does not support non-finite '{type.FullName}' values.");
            }

            private static void RequireFinite(double value, Type type)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    throw new InvalidOperationException($"Canonical JSON does not support non-finite '{type.FullName}' values.");
            }
        }
    }
}
