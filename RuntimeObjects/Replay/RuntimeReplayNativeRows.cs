using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    // The checkpoint owns the allocation. Jobs borrow its native views and must finish
    // before the owner prepares another capture, resets, or disposes these rows.
    public class RuntimeReplayNativeRows<T> : IDisposable where T : unmanaged
    {
        public NativeList<T> Records;

        public RuntimeReplayNativeRows()
        {
            Records = new NativeList<T>(1, Allocator.Persistent);
        }

        public void Reset() => Records.Clear();

        public void PrepareCapture(int capacity)
        {
            Reset();
            if (Records.Capacity < capacity)
            {
                Records.Capacity = capacity;
            }
        }

        public static long EstimateReserveBytes(int length)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }
            var capacity = 1;
            while (capacity < length)
            {
                if (capacity > int.MaxValue / 2)
                {
                    return long.MaxValue;
                }
                capacity <<= 1;
            }
            return checked((long)capacity * UnsafeUtility.SizeOf<T>());
        }

        public void Dispose()
        {
            if (Records.IsCreated)
            {
                Records.Dispose();
            }
        }
    }
}
