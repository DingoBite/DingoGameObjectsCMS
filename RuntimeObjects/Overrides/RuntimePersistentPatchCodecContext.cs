using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.Stores;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    /// <summary>
    /// Authoring/save representation of RuntimeInstance. It stores stable
    /// StoreId + object GUID and reconstructs the process-local id/epoch.
    /// </summary>
    public sealed class RuntimePersistentPatchCodecContext : RuntimePatchCodecContext
    {
        private readonly Func<RuntimeInstance, RuntimePatchObjectReference> _encode;
        private readonly Func<RuntimePatchObjectReference, RuntimeInstance> _decode;

        public RuntimePersistentPatchCodecContext(
            Func<RuntimeInstance, RuntimePatchObjectReference> encode,
            Func<RuntimePatchObjectReference, RuntimeInstance> decode)
        {
            _encode = encode ?? throw new ArgumentNullException(nameof(encode));
            _decode = decode ?? throw new ArgumentNullException(nameof(decode));
        }

        public static RuntimePersistentPatchCodecContext ForActiveRealm(StoreRealm realm)
        {
            return new RuntimePersistentPatchCodecContext(
                value =>
                {
                    if (!RuntimeStores.TryGetRuntimeStore(value.StoreId, realm, out var store)
                        || !store.IsRuntimeInstanceActive(value)
                        || !store.TryTakeRO(value.Id, out var runtimeObject)
                        || runtimeObject == null
                        || !runtimeObject.GUID.isValid)
                    {
                        throw new InvalidOperationException(
                            $"Runtime reference '{value.StoreId}/{value.Id}' epoch {value.Epoch} is not active in realm {realm}.");
                    }

                    return new RuntimePatchObjectReference(value.StoreId, runtimeObject.GUID);
                },
                reference =>
                {
                    if (!RuntimeStores.TryGetRuntimeStore(reference.StoreId, realm, out var store)
                        || !store.TryTakeRO(reference.ObjectGuid, out var runtimeObject)
                        || runtimeObject == null)
                    {
                        throw new InvalidOperationException(
                            $"Persistent runtime reference '{reference.StoreId}/{reference.ObjectGuid}' is not active in realm {realm}.");
                    }

                    return runtimeObject.RuntimeInstance;
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

            var reference = EncodePersistentReference(value);
            writer.WriteBoolean(true);
            writer.WriteString(reference.StoreId.ToString());
            writer.WriteHash128(reference.ObjectGuid);
        }

        public override RuntimeInstance ReadRuntimeInstance(CanonicalPatchBinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (!reader.ReadBoolean())
                return default;
            var storeId = reader.ReadString();
            if (string.IsNullOrWhiteSpace(storeId))
                throw new FormatException("Persistent RuntimeInstance reference has an empty StoreId.");
            return DecodePersistentReference(new RuntimePatchObjectReference(
                new Unity.Collections.FixedString32Bytes(storeId),
                reader.ReadHash128()));
        }

        public override bool RuntimeInstancesEqual(in RuntimeInstance first, in RuntimeInstance second)
        {
            var firstDefault = IsDefault(first);
            var secondDefault = IsDefault(second);
            if (firstDefault || secondDefault)
                return firstDefault == secondDefault;

            var firstReference = EncodePersistentReference(first);
            var secondReference = EncodePersistentReference(second);
            return firstReference.StoreId.Equals(secondReference.StoreId)
                   && firstReference.ObjectGuid == secondReference.ObjectGuid;
        }

        public RuntimePatchObjectReference EncodePersistentReference(in RuntimeInstance value)
        {
            if (IsDefault(value))
                return default;
            var reference = _encode(value);
            if (reference.StoreId.Length == 0 || !reference.ObjectGuid.isValid)
                throw new InvalidOperationException("Persistent RuntimeInstance encoder returned an invalid object reference.");
            return reference;
        }

        public RuntimeInstance DecodePersistentReference(in RuntimePatchObjectReference reference)
        {
            if (reference.StoreId.Length == 0 && !reference.ObjectGuid.isValid)
                return default;
            if (reference.StoreId.Length == 0 || !reference.ObjectGuid.isValid)
                throw new InvalidOperationException("Persistent RuntimeInstance reference requires both StoreId and object GUID.");
            return _decode(reference);
        }

        public static bool IsDefaultRuntimeInstance(in RuntimeInstance value) => IsDefault(value);

        private static bool IsDefault(in RuntimeInstance value)
        {
            return value.StoreId.Length == 0 && value.Id == 0 && value.Epoch == 0;
        }
    }
}
