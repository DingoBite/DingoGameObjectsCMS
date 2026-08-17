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

        /// <summary>
        /// Authored sparse override of <see cref="Asset"/>, in the same document
        /// vocabulary a prefab uses. A placement carrying one resolves to a
        /// derived asset instead of the referenced one, which is how a one-off
        /// variant stays inside the level that needs it.
        /// </summary>
        public GameAssetOverrides Overrides;

        /// <summary>
        /// Runtime-binary override carried by the replication lane. This is not
        /// authored content: authored overrides go through
        /// <see cref="Overrides"/>.
        /// </summary>
        [SerializeReference]
        public RuntimeObjectPatch Patch;

        public GameAssetInstance(Hash128 instanceGuid, GameAssetReference asset, RuntimeObjectPatch patch)
        {
            InstanceGuid = instanceGuid;
            Asset = asset;
            Overrides = null;
            Patch = patch;
        }

        public GameAssetInstance WithOverrides(GameAssetOverrides overrides)
        {
            var result = this;
            result.Overrides = overrides;
            return result;
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
                instanceGuid.isValid ? instanceGuid : RuntimeInstanceIdentity.Next(),
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
        /// <summary>
        /// Adds only component presence. The receiver creates the component
        /// with its default value; semantic state must arrive through the
        /// component's typed hot-state stream.
        /// </summary>
        AddPresence = 5,
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
        public uint FieldId;
        public FieldPatchKind Kind;
        [NonSerialized, JsonIgnore] public byte[] Payload;
        public string CanonicalJson;

        public FieldPatch() { }

        public FieldPatch(uint fieldId, FieldPatchKind kind, byte[] payload = null)
        {
            FieldId = fieldId;
            Kind = kind;
            Payload = payload;
        }

        public static FieldPatch Authoring(uint fieldId, FieldPatchKind kind, string canonicalJson = null)
        {
            return new FieldPatch
            {
                FieldId = fieldId,
                Kind = kind,
                CanonicalJson = canonicalJson,
            };
        }
    }

    [Serializable, Preserve]
    public class ComponentPatch
    {
        public uint ComponentTypeId;
        public ComponentPatchKind Kind;
        [NonSerialized, JsonIgnore] public byte[] Payload;
        public string CanonicalJson;
        public List<FieldPatch> Fields = new();

        public ComponentPatch() { }

        public ComponentPatch(uint componentTypeId, ComponentPatchKind kind, byte[] payload = null)
        {
            ComponentTypeId = componentTypeId;
            Kind = kind;
            Payload = payload;
        }

        public static ComponentPatch Authoring(
            uint componentTypeId,
            ComponentPatchKind kind,
            string canonicalJson = null)
        {
            return new ComponentPatch
            {
                ComponentTypeId = componentTypeId,
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
