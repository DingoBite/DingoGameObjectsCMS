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
