using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using Unity.Entities;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public delegate RuntimeProtocolV2Context RuntimeProtocolV2ContextFactory(StoreRealm realm);

    public delegate bool RuntimeCommandEnvelopeEncoder(
        GameRuntimeCommand command,
        in RuntimeExecutionState authority,
        ulong clientSequence,
        out RuntimeCommandEnvelope envelope);

    public delegate bool RuntimeObjectVisibility(
        int connectionId,
        RuntimeStore store,
        long objectId);

    /// <summary>
    /// Immutable game-supplied composition boundary for protocol v2. The
    /// transport never reflects over game assemblies and never creates a store
    /// through GetOrAdd; every store is resolved by the exact immutable
    /// manifest generation.
    /// </summary>
    public class RuntimeProtocolV2Context
    {
        public readonly RuntimeSessionManifestTemplate ManifestTemplate;
        public readonly RuntimeSessionClientExpectation ClientExpectation;
        public readonly RuntimeSessionAssetCatalog AssetCatalog;
        public readonly GameAssetLibraryLock AssetLock;
        public readonly GameAssetTemplateCache TemplateCache;
        public readonly RuntimePatchCodecRegistry PatchCodecs;
        public readonly RuntimeReplicationPolicyRegistry ReplicationPolicies;
        public readonly RuntimeStateStreamProfileRegistry StateStreamProfiles;
        public readonly World World;
        public readonly RuntimeCommandsBus CommandsBus;
        public readonly RuntimeCommandRegistry CommandRegistry;
        public readonly RuntimeCommandEnvelopeEncoder CommandEncoder;
        public readonly RuntimeObjectVisibility IsObjectVisible;

        public RuntimeProtocolV2Context(
            RuntimeSessionManifestTemplate manifestTemplate,
            RuntimeSessionAssetCatalog assetCatalog,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            RuntimePatchCodecRegistry patchCodecs,
            RuntimeReplicationPolicyRegistry replicationPolicies,
            World world,
            RuntimeStateStreamProfileRegistry stateStreamProfiles,
            RuntimeCommandsBus commandsBus = null,
            RuntimeCommandRegistry commandRegistry = null,
            RuntimeCommandEnvelopeEncoder commandEncoder = null,
            RuntimeObjectVisibility isObjectVisible = null)
            : this(
                manifestTemplate,
                RuntimeSessionClientExpectation.FromServerTemplate(manifestTemplate),
                assetCatalog,
                assetLock,
                templateCache,
                patchCodecs,
                replicationPolicies,
                world,
                stateStreamProfiles,
                commandsBus,
                commandRegistry,
                commandEncoder,
                isObjectVisible) { }

        public RuntimeProtocolV2Context(
            RuntimeSessionClientExpectation clientExpectation,
            RuntimeSessionAssetCatalog assetCatalog,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            RuntimePatchCodecRegistry patchCodecs,
            RuntimeReplicationPolicyRegistry replicationPolicies,
            World world,
            RuntimeStateStreamProfileRegistry stateStreamProfiles,
            RuntimeCommandsBus commandsBus = null,
            RuntimeCommandRegistry commandRegistry = null,
            RuntimeCommandEnvelopeEncoder commandEncoder = null,
            RuntimeObjectVisibility isObjectVisible = null)
            : this(
                null,
                clientExpectation,
                assetCatalog,
                assetLock,
                templateCache,
                patchCodecs,
                replicationPolicies,
                world,
                stateStreamProfiles,
                commandsBus,
                commandRegistry,
                commandEncoder,
                isObjectVisible) { }

        private RuntimeProtocolV2Context(
            RuntimeSessionManifestTemplate manifestTemplate,
            RuntimeSessionClientExpectation clientExpectation,
            RuntimeSessionAssetCatalog assetCatalog,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            RuntimePatchCodecRegistry patchCodecs,
            RuntimeReplicationPolicyRegistry replicationPolicies,
            World world,
            RuntimeStateStreamProfileRegistry stateStreamProfiles,
            RuntimeCommandsBus commandsBus,
            RuntimeCommandRegistry commandRegistry,
            RuntimeCommandEnvelopeEncoder commandEncoder,
            RuntimeObjectVisibility isObjectVisible)
        {
            if (manifestTemplate == null && clientExpectation == null)
                throw new ArgumentException("Protocol v2 context requires a server manifest or client expectation.");
            ManifestTemplate = manifestTemplate;
            ClientExpectation = clientExpectation ?? throw new ArgumentNullException(nameof(clientExpectation));
            AssetCatalog = assetCatalog ?? throw new ArgumentNullException(nameof(assetCatalog));
            AssetLock = assetLock ?? throw new ArgumentNullException(nameof(assetLock));
            TemplateCache = templateCache ?? throw new ArgumentNullException(nameof(templateCache));
            PatchCodecs = patchCodecs ?? throw new ArgumentNullException(nameof(patchCodecs));
            ReplicationPolicies = replicationPolicies ?? throw new ArgumentNullException(nameof(replicationPolicies));
            if (!replicationPolicies.IsSealed)
                throw new InvalidOperationException("Protocol v2 requires a sealed replication policy registry.");
            if (world == null || !world.IsCreated)
                throw new ArgumentException("Protocol v2 requires a valid ECS World.", nameof(world));
            if (!string.Equals(patchCodecs.SchemaHash, templateCache.CodecRegistry.SchemaHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Protocol v2 patch registry does not match the GameAsset template cache schema.");

            StateStreamProfiles = stateStreamProfiles ?? throw new ArgumentNullException(nameof(stateStreamProfiles));
            if (!StateStreamProfiles.IsSealed)
                throw new InvalidOperationException("Protocol v2 requires a sealed state stream profile registry.");
            var descriptor = manifestTemplate != null
                ? manifestTemplate.Descriptor
                : clientExpectation.Descriptor;
            if (!string.Equals(descriptor.StateStreamCatalogHash, StateStreamProfiles.CatalogHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Protocol v2 descriptor does not match the sealed state stream catalog.");

            World = world;
            CommandsBus = commandsBus;
            CommandRegistry = commandRegistry;
            CommandEncoder = commandEncoder;
            IsObjectVisible = isObjectVisible ?? AlwaysVisible;
            ValidateAssetCatalog();
        }

        public bool TryGetAuthoritativeStore(in NetStoreRef storeReference, out RuntimeStore store)
        {
            return RuntimeStores.TryGetRuntimeStore(
                storeReference.StoreId,
                storeReference.StoreGeneration,
                StoreRealm.Server,
                out store);
        }

        public RuntimeStore GetRequiredAuthoritativeStore(in NetStoreRef storeReference)
        {
            if (TryGetAuthoritativeStore(storeReference, out var store))
                return store;
            throw new InvalidOperationException(
                $"Protocol-v2 manifest store '{storeReference}' is not registered in the authoritative realm.");
        }

        public IReadOnlyList<NetStoreRef> GetManifestStores()
        {
            if (ManifestTemplate == null)
                throw new InvalidOperationException("Client-only protocol-v2 context has no authoritative store generations before manifest acceptance.");
            var entries = ManifestTemplate.Stores;
            var result = new NetStoreRef[entries.Count];
            for (var i = 0; i < entries.Count; i++)
            {
                result[i] = new NetStoreRef(entries[i].StoreId, entries[i].StoreGeneration);
            }

            return Array.AsReadOnly(result);
        }

        private void ValidateAssetCatalog()
        {
            var manifestAssets = ManifestTemplate != null
                ? ManifestTemplate.Assets
                : ClientExpectation.Assets;
            var catalogAssets = AssetCatalog.ManifestEntries;
            if (manifestAssets.Count != catalogAssets.Count)
                throw new InvalidOperationException("Protocol-v2 manifest and immutable GameAsset catalog have different sizes.");

            for (var i = 0; i < manifestAssets.Count; i++)
            {
                var manifest = manifestAssets[i];
                var catalog = catalogAssets[i];
                if (manifest.AssetNetId != catalog.AssetNetId
                    || !string.Equals(manifest.ExactKey, catalog.ExactKey, StringComparison.Ordinal)
                    || !string.Equals(manifest.AssetGuid, catalog.AssetGuid, StringComparison.Ordinal)
                    || !string.Equals(manifest.MaterializedContentHash, catalog.MaterializedContentHash, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Protocol-v2 manifest GameAsset entry {manifest.AssetNetId} does not match the immutable session catalog.");
                }
            }
        }

        private static bool AlwaysVisible(int connectionId, RuntimeStore store, long objectId)
        {
            return true;
        }
    }
}
