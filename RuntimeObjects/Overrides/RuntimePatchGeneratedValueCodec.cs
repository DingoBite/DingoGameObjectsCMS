using System;
using System.Collections.Generic;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public static class RuntimePatchGeneratedValueCodec
    {
        public const int MAX_COLLECTION_ELEMENTS = 1_048_576;
        public const int MAX_NESTED_PATCH_BYTES = 64 * 1024 * 1024;

        private const uint AUTHORING_PATCH_MAGIC = 0x31415047;
        private const uint AUTHORING_PATCH_VERSION = 2;

        public static void RequireCollectionCountForWrite(int count)
        {
            if (count < 0 || count > MAX_COLLECTION_ELEMENTS)
            {
                throw new InvalidOperationException(
                    $"Runtime patch collection count {count} is outside supported range 0..{MAX_COLLECTION_ELEMENTS}.");
            }
        }

        public static int ReadCollectionCount(CanonicalPatchBinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            var count = reader.ReadInt32();
            if (count < -1 || count > MAX_COLLECTION_ELEMENTS)
            {
                throw new FormatException(
                    $"Canonical collection count {count} is outside supported range -1..{MAX_COLLECTION_ELEMENTS}.");
            }
            return count;
        }

        public static Hash128 ReadHash128(CanonicalPatchBinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            return reader.ReadHash128();
        }

        public static void WriteRuntimeObjectPatch(
            CanonicalPatchBinaryWriter writer,
            RuntimeObjectPatch patch)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (patch == null)
            {
                writer.WriteByte(0);
                return;
            }

            var payload = EncodeRuntimeObjectPatch(patch);
            if (payload.Length > MAX_NESTED_PATCH_BYTES)
            {
                throw new InvalidOperationException(
                    $"Nested runtime object patch contains {payload.Length} bytes; maximum is {MAX_NESTED_PATCH_BYTES}.");
            }
            writer.WriteByte(1);
            writer.WriteBytes(payload);
        }

        public static RuntimeObjectPatch ReadRuntimeObjectPatch(CanonicalPatchBinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            var presence = reader.ReadByte();
            if (presence == 0)
                return null;
            if (presence != 1)
                throw new FormatException($"Invalid nested runtime object patch presence marker {presence}.");
            var payload = reader.ReadBytes(MAX_NESTED_PATCH_BYTES, "nested runtime object patch");
            if (payload == null)
                throw new FormatException("Present nested runtime object patch cannot have a null payload.");
            return DecodeRuntimeObjectPatch(payload);
        }

        public static RuntimeObjectPatch CloneRuntimeObjectPatch(RuntimeObjectPatch patch)
        {
            return patch == null ? null : DecodeRuntimeObjectPatch(EncodeRuntimeObjectPatch(patch));
        }

        public static bool RuntimeObjectPatchesEqual(RuntimeObjectPatch first, RuntimeObjectPatch second)
        {
            if (ReferenceEquals(first, second))
                return true;
            if (first == null || second == null)
                return false;
            var firstBytes = EncodeRuntimeObjectPatch(first);
            var secondBytes = EncodeRuntimeObjectPatch(second);
            if (firstBytes.Length != secondBytes.Length)
                return false;
            for (var i = 0; i < firstBytes.Length; i++)
            {
                if (firstBytes[i] != secondBytes[i])
                    return false;
            }
            return true;
        }

        public static byte[] EncodeRuntimeObjectPatch(RuntimeObjectPatch patch)
        {
            if (patch == null)
                throw new ArgumentNullException(nameof(patch));
            var result = patch.Representation switch
            {
                RuntimeObjectPatchRepresentation.RuntimeBinary =>
                    Wrap(
                        RuntimeObjectPatchRepresentation.RuntimeBinary,
                        new RuntimeObjectPatchBinaryCodec().Encode(patch)),
                RuntimeObjectPatchRepresentation.AuthoringCanonicalJson =>
                    Wrap(
                        RuntimeObjectPatchRepresentation.AuthoringCanonicalJson,
                        EncodeAuthoringPatch(patch)),
                _ => throw new InvalidOperationException(
                    $"Nested runtime object patch has unsupported representation {patch.Representation}."),
            };
            if (result.Length > MAX_NESTED_PATCH_BYTES)
            {
                throw new InvalidOperationException(
                    $"Nested runtime object patch contains {result.Length} bytes; maximum is {MAX_NESTED_PATCH_BYTES}.");
            }
            return result;
        }

        public static RuntimeObjectPatch DecodeRuntimeObjectPatch(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length > MAX_NESTED_PATCH_BYTES)
            {
                throw new FormatException(
                    $"Nested runtime object patch contains {payload.Length} bytes; maximum is {MAX_NESTED_PATCH_BYTES}.");
            }
            var reader = new CanonicalPatchBinaryReader(payload);
            var representation = (RuntimeObjectPatchRepresentation)reader.ReadByte();
            var inner = reader.ReadBytes(MAX_NESTED_PATCH_BYTES, "nested runtime object patch body");
            if (inner == null)
                throw new FormatException("Nested runtime object patch body cannot be null.");
            reader.RequireEnd();
            return representation switch
            {
                RuntimeObjectPatchRepresentation.RuntimeBinary =>
                    new RuntimeObjectPatchBinaryCodec().Decode(inner),
                RuntimeObjectPatchRepresentation.AuthoringCanonicalJson =>
                    DecodeAuthoringPatch(inner),
                _ => throw new FormatException(
                    $"Nested runtime object patch has unsupported representation {representation}."),
            };
        }

        private static byte[] Wrap(RuntimeObjectPatchRepresentation representation, byte[] payload)
        {
            var writer = new CanonicalPatchBinaryWriter(payload.Length + 8);
            writer.WriteByte((byte)representation);
            writer.WriteBytes(payload);
            return writer.ToArray();
        }

        private static byte[] EncodeAuthoringPatch(RuntimeObjectPatch patch)
        {
            var canonical = RuntimeObjectPatchAuthoringCodec.ClonePatch(patch);
            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(AUTHORING_PATCH_MAGIC);
            writer.WriteUInt32(AUTHORING_PATCH_VERSION);
            writer.WriteString(canonical.SchemaHash);
            RuntimePatchGeneratedValueCodec.RequireCollectionCountForWrite(canonical.Components.Count);
            writer.WriteInt32(canonical.Components.Count);
            for (var componentIndex = 0; componentIndex < canonical.Components.Count; componentIndex++)
            {
                var component = canonical.Components[componentIndex];
                writer.WriteUInt32(component.ComponentTypeId);
                writer.WriteByte((byte)component.Kind);
                writer.WriteString(component.CanonicalJson);
                RuntimePatchGeneratedValueCodec.RequireCollectionCountForWrite(component.Fields.Count);
                writer.WriteInt32(component.Fields.Count);
                for (var fieldIndex = 0; fieldIndex < component.Fields.Count; fieldIndex++)
                {
                    var field = component.Fields[fieldIndex];
                    writer.WriteUInt32(field.FieldId);
                    writer.WriteByte((byte)field.Kind);
                    writer.WriteString(field.CanonicalJson);
                }
            }
            return writer.ToArray();
        }

        private static RuntimeObjectPatch DecodeAuthoringPatch(byte[] payload)
        {
            var reader = new CanonicalPatchBinaryReader(payload);
            var magic = reader.ReadUInt32();
            if (magic != AUTHORING_PATCH_MAGIC)
            {
                throw new FormatException(
                    $"Authoring runtime object patch magic 0x{magic:x8} does not match 0x{AUTHORING_PATCH_MAGIC:x8}.");
            }
            var version = reader.ReadUInt32();
            if (version != AUTHORING_PATCH_VERSION)
                throw new FormatException($"Authoring runtime object patch version {version} is not supported.");

            var result = new RuntimeObjectPatch(
                reader.ReadString(),
                RuntimeObjectPatchRepresentation.AuthoringCanonicalJson);
            var componentCount = ReadRequiredCount(reader, "component");
            for (var componentIndex = 0; componentIndex < componentCount; componentIndex++)
            {
                var component = ComponentPatch.Authoring(
                    reader.ReadUInt32(),
                    (ComponentPatchKind)reader.ReadByte(),
                    reader.ReadString());
                var fieldCount = ReadRequiredCount(reader, "field");
                for (var fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                {
                    component.Fields.Add(FieldPatch.Authoring(
                        reader.ReadUInt32(),
                        (FieldPatchKind)reader.ReadByte(),
                        reader.ReadString()));
                }
                result.Components.Add(component);
            }
            reader.RequireEnd();
            return RuntimeObjectPatchAuthoringCodec.ClonePatch(result);
        }

        private static int ReadRequiredCount(CanonicalPatchBinaryReader reader, string label)
        {
            var count = ReadCollectionCount(reader);
            if (count < 0)
                throw new FormatException($"Nested authoring patch {label} count cannot be null.");
            return count;
        }

    }
}
