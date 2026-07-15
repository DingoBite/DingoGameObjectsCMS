using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using UnityEngine;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeStoreDeltaOperationKind : byte
    {
        Spawn = 1,
        Remove = 2,
        Reparent = 3,
        Move = 4,
        Patch = 5,
    }

    public enum RuntimeStoreDeltaKind : byte
    {
        Mutation = 1,
        Interest = 2,
    }

    [Serializable]
    public sealed class RuntimeStoreDeltaPayload
    {
        public RuntimeStoreDeltaKind Kind = RuntimeStoreDeltaKind.Mutation;
        public NetStoreRef Store;
        public ulong BaselineId;
        public ulong DeliverySequence;
        public ulong FromRevision;
        public ulong ToRevision;
        public List<RuntimeStoreDeltaOperation> Operations = new();
    }

    [Serializable]
    public sealed class RuntimeStoreDeltaOperation
    {
        public RuntimeStoreDeltaOperationKind Kind;
        public long ObjectId;
        public Hash128 InstanceGuid;
        public long ParentObjectId = -1;
        public int SiblingIndex = -1;
        public uint AssetNetId;
        public byte RemoveSubtree;
        public RuntimeObjectPatch Patch;
    }

    public sealed class RuntimeStoreDeltaCodec
    {
        public const uint FORMAT_MAGIC = 0x32445347;
        public const uint FORMAT_VERSION = 2;
        public const int MAX_OPERATIONS = 65_536;
        public const int MAX_PAYLOAD_BYTES = RuntimeProtocolV2.MAX_PENDING_ENVELOPE_BYTES;

        private readonly RuntimePatchCodecRegistry _patchRegistry;
        private readonly RuntimeObjectPatchNetworkCodec _patchCodec;

        public RuntimeStoreDeltaCodec(RuntimePatchCodecRegistry patchRegistry)
        {
            _patchRegistry = patchRegistry ?? throw new ArgumentNullException(nameof(patchRegistry));
            _patchCodec = new RuntimeObjectPatchNetworkCodec(patchRegistry);
        }

        public byte[] Encode(RuntimeStoreDeltaPayload value)
        {
            Validate(value);
            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(FORMAT_MAGIC);
            writer.WriteUInt32(FORMAT_VERSION);
            writer.WriteByte((byte)value.Kind);
            writer.WriteString(value.Store.StoreId.ToString());
            writer.WriteUInt32(value.Store.StoreGeneration);
            writer.WriteUInt64(value.BaselineId);
            writer.WriteUInt64(value.DeliverySequence);
            writer.WriteUInt64(value.FromRevision);
            writer.WriteUInt64(value.ToRevision);
            writer.WriteInt32(value.Operations.Count);
            for (var i = 0; i < value.Operations.Count; i++)
                WriteOperation(writer, value.Operations[i]);

            var payload = writer.ToArray();
            if (payload.Length > MAX_PAYLOAD_BYTES)
                throw new InvalidOperationException($"Store delta is {payload.Length} bytes; maximum is {MAX_PAYLOAD_BYTES} bytes.");
            return payload;
        }

        public RuntimeStoreDeltaPayload Decode(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length > MAX_PAYLOAD_BYTES)
                throw new FormatException($"Store delta is {payload.Length} bytes; maximum is {MAX_PAYLOAD_BYTES} bytes.");

            var reader = new CanonicalPatchBinaryReader(payload);
            var magic = reader.ReadUInt32();
            if (magic != FORMAT_MAGIC)
                throw new FormatException($"Store delta magic 0x{magic:x8} does not match 0x{FORMAT_MAGIC:x8}.");
            var version = reader.ReadUInt32();
            if (version != FORMAT_VERSION)
                throw new FormatException($"Store delta version {version} is not supported.");
            var kind = (RuntimeStoreDeltaKind)reader.ReadByte();
            var storeId = reader.ReadString();
            if (string.IsNullOrWhiteSpace(storeId))
                throw new FormatException("Store delta has an empty store id.");

            var result = new RuntimeStoreDeltaPayload
            {
                Kind = kind,
                Store = new NetStoreRef(new Unity.Collections.FixedString32Bytes(storeId), reader.ReadUInt32()),
                BaselineId = reader.ReadUInt64(),
                DeliverySequence = reader.ReadUInt64(),
                FromRevision = reader.ReadUInt64(),
                ToRevision = reader.ReadUInt64(),
            };
            var operationCount = reader.ReadInt32();
            if (operationCount < 0 || operationCount > MAX_OPERATIONS)
                throw new FormatException($"Invalid store delta operation count {operationCount}; expected 0..{MAX_OPERATIONS}.");
            for (var i = 0; i < operationCount; i++)
                result.Operations.Add(ReadOperation(reader));
            reader.RequireEnd();
            Validate(result);
            return result;
        }

        private void WriteOperation(CanonicalPatchBinaryWriter writer, RuntimeStoreDeltaOperation operation)
        {
            writer.WriteByte((byte)operation.Kind);
            writer.WriteInt64(operation.ObjectId);
            switch (operation.Kind)
            {
                case RuntimeStoreDeltaOperationKind.Spawn:
                    writer.WriteHash128(operation.InstanceGuid);
                    writer.WriteInt64(operation.ParentObjectId);
                    writer.WriteInt32(operation.SiblingIndex);
                    writer.WriteUInt32(operation.AssetNetId);
                    writer.WriteBytes(_patchCodec.Encode(operation.Patch ?? new RuntimeObjectPatch(_patchRegistry.SchemaHash)));
                    return;
                case RuntimeStoreDeltaOperationKind.Remove:
                    writer.WriteByte(operation.RemoveSubtree);
                    return;
                case RuntimeStoreDeltaOperationKind.Reparent:
                case RuntimeStoreDeltaOperationKind.Move:
                    writer.WriteInt64(operation.ParentObjectId);
                    writer.WriteInt32(operation.SiblingIndex);
                    return;
                case RuntimeStoreDeltaOperationKind.Patch:
                    writer.WriteBytes(_patchCodec.Encode(operation.Patch));
                    return;
                default:
                    throw new InvalidOperationException($"Unsupported store delta operation kind {operation.Kind}.");
            }
        }

        private RuntimeStoreDeltaOperation ReadOperation(CanonicalPatchBinaryReader reader)
        {
            var result = new RuntimeStoreDeltaOperation
            {
                Kind = (RuntimeStoreDeltaOperationKind)reader.ReadByte(),
                ObjectId = reader.ReadInt64(),
            };
            switch (result.Kind)
            {
                case RuntimeStoreDeltaOperationKind.Spawn:
                    result.InstanceGuid = reader.ReadHash128();
                    result.ParentObjectId = reader.ReadInt64();
                    result.SiblingIndex = reader.ReadInt32();
                    result.AssetNetId = reader.ReadUInt32();
                    result.Patch = DecodePatch(reader, result.ObjectId);
                    return result;
                case RuntimeStoreDeltaOperationKind.Remove:
                    result.RemoveSubtree = reader.ReadByte();
                    return result;
                case RuntimeStoreDeltaOperationKind.Reparent:
                case RuntimeStoreDeltaOperationKind.Move:
                    result.ParentObjectId = reader.ReadInt64();
                    result.SiblingIndex = reader.ReadInt32();
                    return result;
                case RuntimeStoreDeltaOperationKind.Patch:
                    result.Patch = DecodePatch(reader, result.ObjectId);
                    return result;
                default:
                    throw new FormatException($"Unsupported store delta operation kind {result.Kind}.");
            }
        }

        private RuntimeObjectPatch DecodePatch(CanonicalPatchBinaryReader reader, long objectId)
        {
            var payload = reader.ReadBytes();
            if (payload == null)
                throw new FormatException($"Store delta object {objectId} has a null patch payload.");
            return _patchCodec.Decode(payload);
        }

        private static void Validate(RuntimeStoreDeltaPayload value)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (value.Store.StoreId.Length == 0 || value.Store.StoreGeneration == 0)
                throw new InvalidOperationException($"Store delta has invalid store reference '{value.Store}'.");
            if (value.BaselineId == 0 || value.DeliverySequence == 0)
                throw new InvalidOperationException("Store delta requires non-zero baseline and delivery sequence ids.");
            if (value.Kind != RuntimeStoreDeltaKind.Mutation
                && value.Kind != RuntimeStoreDeltaKind.Interest)
            {
                throw new InvalidOperationException($"Store delta kind {value.Kind} is invalid.");
            }
            if (value.Kind == RuntimeStoreDeltaKind.Mutation && value.ToRevision <= value.FromRevision)
                throw new InvalidOperationException($"Mutation delta revision range {value.FromRevision}..{value.ToRevision} is not increasing.");
            if (value.Kind == RuntimeStoreDeltaKind.Interest && value.ToRevision < value.FromRevision)
                throw new InvalidOperationException($"Interest delta revision range {value.FromRevision}..{value.ToRevision} is inverted.");
            if (value.Operations == null || value.Operations.Count == 0)
                throw new InvalidOperationException("Filtered revisions without data must not emit an empty store delta.");
            if (value.Operations.Count > MAX_OPERATIONS)
                throw new InvalidOperationException($"Store delta has {value.Operations.Count} operations; maximum is {MAX_OPERATIONS}.");

            for (var i = 0; i < value.Operations.Count; i++)
            {
                var operation = value.Operations[i] ?? throw new InvalidOperationException($"Store delta operation {i} is null.");
                if (operation.ObjectId <= 0)
                    throw new InvalidOperationException($"Store delta operation {i} has invalid object id {operation.ObjectId}.");
                switch (operation.Kind)
                {
                    case RuntimeStoreDeltaOperationKind.Spawn:
                        if (!operation.InstanceGuid.isValid || operation.AssetNetId == 0 || operation.SiblingIndex < 0)
                            throw new InvalidOperationException($"Store delta spawn {operation.ObjectId} is incomplete.");
                        break;
                    case RuntimeStoreDeltaOperationKind.Remove:
                        if (operation.RemoveSubtree > 1)
                            throw new InvalidOperationException($"Store delta remove {operation.ObjectId} has invalid subtree flag {operation.RemoveSubtree}.");
                        if (value.Kind == RuntimeStoreDeltaKind.Interest && operation.RemoveSubtree != 0)
                            throw new InvalidOperationException($"Interest delta remove {operation.ObjectId} must preserve independently projected descendants.");
                        break;
                    case RuntimeStoreDeltaOperationKind.Reparent:
                        if (operation.ParentObjectId < -1 || operation.SiblingIndex < 0)
                            throw new InvalidOperationException($"Store delta reparent {operation.ObjectId} has invalid parent/index.");
                        break;
                    case RuntimeStoreDeltaOperationKind.Move:
                        if (operation.ParentObjectId <= 0 || operation.SiblingIndex < 0)
                            throw new InvalidOperationException($"Store delta move {operation.ObjectId} has invalid parent/index.");
                        break;
                    case RuntimeStoreDeltaOperationKind.Patch:
                        if (value.Kind == RuntimeStoreDeltaKind.Interest)
                            throw new InvalidOperationException($"Interest delta cannot patch existing object {operation.ObjectId}.");
                        if (operation.Patch == null)
                            throw new InvalidOperationException($"Store delta patch {operation.ObjectId} is null.");
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported store delta operation kind {operation.Kind}.");
                }
            }
        }
    }
}
