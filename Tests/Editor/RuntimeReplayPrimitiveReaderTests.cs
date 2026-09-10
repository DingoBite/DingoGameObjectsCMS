using System;
using System.IO;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplayPrimitiveReaderTests
    {
        private static readonly int[] PRIMITIVE_WIDTHS = { 1, 1, 4, 4, 8, 8, 4, 8 };

        [TestCase(false)]
        [TestCase(true)]
        public void EveryPrimitiveMatchesBinaryReaderAtEveryNearbyPageOffset(bool paged)
        {
            var payload = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES * 2 + 32);
            for (var boundary = 1; boundary <= 2; boundary++)
            {
                for (var delta = -8; delta <= 8; delta++)
                {
                    var start = RuntimeReplayCheckpointCodec.PAGE_BYTES * boundary + delta;
                    for (var kind = 0; kind < PRIMITIVE_WIDTHS.Length; kind++)
                    {
                        AssertPrimitiveAt(payload, start, kind, paged);
                    }
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EveryBooleanByteKeepsBinaryReaderNonzeroSemantics(bool paged)
        {
            var start = RuntimeReplayCheckpointCodec.PAGE_BYTES - 128;
            var payload = CreatePayload(start + 256);
            for (var value = 0; value <= byte.MaxValue; value++)
            {
                payload[start + value] = (byte)value;
            }
            using var expectedStream = new MemoryStream(payload, false);
            using var expected = new BinaryReader(expectedStream);
            using var actual = CreateReader(payload, paged);
            actual.ReadRawBytes(start);
            expectedStream.Position = start;
            for (var value = 0; value <= byte.MaxValue; value++)
            {
                Assert.That(actual.ReadBoolean(), Is.EqualTo(expected.ReadBoolean()), "Boolean byte " + value);
                Assert.That(actual.Position, Is.EqualTo(expectedStream.Position));
            }
            actual.RequireEnd();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SignedAndUnsignedEdgeBitsMatchBinaryReaderAcrossPageBoundary(bool paged)
        {
            var payload = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES + 24);
            var patterns = new[]
            {
                0UL, 1UL, 0x7fffffffUL, 0x80000000UL, 0xffffffffUL,
                0x7fffffffffffffffUL, 0x8000000000000000UL, ulong.MaxValue
            };
            for (var delta = -8; delta <= 8; delta++)
            {
                var start = RuntimeReplayCheckpointCodec.PAGE_BYTES + delta;
                for (var kind = 2; kind <= 5; kind++)
                {
                    foreach (var bits in patterns)
                    {
                        WriteBits(payload, start, bits, PRIMITIVE_WIDTHS[kind]);
                        AssertPrimitiveAt(payload, start, kind, paged);
                    }
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FloatingEdgeBitsMatchBinaryReaderWithoutNumericEquality(bool paged)
        {
            var payload = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES + 24);
            var singleBits = new uint[]
            {
                0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x00800000,
                0x7f7fffff, 0xff7fffff, 0x7f800000, 0xff800000,
                0x7fc00001, 0xffc54321, 0x7f800001
            };
            var doubleBits = new ulong[]
            {
                0, 0x8000000000000000, 1, 0x8000000000000001,
                0x000fffffffffffff, 0x0010000000000000,
                0x7fefffffffffffff, 0xffefffffffffffff,
                0x7ff0000000000000, 0xfff0000000000000,
                0x7ff8000000000001, 0xfff8000012345678, 0x7ff0000000000001
            };
            for (var delta = -8; delta <= 8; delta++)
            {
                var start = RuntimeReplayCheckpointCodec.PAGE_BYTES + delta;
                foreach (var bits in singleBits)
                {
                    WriteBits(payload, start, bits, sizeof(float));
                    AssertPrimitiveAt(payload, start, 6, paged);
                }
                foreach (var bits in doubleBits)
                {
                    WriteBits(payload, start, bits, sizeof(double));
                    AssertPrimitiveAt(payload, start, 7, paged);
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TruncatedPrimitiveRejectsBeforeConsumingAnyAvailableBytes(bool paged)
        {
            for (var delta = -8; delta <= 1; delta++)
            {
                var start = RuntimeReplayCheckpointCodec.PAGE_BYTES + delta;
                for (var kind = 0; kind < PRIMITIVE_WIDTHS.Length; kind++)
                {
                    var width = PRIMITIVE_WIDTHS[kind];
                    var complete = CreatePayload(start + width);
                    // Establish a successful control at the same boundary before truncating it.
                    AssertPrimitiveAt(complete, start, kind, paged);
                    for (var available = 0; available < width; available++)
                    {
                        var payload = new byte[start + available];
                        Buffer.BlockCopy(complete, 0, payload, 0, payload.Length);
                        using var actual = CreateReader(payload, paged);
                        actual.ReadRawBytes(start);
                        var context = "kind=" + kind + ", offset=" + start + ", available=" + available;
                        Assert.Throws<EndOfStreamException>(() => ReadActual(actual, kind), context);
                        Assert.That(actual.Position, Is.EqualTo(start), context);
                        Assert.That(actual.Remaining, Is.EqualTo(available), context);
                        var suffix = new byte[available];
                        Buffer.BlockCopy(payload, start, suffix, 0, available);
                        CollectionAssert.AreEqual(suffix, actual.ReadRawBytes(available), context);
                        actual.RequireEnd();
                        Assert.Throws<EndOfStreamException>(() => actual.ReadByte());
                        Assert.That(actual.Position, Is.EqualTo(payload.Length));
                    }
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MixedRawAndPrimitiveReadsShareOneCursorAcrossBothPaths(bool paged)
        {
            var prefix = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES - 5);
            var guid = new Guid("00112233-4455-6677-8899-aabbccddeeff");
            var text = Encoding.UTF8.GetBytes("a\0b / дороги / 世界");
            var blob = new byte[] { 0, 255, 17, 128, 34 };
            byte[] middle;
            byte[] payload;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                writer.Write(prefix);
                writer.Write(0xfedcba98u); // Fits in the current page, leaving exactly one byte.
                writer.Write(0x0123456789abcdefUL); // Requires the BinaryReader fallback.
                writer.Write(0x80000000u); // Negative-zero Single, contiguous again.
                middle = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES * 2 - 3 - (int)stream.Position);
                writer.Write(middle);
                writer.Write(0xfff8000012345678UL); // Double crosses the second page boundary.
                writer.Write(guid.ToByteArray());
                writer.Write(text.Length);
                writer.Write(text);
                writer.Write(blob.Length);
                writer.Write(blob);
                writer.Write((byte)0x80);
                writer.Write(0x10203040u);
                writer.Flush();
                payload = stream.ToArray();
            }
            using var expectedStream = new MemoryStream(payload, false);
            using var expected = new BinaryReader(expectedStream);
            using var actual = CreateReader(payload, paged);
            CollectionAssert.AreEqual(expected.ReadBytes(prefix.Length), actual.ReadRawBytes(prefix.Length));
            CollectionAssert.AreEqual(Array.Empty<byte>(), actual.ReadRawBytes(0));
            Assert.That(actual.Position, Is.EqualTo(expectedStream.Position));
            Assert.Throws<ArgumentOutOfRangeException>(() => actual.ReadRawBytes(-1));
            Assert.That(actual.Position, Is.EqualTo(expectedStream.Position));
            Assert.That(actual.ReadUInt32(), Is.EqualTo(expected.ReadUInt32()));
            Assert.That(actual.ReadUInt64(), Is.EqualTo(expected.ReadUInt64()));
            Assert.That(BitConverter.SingleToInt32Bits(actual.ReadSingle()), Is.EqualTo(BitConverter.SingleToInt32Bits(expected.ReadSingle())));
            CollectionAssert.AreEqual(expected.ReadBytes(middle.Length), actual.ReadRawBytes(middle.Length));
            Assert.That(actual.Position, Is.EqualTo(RuntimeReplayCheckpointCodec.PAGE_BYTES * 2 - 3));
            Assert.That(BitConverter.DoubleToInt64Bits(actual.ReadDouble()), Is.EqualTo(BitConverter.DoubleToInt64Bits(expected.ReadDouble())));
            Assert.That(actual.ReadGuid(), Is.EqualTo(new Guid(expected.ReadBytes(16))));
            Assert.That(actual.ReadString(), Is.EqualTo(Encoding.UTF8.GetString(expected.ReadBytes(expected.ReadInt32()))));
            CollectionAssert.AreEqual(expected.ReadBytes(expected.ReadInt32()), actual.ReadBytes());
            Assert.That(actual.ReadBoolean(), Is.EqualTo(expected.ReadBoolean()));
            Assert.That(actual.Position, Is.EqualTo(expectedStream.Position));
            Assert.That(actual.Remaining, Is.EqualTo(sizeof(uint)));
            Assert.Throws<FormatException>(() => actual.RequireEnd());
            Assert.That(actual.Position, Is.EqualTo(expectedStream.Position));
            Assert.That(actual.ReadUInt32(), Is.EqualTo(expected.ReadUInt32()));
            Assert.That(actual.Position, Is.EqualTo(payload.Length));
            Assert.That(actual.Remaining, Is.Zero);
            actual.RequireEnd();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DisposalKeepsEveryPrimitiveAndCursorGuardAfterValidControl(bool paged)
        {
            var payload = CreatePayload(64);
            using var expected = new BinaryReader(new MemoryStream(payload, false));
            var actual = CreateReader(payload, paged);
            try
            {
                for (var kind = 0; kind < PRIMITIVE_WIDTHS.Length; kind++)
                {
                    Assert.That(ReadActual(actual, kind), Is.EqualTo(ReadExpected(expected, kind)));
                }
                actual.Dispose();
                actual.Dispose();
                for (var kind = 0; kind < PRIMITIVE_WIDTHS.Length; kind++)
                {
                    Assert.Throws<ObjectDisposedException>(() => ReadActual(actual, kind), "kind=" + kind);
                }
                Assert.Throws<ObjectDisposedException>(() => actual.ReadRawBytes(0));
                Assert.Throws<ObjectDisposedException>(() => actual.RequireEnd());
                Assert.Throws<ObjectDisposedException>(() => { _ = actual.Position; });
                Assert.Throws<ObjectDisposedException>(() => { _ = actual.Length; });
                Assert.Throws<ObjectDisposedException>(() => { _ = actual.Remaining; });
            }
            finally
            {
                actual.Dispose();
            }
        }

        private static void AssertPrimitiveAt(byte[] payload, int start, int kind, bool paged)
        {
            using var expectedStream = new MemoryStream(payload, false);
            using var expected = new BinaryReader(expectedStream);
            using var actual = CreateReader(payload, paged);
            expectedStream.Position = start;
            actual.ReadRawBytes(start);
            var context = "kind=" + kind + ", offset=" + start + ", paged=" + paged;
            Assert.That(ReadActual(actual, kind), Is.EqualTo(ReadExpected(expected, kind)), context);
            Assert.That(actual.Position, Is.EqualTo(start + PRIMITIVE_WIDTHS[kind]), context);
            Assert.That(actual.Position, Is.EqualTo(expectedStream.Position), context);
            Assert.That(actual.Length, Is.EqualTo(payload.Length), context);
            Assert.That(actual.Remaining, Is.EqualTo(payload.Length - actual.Position), context);
        }

        private static RuntimeReplayCheckpointReader CreateReader(byte[] payload, bool paged)
        {
            return paged
                ? new RuntimeReplayCheckpointReader(RuntimeReplayCheckpointPageUtils.Split(payload), payload.Length)
                : new RuntimeReplayCheckpointReader(payload);
        }

        private static byte[] CreatePayload(int length)
        {
            var payload = new byte[length];
            for (var index = 0; index < payload.Length; index++)
            {
                payload[index] = unchecked((byte)(index * 73 + 31));
            }
            return payload;
        }

        private static void WriteBits(byte[] payload, int start, ulong bits, int width)
        {
            using var stream = new MemoryStream(payload, true);
            using var writer = new BinaryWriter(stream);
            stream.Position = start;
            if (width == sizeof(uint))
            {
                writer.Write(unchecked((uint)bits));
            }
            else
            {
                writer.Write(bits);
            }
        }

        private static object ReadActual(RuntimeReplayCheckpointReader reader, int kind)
        {
            switch (kind)
            {
                case 0: return reader.ReadByte();
                case 1: return reader.ReadBoolean();
                case 2: return reader.ReadInt32();
                case 3: return reader.ReadUInt32();
                case 4: return reader.ReadInt64();
                case 5: return reader.ReadUInt64();
                case 6: return BitConverter.SingleToInt32Bits(reader.ReadSingle());
                case 7: return BitConverter.DoubleToInt64Bits(reader.ReadDouble());
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        private static object ReadExpected(BinaryReader reader, int kind)
        {
            switch (kind)
            {
                case 0: return reader.ReadByte();
                case 1: return reader.ReadBoolean();
                case 2: return reader.ReadInt32();
                case 3: return reader.ReadUInt32();
                case 4: return reader.ReadInt64();
                case 5: return reader.ReadUInt64();
                case 6: return BitConverter.SingleToInt32Bits(reader.ReadSingle());
                case 7: return BitConverter.DoubleToInt64Bits(reader.ReadDouble());
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
