using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplaySha256Tests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(55)]
        [TestCase(56)]
        [TestCase(63)]
        [TestCase(64)]
        [TestCase(65)]
        [TestCase(RuntimeReplayCheckpointCodec.PAGE_BYTES - 1)]
        [TestCase(RuntimeReplayCheckpointCodec.PAGE_BYTES)]
        [TestCase(RuntimeReplayCheckpointCodec.PAGE_BYTES + 1)]
        [TestCase(RuntimeReplayCheckpointCodec.PAGE_BYTES * 2 + 37)]
        public void HashOverloadsMatchManagedControlAtPaddingAndPageBoundaries(int length)
        {
            var payload = CreatePayload(length);
            using var managed = new SHA256Managed();
            var expected = managed.ComputeHash(payload);
            CollectionAssert.AreEqual(expected, RuntimeReplayHash.CalculateSha256(payload));
            var pages = RuntimeReplayCheckpointPageUtils.Split(payload);
            CollectionAssert.AreEqual(expected, RuntimeReplayHash.CalculateSha256(pages, length));
            using var stream = new MemoryStream(payload, writable: false);
            CollectionAssert.AreEqual(expected, RuntimeReplayHash.CalculateSha256(stream, length));
            Assert.That(stream.Position, Is.EqualTo(length));
        }

        [Test]
        public void ProviderPreservesOffsetsResetAndIndependentInterleavedHashes()
        {
            var payload = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES + 73);
            using var managed = new SHA256Managed();
            var expected = managed.ComputeHash(payload, 7, payload.Length - 19);
            using var first = RuntimeReplaySha256.Create();
            using var second = RuntimeReplaySha256.Create();
            first.TransformBlock(payload, 7, 31, null, 0);
            second.TransformBlock(payload, 11, 0, null, 0);
            second.TransformBlock(payload, 7, 67, null, 0);
            first.TransformBlock(payload, 38, payload.Length - 50, null, 0);
            first.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            CollectionAssert.AreEqual(expected, first.Hash);
            first.Dispose();
            second.TransformBlock(payload, 74, payload.Length - 86, null, 0);
            second.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            CollectionAssert.AreEqual(expected, second.Hash);

            second.TransformBlock(payload, 3, 41, null, 0);
            second.Initialize();
            CollectionAssert.AreEqual(expected, second.ComputeHash(payload, 7, payload.Length - 19));
            CollectionAssert.AreEqual(expected, second.ComputeHash(payload, 7, payload.Length - 19));
            second.Dispose();
            Assert.Throws<ObjectDisposedException>(() => second.ComputeHash(payload));
        }

        [Test]
        public void ConcurrentHashInstancesDoNotShareMutableDigestState()
        {
            var payload = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES + 19);
            var expected = new byte[8][];
            for (var index = 0; index < expected.Length; index++)
            {
                using var managed = new SHA256Managed();
                expected[index] = managed.ComputeHash(payload, index, payload.Length - index * 2);
            }
            var tasks = new Task<byte[]>[expected.Length];
            for (var index = 0; index < tasks.Length; index++)
            {
                var offset = index;
                tasks[index] = Task.Run(() =>
                {
                    using var hash = RuntimeReplaySha256.Create();
                    return hash.ComputeHash(payload, offset, payload.Length - offset * 2);
                });
            }
            Task.WaitAll(tasks);
            for (var index = 0; index < tasks.Length; index++)
            {
                CollectionAssert.AreEqual(expected[index], tasks[index].Result);
            }
        }

        [Test]
        public void StreamFailureIsPropagatedWithoutRetryOrTakingInputOwnership()
        {
            var payload = CreatePayload(113);
            using var stream = new RuntimeReplaySha256ThrowingStream(payload);
            var error = Assert.Throws<IOException>(() => RuntimeReplayHash.CalculateSha256(stream, payload.Length));
            Assert.That(error.Message, Is.EqualTo("deliberate source failure"));
            Assert.That(stream.ReadCalls, Is.EqualTo(2));
            Assert.That(stream.Position, Is.EqualTo(7));
            Assert.That(stream.WasDisposed, Is.False);
            using var managed = new SHA256Managed();
            CollectionAssert.AreEqual(managed.ComputeHash(payload), RuntimeReplayHash.CalculateSha256(payload));
        }

        [Test]
        public void StreamHashRejectsTruncationAfterAValidPrefix()
        {
            var payload = CreatePayload(17);
            using var stream = new MemoryStream(payload, writable: false);
            Assert.Throws<EndOfStreamException>(() => RuntimeReplayHash.CalculateSha256(stream, payload.Length + 1));
            Assert.That(stream.Position, Is.EqualTo(payload.Length));
        }

        [Test]
        public void ByteAndBooleanReadsCrossPageBoundaryAfterLengthPrefixFixup()
        {
            using var writer = new RuntimeReplayCheckpointWriter();
            var block = writer.BeginLengthPrefixedBlock();
            var padding = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES - 5);
            writer.WriteRawBytes(padding);
            writer.WriteByte(0xa5);
            writer.WriteBoolean(true);
            writer.WriteByte(0);
            writer.WriteBoolean(false);
            writer.EndLengthPrefixedBlock(block);
            writer.WriteByte(0x7a);
            var bytes = writer.ToArray();
            using var managed = new SHA256Managed();
            CollectionAssert.AreEqual(managed.ComputeHash(bytes), writer.CalculateSha256());
            using var reader = new RuntimeReplayCheckpointReader(writer.ToPages(), writer.Length);
            Assert.That(reader.ReadInt32(), Is.EqualTo(padding.Length + 4));
            CollectionAssert.AreEqual(padding, reader.ReadRawBytes(padding.Length));
            Assert.That(reader.ReadByte(), Is.EqualTo(0xa5));
            Assert.That(reader.ReadBoolean(), Is.True);
            Assert.That(reader.ReadByte(), Is.Zero);
            Assert.That(reader.ReadBoolean(), Is.False);
            Assert.That(reader.ReadByte(), Is.EqualTo(0x7a));
            reader.RequireEnd();
            Assert.Throws<EndOfStreamException>(() => reader.ReadByte());
            Assert.Throws<EndOfStreamException>(() => reader.ReadBoolean());
            reader.Dispose();
            Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
        }

        [Test]
        public void ByteWriteAtLimitRejectsOverflowWithoutChangingPublishedPages()
        {
            using var writer = new RuntimeReplayCheckpointWriter(initialCapacity: 1, maxBytes: RuntimeReplayCheckpointCodec.PAGE_BYTES);
            writer.WriteRawBytes(CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES - 1));
            var before = writer.ToPages();
            var beforeBytes = before[0].Payload;
            writer.WriteByte(0xef);
            Assert.That(writer.Length, Is.EqualTo(RuntimeReplayCheckpointCodec.PAGE_BYTES));
            Assert.Throws<InvalidOperationException>(() => writer.WriteByte(0x11));
            Assert.That(writer.ToArray()[writer.Length - 1], Is.EqualTo(0xef));
            writer.Dispose();
            CollectionAssert.AreEqual(beforeBytes, before[0].Payload);
            Assert.Throws<ObjectDisposedException>(() => writer.WriteByte(0));
        }

        [Test]
        public void BooleanReadPreservesNonzeroByteSemantics()
        {
            var bytes = new byte[] { 0, 1, 2, 255 };
            using var reader = new RuntimeReplayCheckpointReader(bytes);
            foreach (var value in bytes)
            {
                Assert.That(reader.ReadBoolean(), Is.EqualTo(value != 0));
            }
            reader.RequireEnd();
        }

