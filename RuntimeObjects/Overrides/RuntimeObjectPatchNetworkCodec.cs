using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    /// <summary>
    /// Protocol-v2 patch encoding. Stable authoring keys deliberately do not
    /// travel on the wire: the session schema hash fixes the numeric id table.
    /// </summary>
    public sealed class RuntimeObjectPatchNetworkCodec
    {
        public const uint FORMAT_MAGIC = 0x32504147;
        public const uint FORMAT_VERSION = 1;
        public const int MAX_COMPONENT_PATCHES = 4096;
        public const int MAX_FIELD_PATCHES_PER_COMPONENT = 4096;
        public const int MAX_PAYLOAD_BYTES = 16 * 1024 * 1024;

        private readonly RuntimePatchCodecRegistry _registry;

        public RuntimeObjectPatchNetworkCodec(RuntimePatchCodecRegistry registry)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        }

        public byte[] Encode(RuntimeObjectPatch patch)
        {
            if (patch == null)
                throw new ArgumentNullException(nameof(patch));
            if (!string.Equals(patch.SchemaHash, _registry.SchemaHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Patch schema '{patch.SchemaHash}' does not match active runtime schema '{_registry.SchemaHash}'.");
            if (patch.Representation != RuntimeObjectPatchRepresentation.RuntimeBinary)
                throw new InvalidOperationException($"Network patch codec cannot encode representation {patch.Representation}.");

            var components = NormalizeComponents(patch.Components);
            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(FORMAT_MAGIC);
            writer.WriteUInt32(FORMAT_VERSION);
            writer.WriteInt32(components.Count);
            for (var i = 0; i < components.Count; i++)
                WriteComponent(writer, components[i]);

            var payload = writer.ToArray();
            if (payload.Length > MAX_PAYLOAD_BYTES)
                throw new InvalidOperationException($"Runtime object patch is {payload.Length} bytes; maximum is {MAX_PAYLOAD_BYTES} bytes.");
            return payload;
        }

        public RuntimeObjectPatch Decode(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length > MAX_PAYLOAD_BYTES)
                throw new FormatException($"Runtime object patch is {payload.Length} bytes; maximum is {MAX_PAYLOAD_BYTES} bytes.");

            var reader = new CanonicalPatchBinaryReader(payload);
            var magic = reader.ReadUInt32();
            if (magic != FORMAT_MAGIC)
                throw new FormatException($"Runtime object network patch magic 0x{magic:x8} does not match 0x{FORMAT_MAGIC:x8}.");
            var version = reader.ReadUInt32();
            if (version != FORMAT_VERSION)
                throw new FormatException($"Runtime object network patch version {version} is not supported.");

            var componentCount = ReadBoundedCount(reader, MAX_COMPONENT_PATCHES, "component patch");
            var result = new RuntimeObjectPatch(_registry.SchemaHash);
            var componentIds = new HashSet<uint>();
            for (var i = 0; i < componentCount; i++)
            {
                var component = ReadComponent(reader);
                if (!componentIds.Add(component.ComponentTypeId))
                    throw new FormatException($"Runtime object network patch contains duplicate component id {component.ComponentTypeId}.");
                result.Components.Add(component);
            }

            result.Components.Sort((left, right) => left.ComponentTypeId.CompareTo(right.ComponentTypeId));
            reader.RequireEnd();
            return result;
        }

        private void WriteComponent(CanonicalPatchBinaryWriter writer, ComponentPatch component)
        {
            if (component == null)
                throw new InvalidOperationException("Runtime object patch cannot contain null components.");
            if (component.CanonicalJson != null)
                throw new InvalidOperationException($"Runtime-binary component {component.ComponentTypeId} cannot contain authoring JSON.");
            var codec = _registry.Get(component.ComponentTypeId);
            if (!string.Equals(component.ComponentTypeKey, codec.ComponentTypeKey, StringComparison.Ordinal))
                throw new InvalidOperationException($"Component id {component.ComponentTypeId} has authoring key '{component.ComponentTypeKey}', expected '{codec.ComponentTypeKey}'.");

            writer.WriteUInt32(component.ComponentTypeId);
            writer.WriteByte((byte)component.Kind);
            switch (component.Kind)
            {
                case ComponentPatchKind.Add:
                case ComponentPatchKind.Custom:
                    if (component.Payload == null || (component.Fields?.Count ?? 0) != 0)
                        throw new InvalidOperationException($"Component {component.ComponentTypeId} {component.Kind} requires a canonical payload and no fields.");
                    writer.WriteBytes(component.Payload);
                    return;

                case ComponentPatchKind.Remove:
                    if (component.Payload != null || (component.Fields?.Count ?? 0) != 0)
                        throw new InvalidOperationException($"Component {component.ComponentTypeId} remove cannot contain payload or fields.");
                    return;

                case ComponentPatchKind.Fields:
                    if (component.Payload != null)
                        throw new InvalidOperationException($"Component {component.ComponentTypeId} fields patch cannot contain a component payload.");
                    var fields = NormalizeFields(component.Fields);
                    if (fields.Count == 0)
                        throw new InvalidOperationException($"Component {component.ComponentTypeId} fields patch is empty.");
                    writer.WriteInt32(fields.Count);
                    for (var i = 0; i < fields.Count; i++)
                        WriteField(writer, codec, fields[i]);
                    return;

                default:
                    throw new InvalidOperationException($"Unsupported component patch kind {component.Kind}.");
            }
        }

        private ComponentPatch ReadComponent(CanonicalPatchBinaryReader reader)
        {
            var componentTypeId = reader.ReadUInt32();
            var codec = _registry.Get(componentTypeId);
            var kind = (ComponentPatchKind)reader.ReadByte();
            var result = new ComponentPatch(componentTypeId, codec.ComponentTypeKey, kind);
            switch (kind)
            {
                case ComponentPatchKind.Add:
                case ComponentPatchKind.Custom:
                    result.Payload = reader.ReadBytes();
                    if (result.Payload == null)
                        throw new FormatException($"Component {componentTypeId} {kind} has a null payload.");
                    return result;

                case ComponentPatchKind.Remove:
                    return result;

                case ComponentPatchKind.Fields:
                    var fieldCount = ReadBoundedCount(reader, MAX_FIELD_PATCHES_PER_COMPONENT, $"field patch for component {componentTypeId}");
                    if (fieldCount == 0)
                        throw new FormatException($"Component {componentTypeId} fields patch is empty.");
                    var fieldIds = new HashSet<uint>();
                    for (var i = 0; i < fieldCount; i++)
                    {
                        var field = ReadField(reader, codec, componentTypeId);
                        if (!fieldIds.Add(field.FieldId))
                            throw new FormatException($"Component {componentTypeId} contains duplicate field id {field.FieldId}.");
                        result.Fields.Add(field);
                    }
                    result.Fields.Sort((left, right) => left.FieldId.CompareTo(right.FieldId));
                    return result;

                default:
                    throw new FormatException($"Unsupported component patch kind {kind}.");
            }
        }

        private static void WriteField(
            CanonicalPatchBinaryWriter writer,
            RuntimeComponentPatchCodec codec,
            FieldPatch field)
        {
            if (field == null)
                throw new InvalidOperationException("Component patch cannot contain null fields.");
            if (field.CanonicalJson != null)
                throw new InvalidOperationException($"Runtime-binary field {field.FieldId} cannot contain authoring JSON.");
            if (!codec.TryGetFieldKey(field.FieldId, out var expectedKey))
                throw new InvalidOperationException($"Component {codec.ComponentTypeId} has no field id {field.FieldId}.");
            if (!string.Equals(field.FieldKey, expectedKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Field id {field.FieldId} has authoring key '{field.FieldKey}', expected '{expectedKey}'.");
            }
            writer.WriteUInt32(field.FieldId);
            writer.WriteByte((byte)field.Kind);
            switch (field.Kind)
            {
                case FieldPatchKind.Set:
                    if (field.Payload == null)
                        throw new InvalidOperationException($"Field {field.FieldId} set requires a canonical payload.");
                    writer.WriteBytes(field.Payload);
                    return;
                case FieldPatchKind.Remove:
                    if (field.Payload != null)
                        throw new InvalidOperationException($"Field {field.FieldId} remove cannot contain a payload.");
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported field patch kind {field.Kind}.");
            }
        }

        private static FieldPatch ReadField(
            CanonicalPatchBinaryReader reader,
            RuntimeComponentPatchCodec codec,
            uint componentTypeId)
        {
            var fieldId = reader.ReadUInt32();
            if (!codec.TryGetFieldKey(fieldId, out var fieldKey))
                throw new FormatException($"Component {componentTypeId} has no field id {fieldId} in the active runtime schema.");
            var kind = (FieldPatchKind)reader.ReadByte();
            return kind switch
            {
                FieldPatchKind.Set => new FieldPatch(fieldId, fieldKey, kind, RequirePayload(reader.ReadBytes(), fieldId)),
                FieldPatchKind.Remove => new FieldPatch(fieldId, fieldKey, kind),
                _ => throw new FormatException($"Unsupported field patch kind {kind}.")
            };
        }

        private static byte[] RequirePayload(byte[] payload, uint fieldId)
        {
            if (payload == null)
                throw new FormatException($"Field {fieldId} set has a null payload.");
            return payload;
        }

        private static List<ComponentPatch> NormalizeComponents(IReadOnlyCollection<ComponentPatch> source)
        {
            var result = source == null ? new List<ComponentPatch>() : new List<ComponentPatch>(source);
            if (result.Count > MAX_COMPONENT_PATCHES)
                throw new InvalidOperationException($"Runtime object patch contains {result.Count} components; maximum is {MAX_COMPONENT_PATCHES}.");
            result.Sort((left, right) =>
            {
                if (left == null || right == null)
                    return left == right ? 0 : left == null ? -1 : 1;
                return left.ComponentTypeId.CompareTo(right.ComponentTypeId);
            });
            for (var i = 0; i < result.Count; i++)
            {
                if (result[i] == null)
                    throw new InvalidOperationException("Runtime object patch cannot contain null components.");
                if (i > 0 && result[i - 1].ComponentTypeId == result[i].ComponentTypeId)
                    throw new InvalidOperationException($"Runtime object patch contains duplicate component id {result[i].ComponentTypeId}.");
            }
            return result;
        }

        private static List<FieldPatch> NormalizeFields(IReadOnlyCollection<FieldPatch> source)
        {
            var result = source == null ? new List<FieldPatch>() : new List<FieldPatch>(source);
            if (result.Count > MAX_FIELD_PATCHES_PER_COMPONENT)
                throw new InvalidOperationException($"Component patch contains {result.Count} fields; maximum is {MAX_FIELD_PATCHES_PER_COMPONENT}.");
            result.Sort((left, right) =>
            {
                if (left == null || right == null)
                    return left == right ? 0 : left == null ? -1 : 1;
                return left.FieldId.CompareTo(right.FieldId);
            });
            for (var i = 0; i < result.Count; i++)
            {
                if (result[i] == null)
                    throw new InvalidOperationException("Component patch cannot contain null fields.");
                if (i > 0 && result[i - 1].FieldId == result[i].FieldId)
                    throw new InvalidOperationException($"Component patch contains duplicate field id {result[i].FieldId}.");
            }
            return result;
        }

        private static int ReadBoundedCount(CanonicalPatchBinaryReader reader, int maximum, string label)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > maximum)
                throw new FormatException($"Invalid {label} count {count}; expected 0..{maximum}.");
            return count;
        }
    }
}
