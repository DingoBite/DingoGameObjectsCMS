using System;
using System.Collections.Generic;
using Unity.Collections;

namespace DingoGameObjectsCMS.RuntimeObjects.Stores
{
    public readonly struct RuntimeStoreRevisionJournalLimits
    {
        public readonly int MaxRevisionCount;
        public readonly long MaxEstimatedBytes;

        public RuntimeStoreRevisionJournalLimits(int maxRevisionCount, long maxEstimatedBytes)
        {
            if (maxRevisionCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxRevisionCount));
            if (maxEstimatedBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxEstimatedBytes));

            MaxRevisionCount = maxRevisionCount;
            MaxEstimatedBytes = maxEstimatedBytes;
        }
    }

    public class RuntimeStoreRevisionRecord
    {
        private const long RECORD_OVERHEAD_ESTIMATED_BYTES = 64;
        private const long STRUCTURE_CHANGE_ESTIMATED_BYTES = 64;
        private const long COMPONENT_STRUCTURE_CHANGE_ESTIMATED_BYTES = 16;
        private const long COMPONENT_CHANGE_ESTIMATED_BYTES = 16;

        public readonly FixedString32Bytes StoreId;
        public readonly StoreRealm Realm;
        public readonly uint StoreGeneration;
        public readonly ulong StoreRevision;
        public readonly bool ReplicationSuppressed;
        public readonly IReadOnlyList<RuntimeStoreStructureChange> StructureChanges;
        public readonly IReadOnlyList<ObjectStructDirty> ComponentStructureChanges;
        public readonly IReadOnlyList<ObjectComponentDirty> ComponentChanges;
        public readonly long EstimatedBytes;

        private RuntimeStoreRevisionRecord(RuntimeStoreCommittedBatch batch)
        {
            StoreId = batch.StoreId;
            Realm = batch.Realm;
            StoreGeneration = batch.StoreGeneration;
            StoreRevision = batch.StoreRevision;
            ReplicationSuppressed = batch.ReplicationSuppressed;
            StructureChanges = CopyImmutable(batch.StructureChanges);
            ComponentStructureChanges = CopyImmutable(batch.ComponentStructureChanges);
            ComponentChanges = CopyImmutable(batch.ComponentChanges);
            EstimatedBytes = checked(RECORD_OVERHEAD_ESTIMATED_BYTES
                + StructureChanges.Count * STRUCTURE_CHANGE_ESTIMATED_BYTES
                + ComponentStructureChanges.Count * COMPONENT_STRUCTURE_CHANGE_ESTIMATED_BYTES
                + ComponentChanges.Count * COMPONENT_CHANGE_ESTIMATED_BYTES);
        }

        public static RuntimeStoreRevisionRecord CopyFrom(RuntimeStoreCommittedBatch batch) => new(batch);

        private static IReadOnlyList<T> CopyImmutable<T>(NativeArray<T> source) where T : struct
        {
            if (!source.IsCreated || source.Length == 0)
                return Array.Empty<T>();

            var copy = new T[source.Length];
            for (var i = 0; i < source.Length; i++)
            {
                copy[i] = source[i];
            }

            return Array.AsReadOnly(copy);
        }
    }

    public enum RuntimeStoreRevisionReadStatus : byte
    {
        Success = 1,
        UpToDate = 2,
        Gap = 3,
        Evicted = 4,
        Ahead = 5,
    }

    public class RuntimeStoreRevisionReadResult
    {
        public readonly RuntimeStoreRevisionReadStatus Status;
        public readonly ulong BaselineRevision;
        public readonly ulong OldestAvailableRevision;
        public readonly ulong LastRevision;
        public readonly ulong EvictedThroughRevision;
        public readonly IReadOnlyList<RuntimeStoreRevisionRecord> Revisions;

        public bool RequiresBaseline => Status == RuntimeStoreRevisionReadStatus.Gap
            || Status == RuntimeStoreRevisionReadStatus.Evicted
            || Status == RuntimeStoreRevisionReadStatus.Ahead;

        public RuntimeStoreRevisionReadResult(RuntimeStoreRevisionReadStatus status, ulong baselineRevision, ulong oldestAvailableRevision, ulong lastRevision, ulong evictedThroughRevision, IReadOnlyList<RuntimeStoreRevisionRecord> revisions)
        {
            Status = status;
            BaselineRevision = baselineRevision;
            OldestAvailableRevision = oldestAvailableRevision;
            LastRevision = lastRevision;
            EvictedThroughRevision = evictedThroughRevision;
            if (revisions.Count == 0)
            {
                Revisions = Array.Empty<RuntimeStoreRevisionRecord>();
                return;
            }

            var copy = new RuntimeStoreRevisionRecord[revisions.Count];
            for (var i = 0; i < revisions.Count; i++)
            {
                copy[i] = revisions[i];
            }

            Revisions = Array.AsReadOnly(copy);
        }
    }

    public class RuntimeStoreRevisionJournal
    {
        private readonly Queue<RuntimeStoreRevisionRecord> _records = new();
        private readonly RuntimeStoreRevisionJournalLimits _limits;
        private long _estimatedBytes;
        private ulong _lastRevision;
        private ulong _evictedThroughRevision;

        public readonly FixedString32Bytes StoreId;
        public readonly StoreRealm Realm;
        public readonly uint StoreGeneration;
        public readonly ulong InitialRevision;

        public int Count => _records.Count;
        public long EstimatedBytes => _estimatedBytes;
        public ulong LastRevision => _lastRevision;
        public ulong EvictedThroughRevision => _evictedThroughRevision;
        public ulong OldestAvailableRevision => _records.Count > 0 ? _records.Peek().StoreRevision : 0;

        public RuntimeStoreRevisionJournal(FixedString32Bytes storeId, StoreRealm realm, uint storeGeneration, ulong initialRevision, RuntimeStoreRevisionJournalLimits limits)
        {
            if (storeGeneration == 0)
                throw new ArgumentOutOfRangeException(nameof(storeGeneration));

            StoreId = storeId;
            Realm = realm;
            StoreGeneration = storeGeneration;
            InitialRevision = initialRevision;
            _lastRevision = initialRevision;
            _limits = limits;
            if (_limits.MaxRevisionCount <= 0)
                throw new ArgumentOutOfRangeException(nameof(limits));
            if (_limits.MaxEstimatedBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(limits));
        }

        public RuntimeStoreRevisionRecord Append(RuntimeStoreCommittedBatch batch)
        {
            if (!batch.StoreId.Equals(StoreId) || batch.Realm != Realm || batch.StoreGeneration != StoreGeneration)
                throw new InvalidOperationException($"RuntimeStore revision journal for '{StoreId}' generation {StoreGeneration} cannot accept batch for '{batch.StoreId}' generation {batch.StoreGeneration} in realm {batch.Realm}.");
            if (_lastRevision == ulong.MaxValue)
                throw new InvalidOperationException($"RuntimeStore revision journal for '{StoreId}' generation {StoreGeneration} exhausted its revision range.");

            var expectedRevision = _lastRevision + 1;
            if (batch.StoreRevision != expectedRevision)
                throw new InvalidOperationException($"RuntimeStore revision journal for '{StoreId}' generation {StoreGeneration} expected revision {expectedRevision}, but received {batch.StoreRevision}.");

            var record = RuntimeStoreRevisionRecord.CopyFrom(batch);
            var nextEstimatedBytes = checked(_estimatedBytes + record.EstimatedBytes);
            _records.Enqueue(record);
            _estimatedBytes = nextEstimatedBytes;
            _lastRevision = record.StoreRevision;
            EvictToLimits();
            return record;
        }

        public RuntimeStoreRevisionReadResult ReadAfter(ulong baselineRevision)
        {
            if (baselineRevision < InitialRevision)
                return CreateReadResult(RuntimeStoreRevisionReadStatus.Gap, baselineRevision, Array.Empty<RuntimeStoreRevisionRecord>());
            if (baselineRevision > _lastRevision)
                return CreateReadResult(RuntimeStoreRevisionReadStatus.Ahead, baselineRevision, Array.Empty<RuntimeStoreRevisionRecord>());
            if (baselineRevision == _lastRevision)
                return CreateReadResult(RuntimeStoreRevisionReadStatus.UpToDate, baselineRevision, Array.Empty<RuntimeStoreRevisionRecord>());
            if (_records.Count == 0 || baselineRevision + 1 < _records.Peek().StoreRevision)
                return CreateReadResult(RuntimeStoreRevisionReadStatus.Evicted, baselineRevision, Array.Empty<RuntimeStoreRevisionRecord>());

            var revisions = new List<RuntimeStoreRevisionRecord>();
            var expectedRevision = baselineRevision + 1;
            foreach (var record in _records)
            {
                if (record.StoreRevision <= baselineRevision)
                    continue;

                if (record.StoreRevision != expectedRevision)
                    return CreateReadResult(RuntimeStoreRevisionReadStatus.Gap, baselineRevision, Array.Empty<RuntimeStoreRevisionRecord>());

                revisions.Add(record);
                expectedRevision++;
            }

            if (revisions.Count == 0 || revisions[revisions.Count - 1].StoreRevision != _lastRevision)
                return CreateReadResult(RuntimeStoreRevisionReadStatus.Gap, baselineRevision, Array.Empty<RuntimeStoreRevisionRecord>());

            return CreateReadResult(RuntimeStoreRevisionReadStatus.Success, baselineRevision, revisions);
        }

        private RuntimeStoreRevisionReadResult CreateReadResult(RuntimeStoreRevisionReadStatus status, ulong baselineRevision, IReadOnlyList<RuntimeStoreRevisionRecord> revisions)
        {
            return new RuntimeStoreRevisionReadResult(status, baselineRevision, OldestAvailableRevision, _lastRevision, _evictedThroughRevision, revisions);
        }

        private void EvictToLimits()
        {
            while (_records.Count > _limits.MaxRevisionCount || _estimatedBytes > _limits.MaxEstimatedBytes)
            {
                var evicted = _records.Dequeue();
                _estimatedBytes -= evicted.EstimatedBytes;
                _evictedThroughRevision = evicted.StoreRevision;
            }
        }
    }
}
