using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror.V2
{
    [Serializable, Preserve]
    public struct RuntimeBaselineChunk
    {
        public ulong SessionId;
        public NetStoreRef Store;
        public ulong BaselineId;
        public ulong DeliverySequence;
        public ulong StoreRevision;
        public ushort ChunkIndex;
        public ushort ChunkCount;
        public int LogicalLength;
        public byte[] PayloadHash;
        public byte[] Payload;
    }

    public enum RuntimeBaselineChunkResult : byte
    {
        Accepted = 0,
        Completed = 1,
        Duplicate = 2,
        DuplicateCompleted = 3,
        Invalid = 4,
        ConflictingTransfer = 5,
        Corrupt = 6,
        TimedOut = 7,
    }

    public static class RuntimeBaselineChunker
    {
        public static IReadOnlyList<RuntimeBaselineChunk> Split(
            ulong sessionId,
            NetStoreRef store,
            ulong baselineId,
            ulong deliverySequence,
            ulong storeRevision,
            byte[] payload)
        {
            if (sessionId == 0)
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            if (!store.IsValid)
                throw new ArgumentException("Baseline store reference is invalid.", nameof(store));
            if (baselineId == 0)
                throw new ArgumentOutOfRangeException(nameof(baselineId));
            if (deliverySequence == 0)
                throw new ArgumentOutOfRangeException(nameof(deliverySequence));

            payload ??= Array.Empty<byte>();
            if (payload.Length > RuntimeProtocolV2.MAX_BASELINE_BYTES)
                throw new ArgumentOutOfRangeException(nameof(payload), $"Baseline exceeds {RuntimeProtocolV2.MAX_BASELINE_BYTES} bytes.");

            var count = Math.Max(1, (payload.Length + RuntimeProtocolV2.BASELINE_CHUNK_BYTES - 1) / RuntimeProtocolV2.BASELINE_CHUNK_BYTES);
            if (count > RuntimeProtocolV2.MAX_BASELINE_CHUNKS)
                throw new InvalidOperationException($"Baseline requires {count} chunks; limit is {RuntimeProtocolV2.MAX_BASELINE_CHUNKS}.");

            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(payload);

            var chunks = new RuntimeBaselineChunk[count];
            for (var i = 0; i < count; i++)
            {
                var offset = i * RuntimeProtocolV2.BASELINE_CHUNK_BYTES;
                var length = Math.Min(RuntimeProtocolV2.BASELINE_CHUNK_BYTES, payload.Length - offset);
                if (length < 0)
                    length = 0;

                var part = new byte[length];
                if (length > 0)
                    Buffer.BlockCopy(payload, offset, part, 0, length);

                chunks[i] = new RuntimeBaselineChunk
                {
                    SessionId = sessionId,
                    Store = store,
                    BaselineId = baselineId,
                    DeliverySequence = deliverySequence,
                    StoreRevision = storeRevision,
                    ChunkIndex = (ushort)i,
                    ChunkCount = (ushort)count,
                    LogicalLength = payload.Length,
                    PayloadHash = (byte[])hash.Clone(),
                    Payload = part,
                };
            }

            return chunks;
        }
    }

    public sealed class RuntimeBaselineChunkAssembler
    {
        private RuntimeBaselineChunk _header;
        private byte[][] _chunks;
        private int _received;
        private double _startedAt;

        private ulong _lastCompletedSessionId;
        private NetStoreRef _lastCompletedStore;
        private ulong _lastCompletedBaselineId;
        private ulong _lastCompletedSequence;

        public bool IsActive => _chunks != null;

        public RuntimeBaselineChunkResult Accept(in RuntimeBaselineChunk chunk, double nowSeconds, out byte[] completedPayload)
        {
            completedPayload = null;

            if (!ValidateHeader(chunk))
                return RuntimeBaselineChunkResult.Invalid;

            if (IsLastCompleted(chunk))
                return RuntimeBaselineChunkResult.DuplicateCompleted;

            if (_chunks != null && nowSeconds - _startedAt > RuntimeProtocolV2.BASELINE_TIMEOUT_SECONDS)
            {
                ResetActive();
                return RuntimeBaselineChunkResult.TimedOut;
            }

            if (_chunks == null)
                Begin(chunk, nowSeconds);
            else if (!SameTransfer(_header, chunk))
                return RuntimeBaselineChunkResult.ConflictingTransfer;

            var index = chunk.ChunkIndex;
            var current = _chunks[index];
            if (current != null)
            {
                if (!BytesEqual(current, chunk.Payload))
                {
                    ResetActive();
                    return RuntimeBaselineChunkResult.Corrupt;
                }

                return RuntimeBaselineChunkResult.Duplicate;
            }

            _chunks[index] = (byte[])chunk.Payload.Clone();
            _received++;
            if (_received != _chunks.Length)
                return RuntimeBaselineChunkResult.Accepted;

            var assembled = new byte[_header.LogicalLength];
            var offset = 0;
            for (var i = 0; i < _chunks.Length; i++)
            {
                var part = _chunks[i];
                if (offset + part.Length > assembled.Length)
                {
                    ResetActive();
                    return RuntimeBaselineChunkResult.Corrupt;
                }

                if (part.Length > 0)
                    Buffer.BlockCopy(part, 0, assembled, offset, part.Length);
                offset += part.Length;
            }

            if (offset != assembled.Length || !HashMatches(assembled, _header.PayloadHash))
            {
                ResetActive();
                return RuntimeBaselineChunkResult.Corrupt;
            }

            _lastCompletedSessionId = _header.SessionId;
            _lastCompletedStore = _header.Store;
            _lastCompletedBaselineId = _header.BaselineId;
            _lastCompletedSequence = _header.DeliverySequence;
            ResetActive();
            completedPayload = assembled;
            return RuntimeBaselineChunkResult.Completed;
        }

        public void Reset()
        {
            ResetActive();
            _lastCompletedSessionId = 0;
            _lastCompletedStore = default;
            _lastCompletedBaselineId = 0;
            _lastCompletedSequence = 0;
        }

        private static bool ValidateHeader(in RuntimeBaselineChunk chunk)
        {
            if (chunk.SessionId == 0 || !chunk.Store.IsValid || chunk.BaselineId == 0 || chunk.DeliverySequence == 0)
                return false;
            if (chunk.ChunkCount == 0 || chunk.ChunkCount > RuntimeProtocolV2.MAX_BASELINE_CHUNKS || chunk.ChunkIndex >= chunk.ChunkCount)
                return false;
            if (chunk.LogicalLength < 0 || chunk.LogicalLength > RuntimeProtocolV2.MAX_BASELINE_BYTES)
                return false;
            if (chunk.PayloadHash == null || chunk.PayloadHash.Length != 32 || chunk.Payload == null)
                return false;

            var expectedCount = Math.Max(1, (chunk.LogicalLength + RuntimeProtocolV2.BASELINE_CHUNK_BYTES - 1) / RuntimeProtocolV2.BASELINE_CHUNK_BYTES);
            if (chunk.ChunkCount != expectedCount)
                return false;

            var expectedLength = chunk.ChunkIndex + 1 < chunk.ChunkCount
                ? RuntimeProtocolV2.BASELINE_CHUNK_BYTES
                : chunk.LogicalLength - chunk.ChunkIndex * RuntimeProtocolV2.BASELINE_CHUNK_BYTES;
            return chunk.Payload.Length == expectedLength;
        }

        private void Begin(in RuntimeBaselineChunk chunk, double nowSeconds)
        {
            _header = chunk;
            _header.Payload = null;
            _header.PayloadHash = (byte[])chunk.PayloadHash.Clone();
            _chunks = new byte[chunk.ChunkCount][];
            _received = 0;
            _startedAt = nowSeconds;
        }

        private bool IsLastCompleted(in RuntimeBaselineChunk chunk)
        {
            return chunk.SessionId == _lastCompletedSessionId
                   && chunk.Store == _lastCompletedStore
                   && chunk.BaselineId == _lastCompletedBaselineId
                   && chunk.DeliverySequence == _lastCompletedSequence;
        }

        private static bool SameTransfer(in RuntimeBaselineChunk left, in RuntimeBaselineChunk right)
        {
            return left.SessionId == right.SessionId
                   && left.Store == right.Store
                   && left.BaselineId == right.BaselineId
                   && left.DeliverySequence == right.DeliverySequence
                   && left.StoreRevision == right.StoreRevision
                   && left.ChunkCount == right.ChunkCount
                   && left.LogicalLength == right.LogicalLength
                   && BytesEqual(left.PayloadHash, right.PayloadHash);
        }

        private static bool HashMatches(byte[] payload, byte[] expected)
        {
            byte[] actual;
            using (var sha = SHA256.Create())
                actual = sha.ComputeHash(payload);
            return BytesEqual(actual, expected);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null || left.Length != right.Length)
                return false;

            var diff = 0;
            for (var i = 0; i < left.Length; i++)
                diff |= left[i] ^ right[i];
            return diff == 0;
        }

        private void ResetActive()
        {
            _header = default;
            _chunks = null;
            _received = 0;
            _startedAt = 0;
        }
    }
}
