using System;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    class RuntimeCheckpointPagedTestParticipant :
        IRuntimeReplayCheckpointParticipant
    {
        private readonly byte[] _payload;

        public uint SectionId => 0x50414745u;
        public uint CurrentVersion => 1u;
        public byte[] RestoredPayload { get; private set; }

        public RuntimeCheckpointPagedTestParticipant(byte[] payload)
        {
            _payload = payload
                       ?? throw new ArgumentNullException(
                           nameof(payload));
        }

        public void Capture(RuntimeReplayCheckpointWriter writer)
        {
            writer.WriteRawBytes(_payload);
        }

        public void Restore(RuntimeReplayCheckpointReader reader)
        {
            RestoredPayload = reader.ReadRawBytes(reader.Remaining);
        }

        public void AppendFingerprint(
            RuntimeReplayCheckpointWriter writer)
        {
            writer.WriteString("paged-participant.v1");
        }
    }

    public class RuntimeCheckpointPagingTests
    {
        [TestCase(0)]
        [TestCase(1)]
        [TestCase(RuntimeReplayCheckpointCodec.PAGE_BYTES - 1)]
        [TestCase(RuntimeReplayCheckpointCodec.PAGE_BYTES)]
        [TestCase(RuntimeReplayCheckpointCodec.PAGE_BYTES + 1)]
        [TestCase(RuntimeReplayCheckpointCodec.PAGE_BYTES * 2 + 37)]
        public void WriterHash_MatchesByteAndPageControls(int length)
        {
            var payload = CreatePayload(length);
            using var writer = new RuntimeReplayCheckpointWriter();
            writer.WriteRawBytes(payload);

            var expected = RuntimeReplayHash.CalculateSha256(payload);
            CollectionAssert.AreEqual(expected, writer.CalculateSha256());
            CollectionAssert.AreEqual(
                RuntimeReplayHash.CalculateSha256(writer.ToPages(), length),
                writer.CalculateSha256());
            Assert.That(writer.Length, Is.EqualTo(length));
            CollectionAssert.AreEqual(payload, writer.ToArray());
        }

        [Test]
        public void WriterHash_PreservesAppendAndLengthPrefixFixup()
        {
            var payload = CreatePayload(RuntimeReplayCheckpointCodec.PAGE_BYTES + 3);
            using var writer = new RuntimeReplayCheckpointWriter();
            var lengthOffset = writer.BeginLengthPrefixedBlock();
            writer.WriteRawBytes(payload);
            var beforeFixup = writer.ToArray();
            var beforeHash = writer.CalculateSha256();
            CollectionAssert.AreEqual(RuntimeReplayHash.CalculateSha256(beforeFixup), beforeHash);

            writer.EndLengthPrefixedBlock(lengthOffset);
            var fixedHash = writer.CalculateSha256();
            Assert.That(RuntimeReplayHash.FixedTimeEquals(beforeHash, fixedHash), Is.False);
            CollectionAssert.AreEqual(RuntimeReplayHash.CalculateSha256(writer.ToArray()), fixedHash);
            writer.WriteByte(0xa5);

            Assert.That(writer.Length, Is.EqualTo(sizeof(int) + payload.Length + 1));
            CollectionAssert.AreEqual(RuntimeReplayHash.CalculateSha256(writer.ToArray()), writer.CalculateSha256());
            using var reader = new RuntimeReplayCheckpointReader(writer.ToPages(), writer.Length);
            Assert.That(reader.ReadInt32(), Is.EqualTo(payload.Length));
            CollectionAssert.AreEqual(payload, reader.ReadRawBytes(payload.Length));
            Assert.That(reader.ReadByte(), Is.EqualTo(0xa5));
            reader.RequireEnd();
        }

        [Test]
        public void WriterHash_RejectsDisposedWriter()
        {
            var writer = new RuntimeReplayCheckpointWriter();
            writer.Dispose();
            Assert.Throws<ObjectDisposedException>(() => writer.CalculateSha256());
        }

        // Captured from the v2 codec before writer-owned hashing; the inputs are SDK-only neutral data.
        [TestCase(0, 128, "4a7e9e2538fa35190c055ab2151f77b9632ace8f685179b8f1214969864abf80", "87f56f3959c0af2ea787e308263a2f5f86174f1265283aea535dc5220a805c0f")]
        [TestCase(1, 212, "5b6aa3f55cba239c320f3f11cd9066b0ff15eb75904806ba3fd01467a1ef8f46", "39d74569b31e5f83623525f0d86b1404c6619f482eafc359fb7650637f2d8116")]
        [TestCase(2, 469, "11e7c32549783321aef9d9f197354f56d53c04ea8ee8bba7acc050de92e893c4", "766f8207da2020c1cd23fe865e9626d2820a250f552a1a894222b21917834804")]
        [TestCase(3, 98792, "3943c28ca73fd73a3f8ba89c4c7cf77047bb6b630a091ba52f2d751c98fc8c7c", "6e1919d9e5443b31cc8fe8a89869e4a48d8c9493dacef6d545a207365bc55c5d")]
        public void EnvelopeCodec_MatchesPreHashRepairGoldenBytes(
            int fixtureIndex,
            int expectedLength,
            string expectedEncodedHash,
            string expectedBodyHash)
        {
            var checkpoint = CreateGoldenEnvelope(fixtureIndex);
            RuntimeReplayCheckpointCodec.Validate(checkpoint);
            var encoded = RuntimeReplayCheckpointCodec.Encode(checkpoint);
            Assert.That(encoded.Length, Is.EqualTo(expectedLength));
            Assert.That(RuntimeReplayHash.ToHex(RuntimeReplayHash.CalculateSha256(encoded)), Is.EqualTo(expectedEncodedHash));
            Assert.That(RuntimeReplayHash.ToHex(checkpoint.OverallHash), Is.EqualTo(expectedBodyHash));

            var pages = RuntimeReplayCheckpointCodec.EncodePages(checkpoint, out var length);
            Assert.That(length, Is.EqualTo(expectedLength));
            Assert.That(RuntimeReplayHash.ToHex(RuntimeReplayHash.CalculateSha256(pages, length)), Is.EqualTo(expectedEncodedHash));
            var decoded = RuntimeReplayCheckpointCodec.DecodePages(pages, length);
            Assert.That(decoded.CompletedTick, Is.EqualTo(checkpoint.CompletedTick));
            Assert.That(decoded.Cursor, Is.EqualTo(checkpoint.Cursor));
            Assert.That(decoded.SchemaHash, Is.EqualTo(checkpoint.SchemaHash));
            Assert.That(decoded.Sections.Count, Is.EqualTo(checkpoint.Sections.Count));
            for (var index = 0; index < checkpoint.Sections.Count; index++)
            {
                Assert.That(decoded.Sections[index].SectionId, Is.EqualTo(checkpoint.Sections[index].SectionId));
                Assert.That(decoded.Sections[index].SectionVersion, Is.EqualTo(checkpoint.Sections[index].SectionVersion));
                CollectionAssert.AreEqual(checkpoint.Sections[index].Payload, decoded.Sections[index].Payload);
            }
            CollectionAssert.AreEqual(encoded, RuntimeReplayCheckpointCodec.Encode(decoded));
        }

        [TestCase(false, "page hash")]
        [TestCase(true, "section hash")]
        public void EnvelopeCodec_RejectsInnerHashCorruptionWithValidOverallHash(
            bool corruptSectionHash,
            string expectedError)
        {
            var encoded = RuntimeReplayCheckpointCodec.Encode(CreateGoldenEnvelope(2));
            var valid = RuntimeReplayCheckpointCodec.Decode(encoded);
            CollectionAssert.AreEqual(CreatePayload(257), valid.Sections[0].Payload);
            RuntimeReplayCheckpointCodec.Validate(valid);

            int pageHashOffset;
            int sectionHashOffset;
            using (var reader = new RuntimeReplayCheckpointReader(encoded))
            {
                Assert.That(reader.ReadUInt32(), Is.EqualTo(RuntimeReplayCheckpointCodec.FORMAT_MAGIC));
                Assert.That(reader.ReadUInt32(), Is.EqualTo(RuntimeReplayCheckpointCodec.FORMAT_VERSION));
                reader.ReadInt64();
                reader.ReadUInt64();
                reader.ReadString();
                Assert.That(reader.ReadInt32(), Is.EqualTo(1));
                reader.ReadUInt32();
                reader.ReadUInt32();
                Assert.That(reader.ReadInt32(), Is.EqualTo(257));
                Assert.That(reader.ReadInt32(), Is.EqualTo(1));
                var pageLength = reader.ReadInt32();
                reader.ReadRawBytes(pageLength);
                pageHashOffset = reader.Position;
                reader.ReadRawBytes(RuntimeReplayHash.SHA256_BYTES);
                sectionHashOffset = reader.Position;
            }
            encoded[corruptSectionHash ? sectionHashOffset : pageHashOffset] ^= 0x5a;
            // Repair only the outer checksum with an independent byte-array hash, isolating the inner guard.
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                var bodyLength = encoded.Length - RuntimeReplayHash.SHA256_BYTES;
                var bodyHash = sha.ComputeHash(encoded, 0, bodyLength);
                Buffer.BlockCopy(bodyHash, 0, encoded, bodyLength, bodyHash.Length);
            }
            var error = Assert.Throws<ArgumentException>(() => RuntimeReplayCheckpointCodec.Decode(encoded));
            StringAssert.Contains(expectedError, error.Message);
        }

        [Test]
        public void EnvelopeCodec_RejectsOverallHashCorruptionAfterValidControl()
        {
            var checkpoint = CreateGoldenEnvelope(2);
            RuntimeReplayCheckpointCodec.Validate(checkpoint);
            var valid = RuntimeReplayCheckpointCodec.Decode(RuntimeReplayCheckpointCodec.Encode(checkpoint));
            CollectionAssert.AreEqual(CreatePayload(257), valid.Sections[0].Payload);

            checkpoint.OverallHash[0] ^= 0x5a;
            var error = Assert.Throws<InvalidOperationException>(() => RuntimeReplayCheckpointCodec.Validate(checkpoint));
            StringAssert.Contains("overall hash mismatch", error.Message);
        }

        [Test]
        public void RegistryAndEnvelopeCodec_KeepLargeSectionPaged()
        {
            var payload = CreatePayload(
                RuntimeReplayCheckpointCodec.PAGE_BYTES * 2 + 37);
            var participant =
                new RuntimeCheckpointPagedTestParticipant(payload);
            var registry = new RuntimeReplayCheckpointRegistry();
            registry.RegisterParticipant(participant);
            registry.Seal();

            var checkpoint = registry.Capture(
                completedTick: 71,
                cursor: 9);
            var section = checkpoint.Sections[0];

            Assert.That(section.PayloadLength, Is.EqualTo(payload.Length));
            Assert.That(section.Pages.Count, Is.EqualTo(3));
            Assert.That(
                section.Pages[0].PayloadLength,
                Is.EqualTo(RuntimeReplayCheckpointCodec.PAGE_BYTES));
            Assert.That(
                section.Pages[2].PayloadLength,
                Is.EqualTo(37));

            var encodedPages =
                RuntimeReplayCheckpointCodec.EncodePages(
                    checkpoint,
                    out var encodedLength);
            Assert.That(encodedPages.Count, Is.GreaterThan(1));
            for (var i = 0; i < encodedPages.Count; i++)
            {
                Assert.That(
                    encodedPages[i].PayloadLength,
                    Is.LessThanOrEqualTo(
                        RuntimeReplayCheckpointCodec.PAGE_BYTES));
            }

            var decoded = RuntimeReplayCheckpointCodec.DecodePages(
                encodedPages,
                encodedLength);
            CollectionAssert.AreEqual(
                payload,
                decoded.Sections[0].Payload);
            registry.Restore(decoded);
            CollectionAssert.AreEqual(
                payload,
                participant.RestoredPayload);
        }

        [Test]
        public void CheckpointChunks_RestoreOutOfOrderCanonicalPages()
        {
            var payload = CreatePayload(
                RuntimeReplayCheckpointCodec.PAGE_BYTES + 113);
            var participant =
                new RuntimeCheckpointPagedTestParticipant(payload);
            var registry = new RuntimeReplayCheckpointRegistry();
            registry.RegisterParticipant(participant);
            registry.Seal();
            var envelope = registry.Capture(91, 17);
            var boundary = new RuntimeCheckpointBoundary(
                "test.paged-network",
                envelope.CompletedTick,
                envelope.Cursor,
                RuntimeReplayHash.ToHex(envelope.OverallHash));
            var recovery = new RuntimeRecoveryCheckpoint(
                boundary,
                envelope);
            var chunks = RuntimeCheckpointChunker.Split(
                5,
                recovery);
            var assembler = new RuntimeCheckpointChunkAssembler();
            RuntimeRecoveryCheckpoint completed = null;
            var lastResult = RuntimeCheckpointChunkResult.Accepted;

            for (var i = chunks.Count - 1; i >= 0; i--)
            {
                lastResult = assembler.Accept(
                    chunks[i],
                    nowSeconds: chunks.Count - i,
                    out var value);
                completed ??= value;
            }

            Assert.That(
                lastResult,
                Is.EqualTo(RuntimeCheckpointChunkResult.Completed));
            Assert.That(completed, Is.Not.Null);
            Assert.That(
                completed.Boundary.CheckpointHash,
                Is.EqualTo(boundary.CheckpointHash));
            CollectionAssert.AreEqual(
                payload,
                completed.Envelope.Sections[0].Payload);

            Assert.That(
                assembler.Accept(
                    chunks[0],
                    nowSeconds: 20,
                    out _),
                Is.EqualTo(
                    RuntimeCheckpointChunkResult.DuplicateCompleted));
        }

        [Test]
        public void CheckpointChunks_RejectCorruptPageWithoutPublishing()
        {
            var participant =
                new RuntimeCheckpointPagedTestParticipant(
                    CreatePayload(128));
            var registry = new RuntimeReplayCheckpointRegistry();
            registry.RegisterParticipant(participant);
            registry.Seal();
            var envelope = registry.Capture(3, 0);
            var recovery = new RuntimeRecoveryCheckpoint(
                new RuntimeCheckpointBoundary(
                    "test.corrupt-page",
                    envelope.CompletedTick,
                    envelope.Cursor,
                    RuntimeReplayHash.ToHex(envelope.OverallHash)),
                envelope);
            var chunk = RuntimeCheckpointChunker.Split(1, recovery)[0];
            chunk.Payload[0] ^= 0x7f;

            var assembler = new RuntimeCheckpointChunkAssembler();
            Assert.That(
                assembler.Accept(
                    chunk,
                    nowSeconds: 0,
                    out var completed),
                Is.EqualTo(RuntimeCheckpointChunkResult.Corrupt));
            Assert.That(completed, Is.Null);
            Assert.That(assembler.IsActive, Is.False);
        }

        private static RuntimeReplayCheckpointEnvelope CreateGoldenEnvelope(int fixtureIndex)
        {
            switch (fixtureIndex)
            {
                case 0:
                    return RuntimeReplayCheckpointCodec.Create(-1, 0, new string('A', 64), Array.Empty<RuntimeReplayCheckpointSection>());
                case 1:
                    return RuntimeReplayCheckpointCodec.Create(0, ulong.MaxValue, new string('0', 64), new[] { CreateGoldenSection(0, 1, 0) });
                case 2:
                    return RuntimeReplayCheckpointCodec.Create(123456789, 987654321, new string('b', 64), new[] { CreateGoldenSection(7, 3, 257) });
                case 3:
                    return RuntimeReplayCheckpointCodec.Create(71, 9, new string('C', 64), new[]
                    {
                        CreateGoldenSection(0, 1, 0),
                        CreateGoldenSection(17, 2, RuntimeReplayCheckpointCodec.PAGE_BYTES - 1),
                        CreateGoldenSection(90, 7, RuntimeReplayCheckpointCodec.PAGE_BYTES * 2 + 37),
                    });
                default:
                    throw new ArgumentOutOfRangeException(nameof(fixtureIndex));
            }
        }

        private static RuntimeReplayCheckpointSection CreateGoldenSection(uint id, uint version, int length)
        {
            var payload = CreatePayload(length);
            return new RuntimeReplayCheckpointSection(id, version, payload, RuntimeReplayHash.CalculateSha256(payload));
        }

        private static byte[] CreatePayload(int length)
        {
            var result = new byte[length];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = (byte)(i * 31 + 7);
            }
            return result;
        }

    }
}
