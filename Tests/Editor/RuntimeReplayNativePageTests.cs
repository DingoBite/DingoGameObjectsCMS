using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplayNativePageTests
    {
        [Test]
        public void WriterPagesAndManagedBytes_ReadAcrossBoundaryAfterWriterDisposal()
        {
            var payload = new byte[RuntimeReplayCheckpointCodec.PAGE_BYTES + 17];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i * 31 + 7);
            }

            byte[] encoded;
            System.Collections.Generic.IReadOnlyList<RuntimeReplayCheckpointPage> pages;
            using (var writer = new RuntimeReplayCheckpointWriter())
            {
                var lengthOffset = writer.BeginLengthPrefixedBlock();
                writer.WriteRawBytes(payload);
                writer.EndLengthPrefixedBlock(lengthOffset);
                encoded = writer.ToArray();
                pages = writer.ToPages();
            }

            Assert.That(encoded.Length, Is.EqualTo(payload.Length + sizeof(int)));
            Assert.That(pages.Count, Is.EqualTo(2));
            Assert.That(pages[0].PayloadLength, Is.EqualTo(RuntimeReplayCheckpointCodec.PAGE_BYTES));
            Assert.That(pages[1].PayloadLength, Is.EqualTo(21));

            using (var reader = new RuntimeReplayCheckpointReader(encoded))
            {
                Assert.That(reader.ReadInt32(), Is.EqualTo(payload.Length));
                Assert.That(reader.ReadRawBytes(payload.Length), Is.EqualTo(payload));
                reader.RequireEnd();
            }

            using (var reader = new RuntimeReplayCheckpointReader(pages, encoded.Length))
            {
                Assert.That(reader.ReadInt32(), Is.EqualTo(payload.Length));
                Assert.That(reader.ReadRawBytes(payload.Length), Is.EqualTo(payload));
                reader.RequireEnd();
            }
        }

        [Test]
        public void EmptyWriter_ProducesReadableEmptyPageAfterDisposal()
        {
            RuntimeReplayCheckpointPage page;
            using (var writer = new RuntimeReplayCheckpointWriter())
            {
                Assert.That(writer.ToArray(), Is.Empty);
                var pages = writer.ToPages();
                Assert.That(pages.Count, Is.EqualTo(1));
                page = pages[0];
            }

            Assert.That(page.PayloadLength, Is.Zero);
            using var reader = new RuntimeReplayCheckpointReader(new[] { page }, 0);
            reader.RequireEnd();
        }
    }
}
