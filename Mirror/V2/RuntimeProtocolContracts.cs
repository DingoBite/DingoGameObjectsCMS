using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public static class RuntimeProtocolV2
    {
        public const ushort VERSION = 2;
        public const int BASELINE_CHUNK_BYTES = 32 * 1024;
        public const int MAX_BASELINE_BYTES = 16 * 1024 * 1024;
        public const int MAX_BASELINE_CHUNKS = 512;
        public const double BASELINE_TIMEOUT_SECONDS = 10d;
        public const int MAX_PENDING_ENVELOPES = 256;
        public const int MAX_PENDING_ENVELOPE_BYTES = 4 * 1024 * 1024;
        // Reliable deltas deliberately stay small even when the selected
        // transport supports very large reliable packets. Large mutation
        // batches are represented by the existing chunked baseline path so a
        // single delta cannot introduce multi-frame head-of-line blocking.
        public const int MAX_RELIABLE_DELTA_BATCH_BYTES = 32 * 1024;
    }

    /// <summary>
    /// Allocation-free description of the next reliable delta at the protocol
    /// to transport boundary. The payload buffer is owned by the caller and is
    /// only borrowed for the synchronous budget check.
    /// </summary>
    public readonly struct RuntimeReliableDeltaTransportEnvelope
    {
        public readonly ulong SessionId;
        public readonly NetStoreRef Store;
        public readonly ulong BaselineId;
        public readonly ulong DeliverySequence;
        public readonly ulong FromRevision;
        public readonly ulong ToRevision;
        public readonly RuntimeStoreDeltaKind Kind;

        internal readonly byte[] PayloadBuffer;
        public int PayloadBytes => PayloadBuffer?.Length ?? 0;

        public RuntimeReliableDeltaTransportEnvelope(
            ulong sessionId,
            in NetStoreRef store,
            ulong baselineId,
            ulong deliverySequence,
            ulong fromRevision,
            ulong toRevision,
            byte[] payload,
            RuntimeStoreDeltaKind kind = RuntimeStoreDeltaKind.Mutation)
        {
            SessionId = sessionId;
            Store = store;
            BaselineId = baselineId;
            DeliverySequence = deliverySequence;
            FromRevision = fromRevision;
            ToRevision = toRevision;
            Kind = kind;
            PayloadBuffer = payload ?? Array.Empty<byte>();
        }
    }

    public delegate bool RuntimeReliableDeltaTransportBudgetCheck(
        in RuntimeReliableDeltaTransportEnvelope envelope);

    [Serializable, Preserve]
    public struct RuntimeSessionDescriptor
    {
        public ushort ProtocolVersion;
        public string BuildId;
        public string RuntimeSchemaHash;
        public string AssetCatalogHash;
    }

    [Serializable, Preserve]
    public struct RuntimeAssetCatalogEntry
    {
        public uint AssetNetId;
        public string ExactKey;
        public string AssetGuid;
        public string MaterializedContentHash;
    }

    [Serializable, Preserve]
    public struct RuntimeStoreCatalogEntry
    {
        public FixedString32Bytes StoreId;
        public uint StoreGeneration;
    }

    public enum RuntimeProtocolRejectCode : byte
    {
        None = 0,
        ProtocolVersionMismatch = 1,
        BuildMismatch = 2,
        RuntimeSchemaMismatch = 3,
        AssetCatalogMismatch = 4,
        InvalidManifest = 5,
        SessionNotReady = 6,
        InvalidStore = 7,
        InvalidEnvelope = 8,
        CommandRejected = 9,
    }

    public static class RuntimeSessionCompatibility
    {
        public static RuntimeProtocolRejectCode Validate(in RuntimeSessionDescriptor expected, in RuntimeSessionDescriptor actual)
        {
            if (actual.ProtocolVersion != RuntimeProtocolV2.VERSION || actual.ProtocolVersion != expected.ProtocolVersion)
                return RuntimeProtocolRejectCode.ProtocolVersionMismatch;
            if (!EqualsRequired(expected.BuildId, actual.BuildId))
                return RuntimeProtocolRejectCode.BuildMismatch;
            if (!EqualsRequired(expected.RuntimeSchemaHash, actual.RuntimeSchemaHash))
                return RuntimeProtocolRejectCode.RuntimeSchemaMismatch;
            if (!EqualsRequired(expected.AssetCatalogHash, actual.AssetCatalogHash))
                return RuntimeProtocolRejectCode.AssetCatalogMismatch;
            return RuntimeProtocolRejectCode.None;
        }

        private static bool EqualsRequired(string expected, string actual)
        {
            return !string.IsNullOrWhiteSpace(expected)
                   && !string.IsNullOrWhiteSpace(actual)
                   && string.Equals(expected, actual, StringComparison.Ordinal);
        }
    }

    public static class RuntimeSessionCatalogHasher
    {
        public static string Calculate(
            IEnumerable<RuntimeAssetCatalogEntry> assets,
            IEnumerable<RuntimeStoreCatalogEntry> stores)
        {
            var assetArray = assets?.OrderBy(value => value.AssetNetId).ToArray()
                             ?? throw new ArgumentNullException(nameof(assets));
            var storeArray = stores?.OrderBy(value => value.StoreId.ToString(), StringComparer.Ordinal).ToArray()
                             ?? throw new ArgumentNullException(nameof(stores));

            Validate(assetArray, storeArray);
            return CalculateValidatedAssets(assetArray);
        }

        public static string CalculateAssets(IEnumerable<RuntimeAssetCatalogEntry> assets)
        {
            var assetArray = assets?.OrderBy(value => value.AssetNetId).ToArray()
                             ?? throw new ArgumentNullException(nameof(assets));
            ValidateAssets(assetArray);
            return CalculateValidatedAssets(assetArray);
        }

        private static string CalculateValidatedAssets(RuntimeAssetCatalogEntry[] assetArray)
        {

            var builder = new StringBuilder();
            foreach (var asset in assetArray)
            {
                builder.Append("asset|")
                    .Append(asset.AssetNetId).Append('|')
                    .Append(asset.ExactKey).Append('|')
                    .Append(asset.AssetGuid).Append('|')
                    .Append(asset.MaterializedContentHash).Append('\n');
            }

            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
            return ToHex(hash);
        }

        private static void Validate(RuntimeAssetCatalogEntry[] assets, RuntimeStoreCatalogEntry[] stores)
        {
            ValidateAssets(assets);
            var storeIds = new HashSet<FixedString32Bytes>();
            foreach (var store in stores)
            {
                if (store.StoreId.Length == 0 || store.StoreGeneration == 0 || !storeIds.Add(store.StoreId))
                    throw new InvalidOperationException($"Store catalog contains invalid or duplicate store '{store.StoreId}'.");
            }
        }

        private static void ValidateAssets(RuntimeAssetCatalogEntry[] assets)
        {
            var assetIds = new HashSet<uint>();
            foreach (var asset in assets)
            {
                if (asset.AssetNetId == 0 || !assetIds.Add(asset.AssetNetId))
                    throw new InvalidOperationException($"Asset catalog contains invalid or duplicate net id '{asset.AssetNetId}'.");
                if (string.IsNullOrWhiteSpace(asset.ExactKey)
                    || string.IsNullOrWhiteSpace(asset.AssetGuid)
                    || string.IsNullOrWhiteSpace(asset.MaterializedContentHash))
                    throw new InvalidOperationException($"Asset catalog entry '{asset.AssetNetId}' is incomplete.");
            }

        }

        internal static string ToHex(byte[] value)
        {
            var builder = new StringBuilder(value.Length * 2);
            for (var i = 0; i < value.Length; i++)
                builder.Append(value[i].ToString("x2"));
            return builder.ToString();
        }
    }
}
