#if UNITY_EDITOR && MIRROR

using System;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using NUnit.Framework;
using Unity.Collections;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeStoreRootDeltaCodecTests
    {
        [Test]
        public void RootComponentPatch_RoundTripsAsMutation()
        {
            var codec = new RuntimeStoreDeltaCodec(new RuntimePatchCodecRegistry("root-delta-test"));
            var payload = CreatePayload(RuntimeStoreDeltaKind.Mutation, RuntimeStoreDeltaOperationKind.Patch);

            var decoded = codec.Decode(codec.Encode(payload));

            Assert.That(decoded.Kind, Is.EqualTo(RuntimeStoreDeltaKind.Mutation));
            Assert.That(decoded.Operations, Has.Count.EqualTo(1));
            Assert.That(decoded.Operations[0].ObjectId, Is.EqualTo(RuntimeStore.STORE_ROOT_OBJECT_ID));
            Assert.That(decoded.Operations[0].Kind, Is.EqualTo(RuntimeStoreDeltaOperationKind.Patch));
            Assert.That(decoded.Operations[0].Patch.SchemaHash, Is.EqualTo("root-delta-test"));
        }

        [TestCase(RuntimeStoreDeltaOperationKind.Spawn)]
        [TestCase(RuntimeStoreDeltaOperationKind.Remove)]
        [TestCase(RuntimeStoreDeltaOperationKind.Reparent)]
        [TestCase(RuntimeStoreDeltaOperationKind.Move)]
        public void RootStructuralOperation_IsRejected(RuntimeStoreDeltaOperationKind kind)
        {
            var codec = new RuntimeStoreDeltaCodec(new RuntimePatchCodecRegistry("root-delta-test"));
            var payload = CreatePayload(RuntimeStoreDeltaKind.Mutation, kind);

            Assert.Throws<InvalidOperationException>(() => codec.Encode(payload));
        }

        [Test]
        public void InterestDelta_CannotPatchRoot()
        {
            var codec = new RuntimeStoreDeltaCodec(new RuntimePatchCodecRegistry("root-delta-test"));
            var payload = CreatePayload(RuntimeStoreDeltaKind.Interest, RuntimeStoreDeltaOperationKind.Patch);

            Assert.Throws<InvalidOperationException>(() => codec.Encode(payload));
        }

        private static RuntimeStoreDeltaPayload CreatePayload(RuntimeStoreDeltaKind kind, RuntimeStoreDeltaOperationKind operationKind)
        {
            return new RuntimeStoreDeltaPayload
            {
                Kind = kind,
                Store = new NetStoreRef(new FixedString32Bytes("root-delta-test"), 1),
                BaselineId = 1,
                DeliverySequence = 1,
                FromRevision = 1,
                ToRevision = 2,
                Operations = new System.Collections.Generic.List<RuntimeStoreDeltaOperation>
                {
                    new RuntimeStoreDeltaOperation
                    {
                        Kind = operationKind,
                        ObjectId = RuntimeStore.STORE_ROOT_OBJECT_ID,
                        Patch = new RuntimeObjectPatch("root-delta-test"),
                    },
                },
            };
        }
    }
}

#endif
