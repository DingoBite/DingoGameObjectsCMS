#if UNITY_EDITOR && MIRROR

using System;
using System.Text.RegularExpressions;
using DingoGameObjectsCMS.Mirror.Protocol;
using Mirror;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeBaselineTransportBudgetTests
    {
        [Test]
        public void SessionManifest_ExactPackedBudgetRejectsBeforeTheHandshakePublishesManifestSent()
        {
            var descriptor = new RuntimeSessionDescriptor { ProtocolVersion = RuntimeProtocol.VERSION, BuildId = new string('b', 2048), RuntimeSchemaHash = "schema", AssetCatalogHash = RuntimeSessionCatalogHasher.CalculateAssets(Array.Empty<RuntimeAssetCatalogEntry>()), ResourceCatalogHash = "resources", StateStreamCatalogHash = "streams" };
            var template = new RuntimeSessionManifestTemplate(descriptor, Array.Empty<RuntimeAssetCatalogEntry>(), Array.Empty<RuntimeStoreCatalogEntry>());
            var message = template.CreateSnapshot(1).ToWireManifest();
            message.AssignedConnectionId = 7;
            using var writer = NetworkWriterPool.Get();
            NetworkMessages.Pack(message, writer);
            Assert.That(RuntimeSessionManifestTransportBudget.Validate(message, writer.Position).Accepted, Is.True);
            var rejected = RuntimeSessionManifestTransportBudget.Validate(message, writer.Position - 1);
            Assert.That(rejected.Accepted, Is.False);
            Assert.That(rejected.RejectCode, Is.EqualTo(RuntimeProtocolRejectCode.InvalidManifest));
            Assert.That(rejected.Detail, Does.Contain(writer.Position.ToString()));
            var handshake = new RuntimeSessionServerHandshake(1, template);

            var result = handshake.ReceiveHello(descriptor, 2, _ => rejected);

            Assert.That(result.RejectCode, Is.EqualTo(RuntimeProtocolRejectCode.InvalidManifest));
            Assert.That(handshake.State, Is.EqualTo(RuntimeSessionHandshakeState.Rejected));
            Assert.That(handshake.ClientNonce, Is.Zero);
            Assert.That(handshake.CanCreateReplica, Is.False);
        }

        [Test]
        public void SessionManifest_PreflightExceptionIsReportedAndCannotPublishManifestSent()
        {
            var descriptor = new RuntimeSessionDescriptor { ProtocolVersion = RuntimeProtocol.VERSION, BuildId = "build", RuntimeSchemaHash = "schema", AssetCatalogHash = RuntimeSessionCatalogHasher.CalculateAssets(Array.Empty<RuntimeAssetCatalogEntry>()), ResourceCatalogHash = "resources", StateStreamCatalogHash = "streams" };
            var template = new RuntimeSessionManifestTemplate(descriptor, Array.Empty<RuntimeAssetCatalogEntry>(), Array.Empty<RuntimeStoreCatalogEntry>());
            var handshake = new RuntimeSessionServerHandshake(1, template);
            LogAssert.Expect(LogType.Exception, new Regex("scripted manifest preparation failure"));

            var result = handshake.ReceiveHello(descriptor, 2, _ => throw new InvalidOperationException("scripted manifest preparation failure"));

            Assert.That(result.RejectCode, Is.EqualTo(RuntimeProtocolRejectCode.InvalidManifest));
            Assert.That(handshake.State, Is.EqualTo(RuntimeSessionHandshakeState.Rejected));
            Assert.That(handshake.ClientNonce, Is.Zero);
        }

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
