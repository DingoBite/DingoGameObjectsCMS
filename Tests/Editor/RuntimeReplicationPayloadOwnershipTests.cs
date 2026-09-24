#if UNITY_EDITOR && MIRROR

using System;
using DingoGameObjectsCMS.Mirror.Protocol;
using NUnit.Framework;
using Unity.Collections;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplicationPayloadOwnershipTests
    {
        [Test]
        public void PublicBaseline_CopiesInputAndReturnedPayload()
        {
            var state = CreateState();
            var source = new byte[] { 1, 2, 3 };
            var transfer = state.BeginBaseline(source, Array.Empty<NetObjectRef>());

            source[0] = 9;
            var copy = transfer.CopyPayload();
            Assert.That(copy, Is.EqualTo(new byte[] { 1, 2, 3 }));

            copy[1] = 9;
            Assert.That(transfer.CopyPayload(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        [Test]
        public void PublicDeltaEnqueue_CopiesInputBeforeQueuing()
        {
            var state = CreateState();
            state.BeginBaseline(Array.Empty<byte>(), Array.Empty<NetObjectRef>());
            var source = new byte[] { 4, 5, 6 };

            var result = state.TryEnqueueDelta(0, 1, source, Array.Empty<NetObjectRef>(), Array.Empty<NetObjectRef>(), out var envelope);

            Assert.That(result, Is.EqualTo(RuntimeConnectionDeltaEnqueueResult.Enqueued));
            source[0] = 9;
            Assert.That(envelope.Payload, Is.EqualTo(new byte[] { 4, 5, 6 }));
        }

        [Test]
        public void PublicInterestDeltaEnqueue_CopiesInputBeforeQueuing()
        {
            var state = CreateState();
            state.BeginBaseline(Array.Empty<byte>(), Array.Empty<NetObjectRef>());
            var source = new byte[] { 7, 8, 9 };

            var result = state.TryEnqueueInterestDelta(source, Array.Empty<NetObjectRef>(), Array.Empty<NetObjectRef>(), out var envelope);

            Assert.That(result, Is.EqualTo(RuntimeConnectionDeltaEnqueueResult.Enqueued));
            source[0] = 1;
            Assert.That(envelope.Payload, Is.EqualTo(new byte[] { 7, 8, 9 }));
        }

        private static RuntimeConnectionStoreReplicationState CreateState() => new(new NetStoreRef(new FixedString32Bytes("payload-ownership"), 1), 0);
    }
}

#endif
