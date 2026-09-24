using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class CanonicalGoldenComponent_GRC : GameRuntimeComponent { }

    public class CanonicalGoldenComponentPatchCodec : RuntimeComponentPatchCodec
    {
        public override Type ComponentRuntimeType => typeof(CanonicalGoldenComponent_GRC);
        public override uint FieldCount => 0;

        public CanonicalGoldenComponentPatchCodec() : base(0x01020304) { }

        public override GameRuntimeComponent Clone(GameRuntimeComponent value) => value;
        public override GameRuntimeComponent CreateDefault() => new CanonicalGoldenComponent_GRC();
        protected override void WriteCanonical(CanonicalPatchBinaryWriter writer, GameRuntimeComponent value, RuntimePatchCodecContext context) { }
        protected override GameRuntimeComponent ReadCanonical(CanonicalPatchBinaryReader reader, RuntimePatchCodecContext context) => new CanonicalGoldenComponent_GRC();
        protected override void CollectFieldPatches(GameRuntimeComponent baseline, GameRuntimeComponent current, List<FieldPatch> fields, RuntimePatchCodecContext context) { }
        protected override void ApplyFieldPatch(GameRuntimeComponent target, FieldPatch fieldPatch, RuntimePatchCodecContext context) { }
    }

    public class CanonicalBinaryGoldenTests
    {
        [Test]
        public void PrimitiveWriter_UsesExistingCanonicalBytesAndReaderAcceptsThem()
        {
            // Captured from the existing CanonicalPatchBinaryWriter format: little-endian primitives, canonical zero/NaN, UTF-8 and -1 null lengths.
            var golden = Hex("AB 01 00 FE FF FF FF 78 56 34 12 FE FF FF FF FF FF FF FF 08 07 06 05 04 03 02 01 00 00 00 00 00 00 C0 7F 00 00 00 00 00 00 F8 7F FF FF FF FF 02 00 00 00 C3 A9 FF FF FF FF 02 00 00 00 A1 B2");
            using var writer = new CanonicalPatchBinaryWriter();
            writer.WriteByte(0xAB);
            writer.WriteBoolean(true);
            writer.WriteBoolean(false);
            writer.WriteInt32(-2);
            writer.WriteUInt32(0x12345678);
            writer.WriteInt64(-2);
            writer.WriteUInt64(0x0102030405060708UL);
            writer.WriteSingle(-0f);
            writer.WriteSingle(float.NaN);
            writer.WriteDouble(double.NaN);
            writer.WriteString(null);
            writer.WriteString("é");
            writer.WriteBytes(null);
            writer.WriteBytes(new byte[] { 0xA1, 0xB2 });

            Assert.That(writer.ToArray(), Is.EqualTo(golden));

            var reader = new CanonicalPatchBinaryReader(golden);
            Assert.That(reader.ReadByte(), Is.EqualTo(0xAB));
            Assert.That(reader.ReadBoolean(), Is.True);
            Assert.That(reader.ReadBoolean(), Is.False);
            Assert.That(reader.ReadInt32(), Is.EqualTo(-2));
            Assert.That(reader.ReadUInt32(), Is.EqualTo(0x12345678u));
            Assert.That(reader.ReadInt64(), Is.EqualTo(-2L));
            Assert.That(reader.ReadUInt64(), Is.EqualTo(0x0102030405060708UL));
            Assert.That(reader.ReadSingle(), Is.EqualTo(0f));
            Assert.That(float.IsNaN(reader.ReadSingle()), Is.True);
            Assert.That(double.IsNaN(reader.ReadDouble()), Is.True);
            Assert.That(reader.ReadString(), Is.Null);
            Assert.That(reader.ReadString(), Is.EqualTo("é"));
            Assert.That(reader.ReadBytes(), Is.Null);
            Assert.That(reader.ReadBytes(), Is.EqualTo(new byte[] { 0xA1, 0xB2 }));
            reader.RequireEnd();
        }

        [Test]
        public void Hash128_WritesLowercaseTextInExistingCanonicalFormat()
        {
            const string value = "0123456789abcdef0123456789abcdef";
            // Hash128 is currently a length-prefixed 32-byte lowercase UTF-8 string, not a raw 16-byte struct.
            var golden = Hex("20 00 00 00 30 31 32 33 34 35 36 37 38 39 61 62 63 64 65 66 30 31 32 33 34 35 36 37 38 39 61 62 63 64 65 66");
            using var writer = new CanonicalPatchBinaryWriter();
            writer.WriteHash128(Hash128.Parse(value));

            Assert.That(writer.ToArray(), Is.EqualTo(golden));

            var reader = new CanonicalPatchBinaryReader(golden);
            Assert.That(reader.ReadHash128().ToString(), Is.EqualTo(value));
            reader.RequireEnd();
        }

        [Test]
        public void NestedRuntimeObjectPatch_PreservesExistingBinaryEnvelopeAndSortedFields()
        {
            // GAP1/v2 body, representation byte, then presence byte and length-prefixed nested body.
            var binaryGolden = Hex("47 41 50 31 02 00 00 00 01 00 00 00 73 02 00 00 00 07 00 00 00 03 FF FF FF FF 02 00 00 00 02 00 00 00 02 FF FF FF FF 09 00 00 00 01 01 00 00 00 AA 04 03 02 01 01 02 00 00 00 A1 B2 00 00 00 00");
            var nestedGolden = Hex("01 45 00 00 00 01 40 00 00 00 47 41 50 31 02 00 00 00 01 00 00 00 73 02 00 00 00 07 00 00 00 03 FF FF FF FF 02 00 00 00 02 00 00 00 02 FF FF FF FF 09 00 00 00 01 01 00 00 00 AA 04 03 02 01 01 02 00 00 00 A1 B2 00 00 00 00");
            var patch = new RuntimeObjectPatch("s");
            patch.Components.Add(new ComponentPatch(0x01020304, ComponentPatchKind.Add, new byte[] { 0xA1, 0xB2 }));
            var fields = new ComponentPatch(7, ComponentPatchKind.Fields);
            fields.Fields.Add(new FieldPatch(9, FieldPatchKind.Set, new byte[] { 0xAA }));
            fields.Fields.Add(new FieldPatch(2, FieldPatchKind.Remove));
            patch.Components.Add(fields);

            Assert.That(new RuntimeObjectPatchBinaryCodec().Encode(patch), Is.EqualTo(binaryGolden));
            using var writer = new CanonicalPatchBinaryWriter();
            RuntimePatchGeneratedValueCodec.WriteRuntimeObjectPatch(writer, patch);
            Assert.That(writer.ToArray(), Is.EqualTo(nestedGolden));

            var reader = new CanonicalPatchBinaryReader(nestedGolden);
            var decoded = RuntimePatchGeneratedValueCodec.ReadRuntimeObjectPatch(reader);
            reader.RequireEnd();
            Assert.That(decoded.SchemaHash, Is.EqualTo("s"));
            Assert.That(decoded.Components[0].ComponentTypeId, Is.EqualTo(7u));
            Assert.That(decoded.Components[0].Fields[0].FieldId, Is.EqualTo(2u));
            Assert.That(decoded.Components[0].Fields[1].Payload, Is.EqualTo(new byte[] { 0xAA }));
            Assert.That(decoded.Components[1].Payload, Is.EqualTo(new byte[] { 0xA1, 0xB2 }));
        }

        [Test]
        public void NetworkPatch_PreservesExistingMagicVersionAndComponentPayload()
        {
            // GAP2/v1 has no schema text on the wire; the active registry supplies that schema.
            var golden = Hex("47 41 50 32 01 00 00 00 01 00 00 00 04 03 02 01 01 02 00 00 00 A1 B2");
            var registry = new RuntimePatchCodecRegistry("s");
            registry.Register(new CanonicalGoldenComponentPatchCodec());
            var codec = new RuntimeObjectPatchNetworkCodec(registry);
            var patch = new RuntimeObjectPatch("s");
            patch.Components.Add(new ComponentPatch(0x01020304, ComponentPatchKind.Add, new byte[] { 0xA1, 0xB2 }));

            Assert.That(codec.Encode(patch), Is.EqualTo(golden));

            var decoded = codec.Decode(golden);
            Assert.That(decoded.SchemaHash, Is.EqualTo("s"));
            Assert.That(decoded.Components.Count, Is.EqualTo(1));
            Assert.That(decoded.Components[0].ComponentTypeId, Is.EqualTo(0x01020304u));
            Assert.That(decoded.Components[0].Payload, Is.EqualTo(new byte[] { 0xA1, 0xB2 }));
        }

        [Test]
        public void NativeReader_ReadsLengthPrefixedViewWithoutChangingWireBytes()
        {
            using var writer = new CanonicalPatchBinaryWriter();
            var block = writer.BeginLengthPrefixedBlock();
            writer.WriteUInt32(0x12345678);
            writer.EndLengthPrefixedBlock(block);
            using var bytes = writer.ToNativeArray(Allocator.Temp);
            var reader = new CanonicalPatchBinaryReader(bytes);
            var nested = reader.ReadBytesReader(sizeof(uint), "test block");

            Assert.That(nested.ReadUInt32(), Is.EqualTo(0x12345678u));
            nested.RequireEnd();
            reader.RequireEnd();
            Assert.That(writer.ToArray(), Is.EqualTo(Hex("04 00 00 00 78 56 34 12")));
        }

        private static byte[] Hex(string value)
        {
            var compact = value.Replace(" ", string.Empty);
            var result = new byte[compact.Length / 2];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = Convert.ToByte(compact.Substring(i * 2, 2), 16);
            }
            return result;
        }
    }
}
