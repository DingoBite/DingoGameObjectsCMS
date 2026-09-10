using System;
using System.IO;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;
using Unity.Collections;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplayFixedStringTests
    {
        [TestCase("")]
        [TestCase("micro_units")]
        [TestCase("abcdefghijklmnopqrstuvwxyz123")]
        [TestCase("дороги")]
        [TestCase("世界😀")]
        [TestCase("a\0b")]
        public void FixedStringWireMatchesStringCodecAcrossPageBoundary(string text)
        {
            var value = new FixedString32Bytes(text);
            var padding = new byte[RuntimeReplayCheckpointCodec.PAGE_BYTES - 6];
            using var expected = new RuntimeReplayCheckpointWriter();
            using var actual = new RuntimeReplayCheckpointWriter();
            expected.WriteRawBytes(padding);
            actual.WriteRawBytes(padding);
            for (var index = 0; index < 3; index++)
            {
                expected.WriteString(value.ToString());
                actual.WriteFixedString32Bytes(in value);
            }
            expected.WriteInt32(1234567);
            actual.WriteInt32(1234567);
            CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
            CollectionAssert.AreEqual(expected.CalculateSha256(), actual.CalculateSha256());

            using var reader = new RuntimeReplayCheckpointReader(actual.ToPages(), actual.Length);
            reader.ReadRawBytes(padding.Length);
            for (var index = 0; index < 3; index++)
            {
                Assert.That(reader.ReadFixedString32Bytes(), Is.EqualTo(value));
            }
            Assert.That(reader.ReadInt32(), Is.EqualTo(1234567));
            reader.RequireEnd();
        }

        [TestCase(-1)]
        [TestCase(0)]
        public void NullAndEmptyPrefixPreserveOldFixedStringConversion(int prefix)
        {
            using var writer = new RuntimeReplayCheckpointWriter();
            writer.WriteInt32(prefix);
            using var expected = new RuntimeReplayCheckpointReader(writer.ToArray());
            using var actual = new RuntimeReplayCheckpointReader(writer.ToArray());
            var legacy = new FixedString32Bytes(expected.ReadString(FixedString32Bytes.UTF8MaxLengthInBytes));
            Assert.That(actual.ReadFixedString32Bytes(), Is.EqualTo(legacy));
            actual.RequireEnd();
        }

        [TestCase("80")]
        [TestCase("c080")]
        [TestCase("e08080")]
        [TestCase("eda080")]
        [TestCase("f4908080")]
        [TestCase("f09f98")]
        [TestCase("e228a1")]
        public void MalformedUtf8HasSameStrictRejectionAndConsumedBytes(string hex)
        {
            var bytes = ParseBytes(hex);
            using var writer = new RuntimeReplayCheckpointWriter();
            writer.WriteInt32(bytes.Length);
            writer.WriteRawBytes(bytes);
            writer.WriteByte(99);
            using var expected = new RuntimeReplayCheckpointReader(writer.ToArray());
            using var actual = new RuntimeReplayCheckpointReader(writer.ToArray());
            Assert.Throws<DecoderFallbackException>(() => expected.ReadString(FixedString32Bytes.UTF8MaxLengthInBytes));
            Assert.Throws<DecoderFallbackException>(() => actual.ReadFixedString32Bytes());
            Assert.That(actual.Position, Is.EqualTo(expected.Position));
            Assert.That(actual.ReadByte(), Is.EqualTo(99));
        }

        [TestCase(-2)]
        [TestCase(30)]
        [TestCase(int.MaxValue)]
        public void InvalidByteLengthIsRejectedBeforePayloadConsumption(int length)
        {
            using var writer = new RuntimeReplayCheckpointWriter();
            writer.WriteInt32(length);
            writer.WriteByte(97);
            using var expected = new RuntimeReplayCheckpointReader(writer.ToArray());
            using var actual = new RuntimeReplayCheckpointReader(writer.ToArray());
            Assert.Throws<FormatException>(() => expected.ReadString(FixedString32Bytes.UTF8MaxLengthInBytes));
            Assert.Throws<FormatException>(() => actual.ReadFixedString32Bytes());
            Assert.That(actual.Position, Is.EqualTo(expected.Position));
        }

        [Test]
        public void TruncationWriterLimitAndDisposalRemainChecked()
        {
            using var truncated = new RuntimeReplayCheckpointWriter();
            truncated.WriteInt32(2);
            truncated.WriteByte(97);
            using var reader = new RuntimeReplayCheckpointReader(truncated.ToArray());
            Assert.Throws<EndOfStreamException>(() => reader.ReadFixedString32Bytes());
            Assert.That(reader.Position, Is.EqualTo(sizeof(int)));
            var value = new FixedString32Bytes("ab");
            using var writer = new RuntimeReplayCheckpointWriter(initialCapacity: 0, maxBytes: 5);
            Assert.Throws<InvalidOperationException>(() => writer.WriteFixedString32Bytes(in value));
            Assert.That(writer.Length, Is.EqualTo(sizeof(int)));
            reader.Dispose();
            writer.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reader.ReadFixedString32Bytes());
            Assert.Throws<ObjectDisposedException>(() => writer.WriteFixedString32Bytes(in value));
        }

        [TestCase("80")]
        [TestCase("c080")]
        [TestCase("f09f98")]
        public void InvalidLiveBytesPreservePreviousWriterNormalization(string hex)
        {
            var bytes = ParseBytes(hex);
            var value = default(FixedString32Bytes);
            value.Length = bytes.Length;
            for (var index = 0; index < bytes.Length; index++)
            {
                value[index] = bytes[index];
            }
            using var expected = new RuntimeReplayCheckpointWriter();
            using var actual = new RuntimeReplayCheckpointWriter();
            expected.WriteString(value.ToString());
            actual.WriteFixedString32Bytes(in value);
            CollectionAssert.AreEqual(expected.ToArray(), actual.ToArray());
            using var reader = new RuntimeReplayCheckpointReader(actual.ToArray());
            Assert.That(reader.ReadFixedString32Bytes().ToString(), Is.EqualTo(value.ToString()));
        }

        private static byte[] ParseBytes(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                bytes[index] = Convert.ToByte(hex.Substring(index * 2, 2), 16);
            }
            return bytes;
        }
    }
}
