using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public class RuntimeObjectPatchBinaryCodec
    {
        public const uint FORMAT_MAGIC = 0x31504147;
        public const uint FORMAT_VERSION = 1;

        public byte[] Encode(RuntimeObjectPatch patch)
        {
            if (patch == null)
                throw new ArgumentNullException(nameof(patch));
            if (string.IsNullOrWhiteSpace(patch.SchemaHash))
                throw new InvalidOperationException("Runtime object patch schema hash is required.");
            if (patch.Representation != RuntimeObjectPatchRepresentation.RuntimeBinary)
                throw new InvalidOperationException($"Binary patch codec cannot encode representation {patch.Representation}.");

            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(FORMAT_MAGIC);
            writer.WriteUInt32(FORMAT_VERSION);
            writer.WriteString(patch.SchemaHash);

            var components = patch.Components == null
                ? new List<ComponentPatch>()
                : new List<ComponentPatch>(patch.Components);
            NormalizeComponents(components);
            writer.WriteInt32(components.Count);
            for (var i = 0; i < components.Count; i++)
            {
                WriteComponentPatch(writer, components[i]);
            }
            return writer.ToArray();
        }

        public RuntimeObjectPatch Decode(byte[] payload)
        {
            var reader = new CanonicalPatchBinaryReader(payload);
            var magic = reader.ReadUInt32();
            if (magic != FORMAT_MAGIC)
                throw new FormatException($"Runtime object patch magic 0x{magic:x8} does not match 0x{FORMAT_MAGIC:x8}.");

            var version = reader.ReadUInt32();
            if (version != FORMAT_VERSION)
                throw new FormatException($"Runtime object patch format version {version} is not supported.");

            var result = new RuntimeObjectPatch(reader.ReadString());
            var componentCount = ReadCount(reader, "component patch");
            for (var i = 0; i < componentCount; i++)
            {
                result.Components.Add(ReadComponentPatch(reader));
            }
            NormalizeComponents(result.Components);
            reader.RequireEnd();
            return result;
        }

        private static void WriteComponentPatch(CanonicalPatchBinaryWriter writer, ComponentPatch component)
        {
            ValidateComponentKind(component.Kind);
            ValidateComponentShape(component);
            writer.WriteUInt32(component.ComponentTypeId);
            writer.WriteString(component.ComponentTypeKey);
            writer.WriteByte((byte)component.Kind);
            writer.WriteBytes(component.Payload);

            var fields = component.Fields == null
                ? new List<FieldPatch>()
                : new List<FieldPatch>(component.Fields);
            NormalizeFields(fields);
            writer.WriteInt32(fields.Count);
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                ValidateFieldKind(field.Kind);
                ValidateFieldShape(field);
                writer.WriteUInt32(field.FieldId);
                writer.WriteString(field.FieldKey);
                writer.WriteByte((byte)field.Kind);
                writer.WriteBytes(field.Payload);
            }
        }

        private static ComponentPatch ReadComponentPatch(CanonicalPatchBinaryReader reader)
        {
            var componentTypeId = reader.ReadUInt32();
            var componentTypeKey = reader.ReadString();
            var kind = (ComponentPatchKind)reader.ReadByte();
            ValidateComponentKind(kind);
            var result = new ComponentPatch(componentTypeId, componentTypeKey, kind, reader.ReadBytes());
            var fieldCount = ReadCount(reader, "field patch");
            for (var i = 0; i < fieldCount; i++)
            {
                var fieldId = reader.ReadUInt32();
                var fieldKey = reader.ReadString();
                var fieldKind = (FieldPatchKind)reader.ReadByte();
                ValidateFieldKind(fieldKind);
                result.Fields.Add(new FieldPatch(fieldId, fieldKey, fieldKind, reader.ReadBytes()));
            }
            NormalizeFields(result.Fields);
            ValidateComponentShape(result);
            return result;
        }

        private static int ReadCount(CanonicalPatchBinaryReader reader, string name)
        {
            var value = reader.ReadInt32();
            if (value < 0)
                throw new FormatException($"Runtime object patch has negative {name} count {value}.");
            return value;
        }

        private static void NormalizeComponents(List<ComponentPatch> components)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < components.Count; i++)
            {
                if (components[i] == null)
                    throw new InvalidOperationException("Runtime object patch cannot contain null component patches.");
                if (string.IsNullOrWhiteSpace(components[i].ComponentTypeKey))
                    throw new InvalidOperationException($"Runtime object component patch {components[i].ComponentTypeId} has no stable component key.");
                if (!keys.Add(components[i].ComponentTypeKey))
                    throw new InvalidOperationException($"Runtime object patch contains duplicate component type key '{components[i].ComponentTypeKey}'.");
            }
            components.Sort(CompareComponents);
            for (var i = 1; i < components.Count; i++)
            {
                if (components[i - 1].ComponentTypeId == components[i].ComponentTypeId)
                    throw new InvalidOperationException($"Runtime object patch contains duplicate component type id {components[i].ComponentTypeId}.");
            }
        }

        private static void NormalizeFields(List<FieldPatch> fields)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i] == null)
                    throw new InvalidOperationException("Component patch cannot contain null field patches.");
                if (string.IsNullOrWhiteSpace(fields[i].FieldKey))
                    throw new InvalidOperationException($"Component field patch {fields[i].FieldId} has no stable field key.");
                if (!keys.Add(fields[i].FieldKey))
                    throw new InvalidOperationException($"Component patch contains duplicate field key '{fields[i].FieldKey}'.");
            }
            fields.Sort(CompareFields);
            for (var i = 1; i < fields.Count; i++)
            {
                if (fields[i - 1].FieldId == fields[i].FieldId)
                    throw new InvalidOperationException($"Component patch contains duplicate field id {fields[i].FieldId}.");
            }
        }

        private static void ValidateComponentKind(ComponentPatchKind kind)
        {
            if (kind != ComponentPatchKind.Add
                && kind != ComponentPatchKind.Remove
                && kind != ComponentPatchKind.Fields
                && kind != ComponentPatchKind.Custom)
                throw new FormatException($"Unsupported component patch kind {kind}.");
        }

        private static void ValidateFieldKind(FieldPatchKind kind)
        {
            if (kind != FieldPatchKind.Set && kind != FieldPatchKind.Remove)
                throw new FormatException($"Unsupported field patch kind {kind}.");
        }

        private static void ValidateComponentShape(ComponentPatch component)
        {
            if (component.CanonicalJson != null)
                throw new FormatException($"Runtime-binary component {component.ComponentTypeId} cannot contain authoring JSON.");
            var fieldCount = component.Fields?.Count ?? 0;
            switch (component.Kind)
            {
                case ComponentPatchKind.Add:
                case ComponentPatchKind.Custom:
                    if (component.Payload == null || fieldCount != 0)
                        throw new FormatException($"Component {component.ComponentTypeId} {component.Kind} patch requires one canonical payload and no field patches.");
                    return;

                case ComponentPatchKind.Remove:
                    if (component.Payload != null || fieldCount != 0)
                        throw new FormatException($"Component {component.ComponentTypeId} remove patch cannot contain payload data.");
                    return;

                case ComponentPatchKind.Fields:
                    if (component.Payload != null || fieldCount == 0)
                        throw new FormatException($"Component {component.ComponentTypeId} field patch requires at least one field and no component payload.");
                    return;

                default:
                    throw new FormatException($"Unsupported component patch kind {component.Kind}.");
            }
        }

        private static void ValidateFieldShape(FieldPatch field)
        {
            if (field.CanonicalJson != null)
                throw new FormatException($"Runtime-binary field {field.FieldId} cannot contain authoring JSON.");
            if (field.Kind == FieldPatchKind.Set && field.Payload == null)
                throw new FormatException($"Field {field.FieldId} set patch requires a canonical payload.");
            if (field.Kind == FieldPatchKind.Remove && field.Payload != null)
                throw new FormatException($"Field {field.FieldId} remove patch cannot contain a payload.");
        }

        private static int CompareComponents(ComponentPatch first, ComponentPatch second)
        {
            var byId = first.ComponentTypeId.CompareTo(second.ComponentTypeId);
            return byId != 0 ? byId : string.CompareOrdinal(first.ComponentTypeKey, second.ComponentTypeKey);
        }

        private static int CompareFields(FieldPatch first, FieldPatch second)
        {
            var byId = first.FieldId.CompareTo(second.FieldId);
            return byId != 0 ? byId : string.CompareOrdinal(first.FieldKey, second.FieldKey);
        }
    }
}
