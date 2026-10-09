using System;
using DingoGameObjectsCMS.Mirror.Protocol;
using NUnit.Framework;
using Unity.Collections;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeStateStreamFrameCodecGoldenTests
    {
        [Test]
        public void SampleFrame_PreservesBinaryWriterWireBytes()
        {
            var golden = Hex("52 54 53 32 02 00 00 00 04 00 00 00 74 65 73 74 01 00 00 00 02 00 00 00 03 00 00 00 04 00 00 00 00 00 00 00 00 01 00 00 00 05 00 00 00 00 00 00 00 01 02 00 00 00 A1 B2");
            var frame = new RuntimeStateStreamFrame(new NetStoreRef(new FixedString32Bytes("test"), 1), 2, 3, 4, 0, RuntimeStateStreamFrameFlags.None, new[] { new RuntimePackedStateStreamSample(new RuntimeStateStreamKey(5), RuntimeStateStreamSampleFlags.Stop, new byte[] { 0xA1, 0xB2 }) });
            var codec = new RuntimeStateStreamFrameCodec();

            CollectionAssert.AreEqual(golden, codec.Encode(frame));
            var decoded = codec.Decode(golden);
            Assert.That(decoded.Store, Is.EqualTo(frame.Store));
            Assert.That(decoded.StreamTypeId, Is.EqualTo(2u));
            Assert.That(decoded.Sequence, Is.EqualTo(3u));
            Assert.That(decoded.SimulationTick, Is.EqualTo(4u));
            Assert.That(decoded.Samples.Count, Is.EqualTo(1));
            Assert.That(decoded.Samples[0].Flags, Is.EqualTo(RuntimeStateStreamSampleFlags.Stop));
            CollectionAssert.AreEqual(new byte[] { 0xA1, 0xB2 }, decoded.Samples[0].PackedState);
            CollectionAssert.AreEqual(golden, codec.Encode(decoded));
        }

        [Test]
        public void EmptyReconciliation_PreservesBinaryWriterWireBytes()
        {
            var golden = Hex("52 54 53 32 02 00 00 00 01 00 00 00 61 01 00 00 00 01 00 00 00 07 00 00 00 08 00 00 00 07 00 00 00 03 00 00 00 00");
            var frame = new RuntimeStateStreamFrame(new NetStoreRef(new FixedString32Bytes("a"), 1), 1, 7, 8, 7, RuntimeStateStreamFrameFlags.ReconciliationBegin | RuntimeStateStreamFrameFlags.ReconciliationEnd, Array.Empty<RuntimePackedStateStreamSample>());
            var codec = new RuntimeStateStreamFrameCodec();

            CollectionAssert.AreEqual(golden, codec.Encode(frame));
            var decoded = codec.Decode(golden);
            Assert.That(decoded.IsKeyframe, Is.True);
            Assert.That(decoded.Samples.Count, Is.Zero);
            CollectionAssert.AreEqual(golden, codec.Encode(decoded));
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
