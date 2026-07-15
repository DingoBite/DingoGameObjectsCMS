using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    [Serializable, Preserve]
    public struct GameAssetReference
    {
        public GameAssetKey RequestedKey;

        public GameAssetReference(GameAssetKey requestedKey)
        {
            RequestedKey = requestedKey;
        }
    }

    [Serializable, Preserve]
    public struct ResolvedGameAssetReference
    {
        public GameAssetKey ExactKey;
        public Hash128 AssetGuid;
        public string MaterializedContentHash;

        public ResolvedGameAssetReference(GameAssetKey exactKey, Hash128 assetGuid, string materializedContentHash)
        {
            ExactKey = exactKey;
            AssetGuid = assetGuid;
            MaterializedContentHash = materializedContentHash;
        }
    }

    [Serializable, Preserve]
    public struct GameAssetInstance
    {
        public Hash128 InstanceGuid;
        public GameAssetReference Asset;
        [SerializeReference]
        public RuntimeObjectPatch Patch;

        public GameAssetInstance(Hash128 instanceGuid, GameAssetReference asset, RuntimeObjectPatch patch)
        {
            InstanceGuid = instanceGuid;
            Asset = asset;
            Patch = patch;
        }
    }

    public static class GameAssetInstanceFactory
    {
        public static GameAssetInstance Create(
            GameAssetReference asset,
            Hash128 instanceGuid = default,
            RuntimeObjectPatch patch = null)
        {
            return new GameAssetInstance(
                instanceGuid.isValid ? instanceGuid : IdUtils.NewHash128FromGuid(),
                asset,
                patch);
        }

        public static GameAssetInstance Create(
            GameAssetScriptableObject asset,
            Hash128 instanceGuid = default,
            RuntimeObjectPatch patch = null)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            return Create(new GameAssetReference(asset.Key), instanceGuid, patch);
        }
    }

    [Serializable, Preserve]
    public struct RuntimeObjectOrigin
    {
        public ResolvedGameAssetReference Asset;
        public Hash128 InstanceGuid;

        public RuntimeObjectOrigin(ResolvedGameAssetReference asset, Hash128 instanceGuid)
        {
            Asset = asset;
            InstanceGuid = instanceGuid;
        }
    }

    public enum ComponentPatchKind : byte
    {
        None = 0,
        Add = 1,
        Remove = 2,
        Fields = 3,
        Custom = 4,
    }

    public enum FieldPatchKind : byte
    {
        Set = 1,
        Remove = 2,
    }

    public enum RuntimeObjectPatchRepresentation : byte
    {
        RuntimeBinary = 1,
        AuthoringCanonicalJson = 2,
    }

    [Serializable, Preserve]
    public class FieldPatch
    {
        [NonSerialized, JsonIgnore] public uint FieldId;
        public string FieldKey;
        public FieldPatchKind Kind;
        [NonSerialized, JsonIgnore] public byte[] Payload;
        public string CanonicalJson;

        public FieldPatch() { }

        public FieldPatch(uint fieldId, string fieldKey, FieldPatchKind kind, byte[] payload = null)
        {
            FieldId = fieldId;
            FieldKey = fieldKey;
            Kind = kind;
            Payload = payload;
        }

        public static FieldPatch Authoring(string fieldKey, FieldPatchKind kind, string canonicalJson = null)
        {
            return new FieldPatch
            {
                FieldKey = fieldKey,
                Kind = kind,
                CanonicalJson = canonicalJson,
            };
        }
    }

    [Serializable, Preserve]
    public class ComponentPatch
    {
        [NonSerialized, JsonIgnore] public uint ComponentTypeId;
        public string ComponentTypeKey;
        public ComponentPatchKind Kind;
        [NonSerialized, JsonIgnore] public byte[] Payload;
        public string CanonicalJson;
        public List<FieldPatch> Fields = new();

        public ComponentPatch() { }

        public ComponentPatch(uint componentTypeId, string componentTypeKey, ComponentPatchKind kind, byte[] payload = null)
        {
            ComponentTypeId = componentTypeId;
            ComponentTypeKey = componentTypeKey;
            Kind = kind;
            Payload = payload;
        }

        public static ComponentPatch Authoring(
            string componentTypeKey,
            ComponentPatchKind kind,
            string canonicalJson = null)
        {
            return new ComponentPatch
            {
                ComponentTypeKey = componentTypeKey,
                Kind = kind,
                CanonicalJson = canonicalJson,
            };
        }
    }

    [Serializable, Preserve]
    public class RuntimeObjectPatch
    {
        public RuntimeObjectPatchRepresentation Representation = RuntimeObjectPatchRepresentation.RuntimeBinary;
        public string SchemaHash;
        public List<ComponentPatch> Components = new();

        [JsonIgnore]
        public bool IsEmpty => Components == null || Components.Count == 0;

        public RuntimeObjectPatch() { }

        public RuntimeObjectPatch(string schemaHash)
        {
            if (string.IsNullOrWhiteSpace(schemaHash))
                throw new ArgumentException("Runtime object patch schema hash is required.", nameof(schemaHash));
            SchemaHash = schemaHash;
        }

        public RuntimeObjectPatch(string schemaHash, RuntimeObjectPatchRepresentation representation)
            : this(schemaHash)
        {
            if (representation != RuntimeObjectPatchRepresentation.RuntimeBinary
                && representation != RuntimeObjectPatchRepresentation.AuthoringCanonicalJson)
            {
                throw new ArgumentOutOfRangeException(nameof(representation), representation, "Unsupported runtime object patch representation.");
            }
            Representation = representation;
        }
    }
}
