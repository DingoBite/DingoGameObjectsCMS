using System;
using System.Collections.Generic;
using Unity.Collections;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public class RuntimeReplayStoreScope
    {
        private readonly FixedString32Bytes[] _storeIds;
        private readonly HashSet<FixedString32Bytes> _storeIdSet;

        public int Count => _storeIds.Length;

        public RuntimeReplayStoreScope(
            IEnumerable<FixedString32Bytes> storeIds)
        {
            if (storeIds == null)
            {
                throw new ArgumentNullException(nameof(storeIds));
            }

            _storeIdSet = new HashSet<FixedString32Bytes>();
            var ordered = new List<FixedString32Bytes>();
            foreach (var storeId in storeIds)
            {
                if (storeId.Length == 0)
                {
                    throw new ArgumentException(
                        "Replay RuntimeStore scope cannot contain an empty id.",
                        nameof(storeIds));
                }
                if (!_storeIdSet.Add(storeId))
                {
                    throw new ArgumentException(
                        $"Replay RuntimeStore scope contains duplicate id '{storeId}'.",
                        nameof(storeIds));
                }
                ordered.Add(storeId);
            }
            if (ordered.Count == 0)
            {
                throw new ArgumentException(
                    "Replay RuntimeStore scope cannot be empty.",
                    nameof(storeIds));
            }

            ordered.Sort((left, right) => left.CompareTo(right));
            _storeIds = ordered.ToArray();
        }

        public RuntimeReplayStoreScope(params string[] storeIds)
            : this(ConvertStoreIds(storeIds))
        {
        }

        public FixedString32Bytes TakeStoreId(int index)
        {
            return _storeIds[index];
        }

        public bool Contains(FixedString32Bytes storeId)
        {
            return _storeIdSet.Contains(storeId);
        }

        private static FixedString32Bytes[] ConvertStoreIds(
            IReadOnlyList<string> storeIds)
        {
            if (storeIds == null)
            {
                throw new ArgumentNullException(nameof(storeIds));
            }

            var converted = new FixedString32Bytes[storeIds.Count];
            for (var i = 0; i < storeIds.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(storeIds[i]))
                {
                    throw new ArgumentException(
                        $"Replay RuntimeStore scope id at index {i} is empty.",
                        nameof(storeIds));
                }
                converted[i] = new FixedString32Bytes(storeIds[i]);
            }
            return converted;
        }
    }
}
