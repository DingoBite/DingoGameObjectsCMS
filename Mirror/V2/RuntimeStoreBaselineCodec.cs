using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror.V2
{
    [Serializable]
    public sealed class RuntimeStoreBaselinePayload
    {
        public NetStoreRef Store;
        public ulong BaselineId;
        public ulong StoreRevision;
        public List<RuntimeStoreBaselineSpawn> Spawns = new();
    }

    [Serializable]
    public sealed class RuntimeStoreBaselineSpawn
    {
        public long ObjectId;
        public Hash128 InstanceGuid;
        public long ParentObjectId = -1;
        public int SiblingIndex;
        public uint AssetNetId;
        public RuntimeObjectPatch Overrides;
    }

    /// <summary>
    /// Deterministic parent-before-child baseline payload used before chunking.
    /// </summary>
    public sealed class RuntimeStoreBaselineCodec
    {
        public const uint FORMAT_MAGIC = 0x32425347;
        public const uint FORMAT_VERSION = 1;
        public const int MAX_OBJECTS = 262_144;

        private readonly RuntimePatchCodecRegistry _patchRegistry;
        private readonly RuntimeObjectPatchNetworkCodec _patchCodec;

        public RuntimeStoreBaselineCodec(RuntimePatchCodecRegistry patchRegistry)
        {
            _patchRegistry = patchRegistry ?? throw new ArgumentNullException(nameof(patchRegistry));
            _patchCodec = new RuntimeObjectPatchNetworkCodec(patchRegistry);
        }

        public byte[] Encode(RuntimeStoreBaselinePayload value)
        {
            Validate(value);
            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(FORMAT_MAGIC);
            writer.WriteUInt32(FORMAT_VERSION);
            writer.WriteString(value.Store.StoreId.ToString());
            writer.WriteUInt32(value.Store.StoreGeneration);
            writer.WriteUInt64(value.BaselineId);
            writer.WriteUInt64(value.StoreRevision);
            writer.WriteInt32(value.Spawns.Count);
            for (var i = 0; i < value.Spawns.Count; i++)
            {
                var spawn = value.Spawns[i];
                writer.WriteInt64(spawn.ObjectId);
                writer.WriteHash128(spawn.InstanceGuid);
                writer.WriteInt64(spawn.ParentObjectId);
                writer.WriteInt32(spawn.SiblingIndex);
                writer.WriteUInt32(spawn.AssetNetId);
                writer.WriteBytes(_patchCodec.Encode(spawn.Overrides ?? new RuntimeObjectPatch(_patchRegistry.SchemaHash)));
            }

            var payload = writer.ToArray();
            if (payload.Length > RuntimeProtocolV2.MAX_BASELINE_BYTES)
                throw new InvalidOperationException($"Baseline payload is {payload.Length} bytes; maximum is {RuntimeProtocolV2.MAX_BASELINE_BYTES} bytes.");
            return payload;
        }

        public RuntimeStoreBaselinePayload Decode(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length > RuntimeProtocolV2.MAX_BASELINE_BYTES)
                throw new FormatException($"Baseline payload is {payload.Length} bytes; maximum is {RuntimeProtocolV2.MAX_BASELINE_BYTES} bytes.");

            var reader = new CanonicalPatchBinaryReader(payload);
            var magic = reader.ReadUInt32();
            if (magic != FORMAT_MAGIC)
                throw new FormatException($"Baseline magic 0x{magic:x8} does not match 0x{FORMAT_MAGIC:x8}.");
            var version = reader.ReadUInt32();
            if (version != FORMAT_VERSION)
                throw new FormatException($"Baseline format version {version} is not supported.");

            var storeIdText = reader.ReadString();
            if (string.IsNullOrWhiteSpace(storeIdText))
                throw new FormatException("Baseline store id is empty.");
            var result = new RuntimeStoreBaselinePayload
            {
                Store = new NetStoreRef(new Unity.Collections.FixedString32Bytes(storeIdText), reader.ReadUInt32()),
                BaselineId = reader.ReadUInt64(),
                StoreRevision = reader.ReadUInt64(),
            };
            var count = reader.ReadInt32();
            if (count < 0 || count > MAX_OBJECTS)
                throw new FormatException($"Invalid baseline object count {count}; expected 0..{MAX_OBJECTS}.");

            for (var i = 0; i < count; i++)
            {
                var spawn = new RuntimeStoreBaselineSpawn
                {
                    ObjectId = reader.ReadInt64(),
                    InstanceGuid = reader.ReadHash128(),
                    ParentObjectId = reader.ReadInt64(),
                    SiblingIndex = reader.ReadInt32(),
                    AssetNetId = reader.ReadUInt32(),
                };
                var patchPayload = reader.ReadBytes();
                if (patchPayload == null)
                    throw new FormatException($"Baseline object {spawn.ObjectId} has a null override payload.");
                spawn.Overrides = _patchCodec.Decode(patchPayload);
                result.Spawns.Add(spawn);
            }

            reader.RequireEnd();
            Validate(result);
            return result;
        }

        public static void Validate(RuntimeStoreBaselinePayload value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (value.Store.StoreId.Length == 0 || value.Store.StoreGeneration == 0)
                throw new InvalidOperationException($"Baseline has invalid store reference '{value.Store}'.");
            if (value.BaselineId == 0)
                throw new InvalidOperationException("Baseline id must be non-zero.");
            if (value.Spawns == null)
                throw new InvalidOperationException("Baseline spawn collection is null.");
            if (value.Spawns.Count > MAX_OBJECTS)
                throw new InvalidOperationException($"Baseline contains {value.Spawns.Count} objects; maximum is {MAX_OBJECTS}.");

            var objectIds = new HashSet<long>();
            var instanceGuids = new HashSet<Hash128>();
            var nextSiblingIndexByParent = new Dictionary<long, int>();
            for (var i = 0; i < value.Spawns.Count; i++)
            {
                var spawn = value.Spawns[i] ?? throw new InvalidOperationException($"Baseline spawn at index {i} is null.");
                if (spawn.ObjectId <= 0 || !objectIds.Add(spawn.ObjectId))
                    throw new InvalidOperationException($"Baseline contains invalid or duplicate object id {spawn.ObjectId}.");
                if (!spawn.InstanceGuid.isValid || !instanceGuids.Add(spawn.InstanceGuid))
                    throw new InvalidOperationException($"Baseline object {spawn.ObjectId} has invalid or duplicate instance guid {spawn.InstanceGuid}.");
                if (spawn.AssetNetId == 0)
                    throw new InvalidOperationException($"Baseline object {spawn.ObjectId} has invalid AssetNetId 0.");
                if (spawn.ParentObjectId != -1 && !objectIds.Contains(spawn.ParentObjectId))
                    throw new InvalidOperationException($"Baseline object {spawn.ObjectId} appears before missing parent {spawn.ParentObjectId}.");
                if (spawn.SiblingIndex < 0)
                    throw new InvalidOperationException($"Baseline object {spawn.ObjectId} has negative sibling index {spawn.SiblingIndex}.");

                nextSiblingIndexByParent.TryGetValue(spawn.ParentObjectId, out var expectedIndex);
                if (spawn.SiblingIndex != expectedIndex)
                {
                    throw new InvalidOperationException(
                        $"Baseline object {spawn.ObjectId} has sibling index {spawn.SiblingIndex} under parent {spawn.ParentObjectId}; expected contiguous index {expectedIndex}.");
                }
                nextSiblingIndexByParent[spawn.ParentObjectId] = expectedIndex + 1;
            }
        }
    }
}