#if UNITY_EDITOR_WIN
        [Test]
        public void WindowsSelectsNativeProviderAndHashRetainsItsAlgorithmLease()
        {
            using var selected = RuntimeReplaySha256.Create();
            Assert.That(selected, Is.TypeOf<RuntimeReplayWindowsSha256>());
            RuntimeReplayBCryptNative.ThrowIfFailed(
                RuntimeReplayBCryptNative.BCryptOpenAlgorithmProvider(out var rawAlgorithm, "SHA256", null, 0),
                "OpenAlgorithmProvider");
            using var algorithm = new RuntimeReplayBCryptAlgorithmHandle(rawAlgorithm);
            using var hash = RuntimeReplayBCryptHashHandle.Create(algorithm);
            algorithm.Dispose();
            var actual = new byte[RuntimeReplayHash.SHA256_BYTES];
            RuntimeReplayBCryptNative.ThrowIfFailed(
                RuntimeReplayBCryptNative.BCryptFinishHash(hash.DangerousGetHandle(), actual, (uint)actual.Length, 0),
                "FinishHash");
            using var managed = new SHA256Managed();
            CollectionAssert.AreEqual(managed.ComputeHash(Array.Empty<byte>()), actual);
            hash.Dispose();
            Assert.That(hash.IsClosed, Is.True);
            Assert.Throws<ObjectDisposedException>(() => RuntimeReplayBCryptHashHandle.Create(algorithm));
        }
#endif

        private static byte[] CreatePayload(int length)
        {
            var result = new byte[length];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = unchecked((byte)(index * 31 + 7));
            }
            return result;
        }
    }

    public class RuntimeReplaySha256ThrowingStream : MemoryStream
    {
        public int ReadCalls;
        public bool WasDisposed;

        public RuntimeReplaySha256ThrowingStream(byte[] payload) : base(payload, writable: false)
        {
        }

        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            if (ReadCalls == 2)
            {
                throw new IOException("deliberate source failure");
            }
            return base.Read(buffer, offset, Math.Min(count, 7));
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
