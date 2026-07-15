using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public sealed class GameAssetTemplateBlueprint
    {
        private readonly Dictionary<uint, byte[]> _componentPayloads;
        private readonly ReadOnlyCollection<uint> _componentTypeIds;

        public ResolvedGameAssetReference Asset { get; }
        public Hash128 SourceAssetGuid { get; }
        public IReadOnlyList<uint> ComponentTypeIds => _componentTypeIds;

        internal GameAssetTemplateBlueprint(
            ResolvedGameAssetReference asset,
            Hash128 sourceAssetGuid,
            IReadOnlyDictionary<uint, byte[]> componentPayloads)
        {
            Asset = asset;
            SourceAssetGuid = sourceAssetGuid;
            _componentPayloads = new Dictionary<uint, byte[]>(componentPayloads.Count);
            foreach (var pair in componentPayloads)
                _componentPayloads.Add(pair.Key, (byte[])pair.Value.Clone());
            _componentTypeIds = Array.AsReadOnly(_componentPayloads.Keys.OrderBy(value => value).ToArray());
        }

        internal Dictionary<uint, GameRuntimeComponent> DecodeComponents(
            RuntimePatchCodecRegistry registry,
            RuntimePatchCodecContext context)
        {
            var result = new Dictionary<uint, GameRuntimeComponent>(_componentPayloads.Count);
            foreach (var componentTypeId in _componentTypeIds)
            {
                result.Add(componentTypeId, DecodeComponent(componentTypeId, registry, context));
            }

            return result;
        }

        internal GameRuntimeComponent DecodeComponent(
            uint componentTypeId,
            RuntimePatchCodecRegistry registry,
            RuntimePatchCodecContext context)
        {
            if (!_componentPayloads.TryGetValue(componentTypeId, out var payload))
                throw new InvalidOperationException($"GameAsset baseline has no component {componentTypeId} to materialize.");

            var component = registry.Get(componentTypeId).DecodeCanonical(payload, context);
            if (component == null)
                throw new InvalidOperationException($"Patch codec '{componentTypeId}' decoded a null baseline component.");
            if (!RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var decodedTypeId) || decodedTypeId != componentTypeId)
            {
                throw new InvalidOperationException(
                    $"Patch codec '{componentTypeId}' decoded component '{component.GetType().FullName}' with mismatched runtime type id '{decodedTypeId}'.");
            }

            return component;
        }
    }

    public sealed class GameAssetTemplateCache
    {
        public const uint MATERIALIZER_VERSION = 2;

        private readonly RuntimePatchCodecRegistry _registry;
        private readonly RuntimePatchCodecContext _context;
        private readonly RuntimeObjectPatchEngine _patchEngine;
        private readonly RuntimeObjectPatchAuthoringCodec _authoringCodec;
        private readonly Dictionary<Hash128, GameAssetTemplateBlueprint> _byAssetGuid = new();

        public RuntimePatchCodecRegistry CodecRegistry => _registry;

        public GameAssetTemplateCache(RuntimePatchCodecRegistry registry, RuntimePatchCodecContext context)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            if (string.IsNullOrWhiteSpace(registry.SchemaHash))
                throw new InvalidOperationException("Runtime patch codec registry requires a non-empty schema hash.");
            _patchEngine = new RuntimeObjectPatchEngine(registry, context);
            _authoringCodec = new RuntimeObjectPatchAuthoringCodec(registry);
        }

        public GameAssetTemplateBlueprint GetOrCreate(GameAssetReference request, GameAsset asset)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));
            ValidateResolvedAsset(request, asset);

            if (_byAssetGuid.TryGetValue(asset.GUID, out var cached))
            {
                if (!KeysEqual(cached.Asset.ExactKey, asset.Key))
                    throw new InvalidOperationException($"Asset GUID '{asset.GUID}' resolves to both '{cached.Asset.ExactKey}' and '{asset.Key}'.");
                return cached;
            }

            var materialized = new GameRuntimeObject();
            asset.SetupRuntimeObject(materialized);

            var payloads = new Dictionary<uint, byte[]>();
            var components = materialized.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i] ?? throw new InvalidOperationException($"GameAsset '{asset.Key}' materialized a null runtime component at index {i}.");
                if (!RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var componentTypeId))
                    throw new InvalidOperationException($"GameAsset '{asset.Key}' materialized unregistered runtime component '{component.GetType().FullName}'.");
                if (!payloads.TryAdd(componentTypeId, _registry.Get(componentTypeId).EncodeCanonical(component, _context)))
                    throw new InvalidOperationException($"GameAsset '{asset.Key}' materialized duplicate runtime component id '{componentTypeId}'.");
            }

            var contentHash = CalculateContentHash(asset.Key, payloads);
            var resolved = new ResolvedGameAssetReference(asset.Key, asset.GUID, contentHash);
            var blueprint = new GameAssetTemplateBlueprint(resolved, asset.SourceAssetGUID, payloads);
            _byAssetGuid.Add(asset.GUID, blueprint);
            return blueprint;
        }

        public GameAssetTemplateBlueprint ResolveStrict(GameAssetReference request, GameAssetLibraryLock assetLock)
        {
            if (assetLock == null)
                throw new InvalidOperationException($"Cannot resolve GameAsset '{request.RequestedKey}' without an immutable asset lock.");
            if (assetLock.FormatVersion != GameAssetLibraryLock.CURRENT_FORMAT_VERSION)
                throw new InvalidOperationException($"Asset lock format '{assetLock.FormatVersion}' does not match required format '{GameAssetLibraryLock.CURRENT_FORMAT_VERSION}'.");
            if (!assetLock.TryGet(request.RequestedKey, out var entry) || entry == null)
                throw new InvalidOperationException($"Asset lock has no entry for '{request.RequestedKey}'.");
            if (string.IsNullOrWhiteSpace(entry.MaterializedContentHash))
                throw new InvalidOperationException($"Asset lock entry '{request.RequestedKey}' has no materialized content hash.");
            if (!GameAssetLibraryLockBuilder.TryResolve(request.RequestedKey, assetLock, out var resolvedAsset)
                || resolvedAsset is not GameAsset gameAsset)
                throw new InvalidOperationException($"Asset lock entry '{request.RequestedKey}' cannot be resolved exactly.");

            var blueprint = GetOrCreate(request, gameAsset);
            if (!string.Equals(blueprint.Asset.MaterializedContentHash, entry.MaterializedContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Materialized content hash mismatch for '{entry.ResolvedKey}'. Lock={entry.MaterializedContentHash}, current={blueprint.Asset.MaterializedContentHash}.");
            }

            return blueprint;
        }

        public GameRuntimeObject Materialize(GameAssetInstance instance, GameAssetLibraryLock assetLock)
        {
            return Materialize(instance, assetLock, _context);
        }

        public GameRuntimeObject Materialize(
            GameAssetInstance instance,
            GameAssetLibraryLock assetLock,
            RuntimePatchCodecContext patchContext)
        {
            if (patchContext == null)
                throw new ArgumentNullException(nameof(patchContext));
            if (!instance.InstanceGuid.isValid)
                throw new InvalidOperationException("GameAsset instance requires a stable InstanceGuid.");

            var blueprint = ResolveStrict(instance.Asset, assetLock);
            var components = blueprint.DecodeComponents(_registry, _context);
            if (instance.Patch != null)
            {
                var patchEngine = ReferenceEquals(patchContext, _context)
                    ? _patchEngine
                    : new RuntimeObjectPatchEngine(_registry, patchContext);
                var runtimePatch = _authoringCodec.MaterializeRuntimePatch(
                    components,
                    instance.Patch,
                    patchContext);
                components = patchEngine.ApplyPatch(components, runtimePatch);
            }

            return CreateRuntimeObject(instance, blueprint, components);
        }

        /// <summary>
        /// Materializes an already validated runtime-binary projection. Unlike
        /// authored local overrides, this path applies only the component lanes
        /// selected by the caller and never semantic-clones untouched values.
        /// </summary>
        public GameRuntimeObject MaterializeProjected(
            GameAssetInstance instance,
            GameAssetLibraryLock assetLock,
            RuntimePatchCodecContext patchContext,
            Func<uint, RuntimeComponentPatchProjectionMode> selectMode)
        {
            if (patchContext == null)
                throw new ArgumentNullException(nameof(patchContext));
            if (selectMode == null)
                throw new ArgumentNullException(nameof(selectMode));
            if (!instance.InstanceGuid.isValid)
                throw new InvalidOperationException("GameAsset instance requires a stable InstanceGuid.");

            var blueprint = ResolveStrict(instance.Asset, assetLock);
            var components = blueprint.DecodeComponents(_registry, _context);
            if (instance.Patch != null)
            {
                if (instance.Patch.Representation != RuntimeObjectPatchRepresentation.RuntimeBinary)
                {
                    throw new InvalidOperationException(
                        $"Projected materialization requires {RuntimeObjectPatchRepresentation.RuntimeBinary}, " +
                        $"received {instance.Patch.Representation}.");
                }

                var patchEngine = ReferenceEquals(patchContext, _context)
                    ? _patchEngine
                    : new RuntimeObjectPatchEngine(_registry, patchContext);
                components = patchEngine.ApplyProjectedPatch(components, instance.Patch, selectMode);
            }

            return CreateRuntimeObject(instance, blueprint, components);
        }

        public RuntimeObjectPatch BuildOverrides(
            GameRuntimeObject runtimeObject,
            GameAssetLibraryLock assetLock,
            RuntimePatchCodecContext patchContext = null)
        {
            return BuildProjectedOverrides(
                runtimeObject,
                assetLock,
                patchContext,
                _ => RuntimeComponentPatchProjectionMode.SemanticDiff);
        }

        public RuntimeObjectPatch BuildProjectedOverrides(
            GameRuntimeObject runtimeObject,
            GameAssetLibraryLock assetLock,
            RuntimePatchCodecContext patchContext,
            Func<uint, RuntimeComponentPatchProjectionMode> selectMode)
        {
            if (runtimeObject == null)
                throw new ArgumentNullException(nameof(runtimeObject));
            if (selectMode == null)
                throw new ArgumentNullException(nameof(selectMode));
            var origin = runtimeObject.Origin;
            if (!origin.InstanceGuid.isValid || origin.InstanceGuid != runtimeObject.GUID)
                throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} has no valid GA instance origin.");
            if (!origin.Asset.AssetGuid.isValid || string.IsNullOrWhiteSpace(origin.Asset.MaterializedContentHash))
                throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} has no exact GA baseline origin.");

            var blueprint = ResolveStrict(new GameAssetReference(origin.Asset.ExactKey), assetLock);
            if (!KeysEqual(blueprint.Asset.ExactKey, origin.Asset.ExactKey)
                || blueprint.Asset.AssetGuid != origin.Asset.AssetGuid
                || !string.Equals(
                    blueprint.Asset.MaterializedContentHash,
                    origin.Asset.MaterializedContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} origin no longer matches its immutable GA blueprint.");
            }

            var current = new Dictionary<uint, GameRuntimeComponent>();
            var components = runtimeObject.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i]
                                ?? throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} contains a null component at index {i}.");
                if (!RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var componentTypeId))
                    throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} contains unregistered component '{component.GetType().FullName}'.");
                if (!current.TryAdd(componentTypeId, component))
                    throw new InvalidOperationException($"Runtime object {runtimeObject.InstanceId} contains duplicate component id {componentTypeId}.");
            }

            var context = patchContext ?? _context;
            var engine = ReferenceEquals(context, _context)
                ? _patchEngine
                : new RuntimeObjectPatchEngine(_registry, context);
            return engine.BuildProjectedPatch(
                blueprint.ComponentTypeIds,
                current,
                componentTypeId => blueprint.DecodeComponent(componentTypeId, _registry, _context),
                selectMode);
        }

        public void Clear() => _byAssetGuid.Clear();

        private static GameRuntimeObject CreateRuntimeObject(
            GameAssetInstance instance,
            GameAssetTemplateBlueprint blueprint,
            IReadOnlyDictionary<uint, GameRuntimeComponent> components)
        {
            var runtimeObject = new GameRuntimeObject
            {
                Key = blueprint.Asset.ExactKey,
                AssetGUID = blueprint.Asset.AssetGuid,
                SourceAssetGUID = blueprint.SourceAssetGuid,
            };
            runtimeObject.SetGuidRequired(instance.InstanceGuid);
            runtimeObject.SetOrigin(new RuntimeObjectOrigin(blueprint.Asset, instance.InstanceGuid));

            foreach (var pair in components.OrderBy(pair => pair.Key))
                runtimeObject.AddOrReplaceById(pair.Key, pair.Value);
            runtimeObject.ClearDirty();
            return runtimeObject;
        }

        private string CalculateContentHash(
            GameAssetKey exactKey,
            IReadOnlyDictionary<uint, byte[]> componentPayloads)
        {
            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(MATERIALIZER_VERSION);
            writer.WriteString(_registry.SchemaHash);
            writer.WriteString(exactKey.Mod);
            writer.WriteString(exactKey.Type);
            writer.WriteString(exactKey.Key);
            writer.WriteString(exactKey.Version);
            writer.WriteInt32(componentPayloads.Count);
            foreach (var pair in componentPayloads.OrderBy(pair => pair.Key))
            {
                writer.WriteUInt32(pair.Key);
                var componentType = pair.Key.GetRegisteredType();
                writer.WriteString(RuntimeComponentTypeRegistry.GetKey(componentType));
                writer.WriteBytes(pair.Value);
            }

            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(writer.ToArray()));
        }

        private static void ValidateResolvedAsset(GameAssetReference request, GameAsset asset)
        {
            if (!asset.GUID.isValid)
                throw new InvalidOperationException($"Resolved GameAsset '{asset.name}' has no valid GUID.");
            if (string.IsNullOrWhiteSpace(asset.Key.Version))
                throw new InvalidOperationException($"Resolved GameAsset '{asset.name}' does not have an exact version.");

            var requested = request.RequestedKey;
            if (!SameIdentity(requested, asset.Key))
                throw new InvalidOperationException($"Requested asset identity '{requested}' resolved to unrelated asset '{asset.Key}'.");
            if (!string.IsNullOrWhiteSpace(requested.Version) && !string.Equals(requested.Version, asset.Key.Version, StringComparison.Ordinal))
                throw new InvalidOperationException($"Exact asset request '{requested}' resolved to version '{asset.Key.Version}'.");
        }

        private static bool SameIdentity(GameAssetKey left, GameAssetKey right)
        {
            return string.Equals(left.Mod, right.Mod, StringComparison.Ordinal)
                   && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
                   && string.Equals(left.Key, right.Key, StringComparison.Ordinal);
        }

        private static bool KeysEqual(GameAssetKey left, GameAssetKey right)
        {
            return SameIdentity(left, right) && string.Equals(left.Version, right.Version, StringComparison.Ordinal);
        }

        private static string ToHex(byte[] hash)
        {
            var builder = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++)
                builder.Append(hash[i].ToString("x2"));
            return builder.ToString();
        }
    }
}
