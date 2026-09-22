using System;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public struct RuntimeReplayNativeRowsWriteJob : IJobParallelFor
    {
        public NativeList<int>.ParallelWriter Rows;

        public void Execute(int index)
        {
            Rows.AddNoResize(index);
        }
    }

    public class RuntimeReplayNativeRowsTests
    {
        [Test]
        public void EnsureCapacityPreservesCapturedRowsAndDoesNotShrink()
        {
            using var rows = new RuntimeReplayNativeRows<long>();
            rows.Records.Add(17);
            rows.Records.Add(23);

            rows.EnsureCapacity(257);
            var capacity = rows.Records.Capacity;
            Assert.That(capacity, Is.GreaterThanOrEqualTo(257));
            Assert.That(rows.Records.Length, Is.EqualTo(2));
            Assert.That(rows.Records[0], Is.EqualTo(17));
            Assert.That(rows.Records[1], Is.EqualTo(23));

            rows.EnsureCapacity(1);
            Assert.That(rows.Records.Capacity, Is.EqualTo(capacity));
            Assert.That(rows.Records.Length, Is.EqualTo(2));
            Assert.That(rows.Records[0], Is.EqualTo(17));
            Assert.That(rows.Records[1], Is.EqualTo(23));
        }

        [Test]
        public void ResetAndSmallerCaptureRetainAllocatedCapacity()
        {
            using var rows = new RuntimeReplayNativeRows<long>();
            rows.PrepareCapture(33);
            rows.Records.Add(17);
            rows.Records.Add(23);
            var capacity = rows.Records.Capacity;
            Assert.That(capacity, Is.GreaterThanOrEqualTo(33));

            rows.Reset();
            Assert.That(rows.Records.Length, Is.Zero);
            Assert.That(rows.Records.Capacity, Is.EqualTo(capacity));

            rows.Records.Add(41);
            rows.PrepareCapture(3);
            Assert.That(rows.Records.Length, Is.Zero);
            Assert.That(rows.Records.Capacity, Is.EqualTo(capacity));
        }

        [Test]
        public void BorrowedParallelWriterAndArrayUseTheOwnedAllocation()
        {
            const int ROW_COUNT = 64;
            using var rows = new RuntimeReplayNativeRows<int>();
            rows.PrepareCapture(ROW_COUNT);
            new RuntimeReplayNativeRowsWriteJob
            {
                Rows = rows.Records.AsParallelWriter(),
            }.Schedule(ROW_COUNT, 8).Complete();

            Assert.That(rows.Records.Length, Is.EqualTo(ROW_COUNT));
            var seen = new bool[ROW_COUNT];
            for (var index = 0; index < rows.Records.Length; index++)
            {
                var value = rows.Records[index];
                Assert.That(value, Is.InRange(0, ROW_COUNT - 1));
                Assert.That(seen[value], Is.False, "The parallel writer must publish each row once.");
                seen[value] = true;
            }

            var borrowed = rows.Records.AsArray();
            borrowed[0] = -1;
            Assert.That(rows.Records[0], Is.EqualTo(-1));
        }

        [Test]
        public void DisposeReleasesStorageAndCanBeRepeated()
        {
            using var rows = new RuntimeReplayNativeRows<byte>();
            rows.PrepareCapture(257);
            rows.Records.Add(42);
            Assert.That(rows.Records.IsCreated, Is.True);

            rows.Dispose();
            Assert.That(rows.Records.IsCreated, Is.False);
            Assert.DoesNotThrow(rows.Dispose);
        }

        [TestCase(0, 8L)]
        [TestCase(1, 8L)]
        [TestCase(2, 16L)]
        [TestCase(3, 32L)]
        [TestCase(1024, 8192L)]
        [TestCase(1073741824, 8589934592L)]
        [TestCase(1073741825, long.MaxValue)]
        [TestCase(int.MaxValue, long.MaxValue)]
        public void ReserveEstimateRoundsCapacityAndSaturatesBeforeIntegerOverflow(int count, long expected)
        {
            Assert.That(RuntimeReplayNativeRows<long>.EstimateReserveBytes(count), Is.EqualTo(expected));
        }

        [Test]
        public void ReserveEstimateRejectsNegativeLength()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => RuntimeReplayNativeRows<byte>.EstimateReserveBytes(-1));
        }
    }
}
