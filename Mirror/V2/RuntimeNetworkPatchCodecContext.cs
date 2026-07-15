using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Stores;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Protocol-v2 RuntimeInstance codec. Epoch is always reconstructed from
    /// local state and is never written to the network payload.
    /// </summary>
    public sealed class RuntimeNetworkPatchCodecContext : RuntimePatchCodecContext
    {
        private readonly Func<RuntimeInstance, NetObjectRef> _encode;
        private readonly Func<NetObjectRef, RuntimeInstance> _decode;

        public RuntimeNetworkPatchCodecContext(
            Func<RuntimeInstance, NetObjectRef> encode,
            Func<NetObjectRef, RuntimeInstance> decode)
        {
            _encode = encode ?? throw new ArgumentNullException(nameof(encode));
            _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        }

        public static RuntimeNetworkPatchCodecContext ForActiveRealm(StoreRealm realm)
        {
            return new RuntimeNetworkPatchCodecContext(
                value =>
                {
                    if (!RuntimeStores.TryGetRuntimeStore(value.StoreId, realm, out var store)
                        || !store.IsRuntimeInstanceActive(value)
                        || !store.TryTakeRO(value.Id, out _))
                    {
                        throw new InvalidOperationException(
                            $"Runtime reference '{value.StoreId}/{value.Id}' epoch {value.Epoch} is not active in realm {realm}.");
                    }

                    return NetObjectRef.FromRuntimeInstance(value, store.StoreGeneration);
                },
                value =>
                {
                    if (!RuntimeStores.TryGetRuntimeStore(value.Store.StoreId, value.Store.StoreGeneration, realm, out var store)
                        || !store.TryTakeRO(value.ObjectId, out _))
                    {
                        throw new InvalidOperationException($"Network object reference '{value}' is not active in realm {realm}.");
                    }

                    return value.ToRuntimeInstance(store.Epoch);
                });
        }

        public override void WriteRuntimeInstance(CanonicalPatchBinaryWriter writer, in RuntimeInstance value)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (IsDefault(value))
            {
                writer.WriteBoolean(false);
                return;
            }

            var reference = _encode(value);
            if (!reference.IsValid)
                throw new InvalidOperationException($"Runtime reference encoder returned invalid NetObjectRef '{reference}'.");
            writer.WriteBoolean(true);
            writer.WriteString(reference.Store.StoreId.ToString());
            writer.WriteUInt32(reference.Store.StoreGeneration);
            writer.WriteInt64(reference.ObjectId);
        }

        public override RuntimeInstance ReadRuntimeInstance(CanonicalPatchBinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (!reader.ReadBoolean())
                return default;
            var storeId = reader.ReadString();
            if (string.IsNullOrWhiteSpace(storeId))
                throw new FormatException("Network RuntimeInstance reference has an empty StoreId.");
            var reference = new NetObjectRef(
                new NetStoreRef(new Unity.Collections.FixedString32Bytes(storeId), reader.ReadUInt32()),
                reader.ReadInt64());
            if (!reference.IsValid)
                throw new FormatException($"Network RuntimeInstance reference '{reference}' is invalid.");
            return _decode(reference);
        }

        public override bool RuntimeInstancesEqual(in RuntimeInstance first, in RuntimeInstance second)
        {
            var firstDefault = IsDefault(first);
            var secondDefault = IsDefault(second);
            if (firstDefault || secondDefault)
                return firstDefault == secondDefault;
            return _encode(first).Equals(_encode(second));
        }

        private static bool IsDefault(in RuntimeInstance value)
        {
            return value.StoreId.Length == 0 && value.Id == 0 && value.Epoch == 0;
        }
    }
}
