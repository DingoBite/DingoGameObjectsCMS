#if UNITY_EDITOR && MIRROR

using System;
using DingoGameObjectsCMS.Mirror.Protocol;
using Mirror;
using NUnit.Framework;
using Unity.Collections;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeBaselineTransportBudgetTests
    {
        [Test]
        public void Split_UsesMirrorReliablePacketBudgetAndStillReassembles()
        {
            const int reliablePacketBytes = 1_150;
            var store = new NetStoreRef(
                new FixedString32Bytes("gameplay"),
                1);
            var transportHeader = new RuntimeBaselineChunk
            {
                SessionId = 1,
                Store = store,
                BaselineId = 1,
                DeliverySequence = 1,
                LogicalLength = 4_096,
                PayloadHash = new byte[32],
                Payload = Array.Empty<byte>(),
            };
            var payloadCapacity = RuntimeBaselineChunkTransportBudget
                .GetPayloadCapacity(
                    transportHeader,
                    reliablePacketBytes);
            var payload = new byte[transportHeader.LogicalLength];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % byte.MaxValue);
            }

            var chunks = RuntimeBaselineChunker.Split(
                1,
                store,
                1,
                1,
                0,
                payload,
                chunkPayloadBytes: payloadCapacity);

            Assert.That(
                chunks.Count,
                Is.EqualTo(
                    (payload.Length + payloadCapacity - 1)
                    / payloadCapacity));
            var maxPackedBytes = reliablePacketBytes
                                 - Batcher.MaxMessageOverhead(
                                     reliablePacketBytes);
            for (var i = 0; i < chunks.Count; i++)
            {
                using var writer = NetworkWriterPool.Get();
                NetworkMessages.Pack(
                    new RtBaselineChunk { Value = chunks[i] },
                    writer);
                Assert.That(
                    writer.Position,
                    Is.LessThanOrEqualTo(maxPackedBytes));
            }

            var assembler = new RuntimeBaselineChunkAssembler();
            byte[] completed = null;
            for (var i = chunks.Count - 1; i >= 0; i--)
            {
                var result = assembler.Accept(
                    chunks[i],
                    0d,
                    out var candidate);
                if (candidate != null)
                {
                    completed = candidate;
                }

                Assert.That(
                    result,
                    i == 0
                        ? Is.EqualTo(
                            RuntimeBaselineChunkResult.Completed)
                        : Is.EqualTo(
                            RuntimeBaselineChunkResult.Accepted));
            }

            Assert.That(completed, Is.EqualTo(payload));
        }
    }
}

#endif
