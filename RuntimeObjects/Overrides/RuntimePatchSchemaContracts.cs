using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public enum RuntimePatchFieldEncoding : byte
    {
        Value = 1,
        RuntimeReference = 2,
        CustomListVector2Int = 3,
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class RuntimePatchFieldKeyAttribute : Attribute
    {
        public readonly string Key;

        public RuntimePatchFieldKeyAttribute(string key)
        {
            Key = key;
        }
    }

    [Serializable, Preserve]
    public class RuntimePatchSchemaManifest
    {
        public int FormatVersion;
        public int CodecVersion;
        public string ComponentRegistryHash;
        public string SchemaHash;
        public List<RuntimePatchComponentSchema> Components = new();
    }

    [Serializable, Preserve]
    public class RuntimePatchComponentSchema
    {
        public int ComponentTypeId;
        public string ComponentTypeKey;
        public string RuntimeTypeName;
        public string AssemblyName;
        public bool Tombstone;
        public List<RuntimePatchFieldSchema> Fields = new();
    }

    [Serializable, Preserve]
    public class RuntimePatchFieldSchema
    {
        public int FieldId;
        public string FieldKey;
        public string FieldName;
        public string FieldTypeSignature;
        public RuntimePatchFieldEncoding Encoding;
        public bool Tombstone;
    }

    [Serializable, Preserve]
    public struct RuntimePatchObjectReference
    {
        public FixedString32Bytes StoreId;
        public Hash128 ObjectGuid;

        public RuntimePatchObjectReference(FixedString32Bytes storeId, Hash128 objectGuid)
        {
            StoreId = storeId;
            ObjectGuid = objectGuid;
        }
    }

    public abstract class RuntimePatchCodecContext
    {
        public abstract void WriteRuntimeInstance(CanonicalPatchBinaryWriter writer, in RuntimeInstance value);
        public abstract RuntimeInstance ReadRuntimeInstance(CanonicalPatchBinaryReader reader);
        public abstract bool RuntimeInstancesEqual(in RuntimeInstance first, in RuntimeInstance second);
    }
}
