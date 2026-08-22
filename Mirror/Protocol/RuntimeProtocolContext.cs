using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.Mirror.Protocol
{
    public delegate RuntimeProtocolContext RuntimeProtocolContextFactory(StoreRealm realm);

    public delegate bool RuntimeCommandEnvelopeEncoder(
        GameRuntimeCommand command,
        in RuntimeExecutionState authority,
        ulong clientSequence,
        out RuntimeCommandEnvelope envelope);

    public delegate bool RuntimeObjectVisibility(
        int connectionId,
        RuntimeStore store,
        long objectId);

    public delegate RuntimeCheckpointBoundary?
        RuntimeCheckpointBoundaryProvider();
    public delegate RuntimeRecoveryCheckpoint
        RuntimeRecoveryCheckpointProvider();

    public interface IRuntimeCheckpointStageRestoreTransaction : IDisposable
    {
        void PrepareCommit();

        void Commit();
    }

    public delegate IRuntimeCheckpointStageRestoreTransaction
        RuntimeCheckpointStageRestore(
        World world,
        RuntimeReplayCheckpointEnvelope checkpoint,
        IReadOnlyList<RuntimeStore> stagedStores);
    public delegate void RuntimeJournalCatchupCompletion(World world);

    public class RuntimeProtocolContext
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
        public readonly RuntimeCheckpointBoundaryProvider CheckpointBoundaryProvider;
        public readonly RuntimeRecoveryCheckpointProvider RecoveryCheckpointProvider;
        public readonly RuntimeCheckpointStageRestore RestoreCheckpointStage;
        public readonly RuntimeCommandJournalScope JournalSubscriptionScope;
        public readonly RuntimeJournalCatchupCompletion CompleteJournalCatchup;

        public RuntimeProtocolContext(
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
            RuntimeObjectVisibility isObjectVisible = null,
            RuntimeCheckpointBoundaryProvider checkpointBoundaryProvider = null,
            RuntimeCommandJournalScope journalSubscriptionScope = null,
            RuntimeJournalCatchupCompletion completeJournalCatchup = null,
            RuntimeRecoveryCheckpointProvider recoveryCheckpointProvider = null,
            RuntimeCheckpointStageRestore restoreCheckpointStage = null)
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
                isObjectVisible,
                checkpointBoundaryProvider,
                journalSubscriptionScope,
                completeJournalCatchup,
                recoveryCheckpointProvider,
                restoreCheckpointStage) { }

        public RuntimeProtocolContext(
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
            RuntimeObjectVisibility isObjectVisible = null,
            RuntimeCheckpointBoundaryProvider checkpointBoundaryProvider = null,
            RuntimeCommandJournalScope journalSubscriptionScope = null,
            RuntimeJournalCatchupCompletion completeJournalCatchup = null,
            RuntimeRecoveryCheckpointProvider recoveryCheckpointProvider = null,
            RuntimeCheckpointStageRestore restoreCheckpointStage = null)
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
                isObjectVisible,
                checkpointBoundaryProvider,
                journalSubscriptionScope,
                completeJournalCatchup,
                recoveryCheckpointProvider,
                restoreCheckpointStage) { }

        private RuntimeProtocolContext(
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
            RuntimeObjectVisibility isObjectVisible,
            RuntimeCheckpointBoundaryProvider checkpointBoundaryProvider,
            RuntimeCommandJournalScope journalSubscriptionScope,
            RuntimeJournalCatchupCompletion completeJournalCatchup,
            RuntimeRecoveryCheckpointProvider recoveryCheckpointProvider,
            RuntimeCheckpointStageRestore restoreCheckpointStage)
        {
            if (manifestTemplate == null && clientExpectation == null)
                throw new ArgumentException("Protocol context requires a server manifest or client expectation.");
            ManifestTemplate = manifestTemplate;
            ClientExpectation = clientExpectation ?? throw new ArgumentNullException(nameof(clientExpectation));
            AssetCatalog = assetCatalog ?? throw new ArgumentNullException(nameof(assetCatalog));
            AssetLock = assetLock ?? throw new ArgumentNullException(nameof(assetLock));
            TemplateCache = templateCache ?? throw new ArgumentNullException(nameof(templateCache));
            PatchCodecs = patchCodecs ?? throw new ArgumentNullException(nameof(patchCodecs));
            ReplicationPolicies = replicationPolicies ?? throw new ArgumentNullException(nameof(replicationPolicies));
            if (!replicationPolicies.IsSealed)
                throw new InvalidOperationException("Protocol requires a sealed replication policy registry.");
            if (world == null || !world.IsCreated)
                throw new ArgumentException("Protocol requires a valid ECS World.", nameof(world));
            if (!string.Equals(patchCodecs.SchemaHash, templateCache.CodecRegistry.SchemaHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Protocol patch registry does not match the GameAsset template cache schema.");

            StateStreamProfiles = stateStreamProfiles ?? throw new ArgumentNullException(nameof(stateStreamProfiles));
            if (!StateStreamProfiles.IsSealed)
                throw new InvalidOperationException("Protocol requires a sealed state stream profile registry.");
            var descriptor = manifestTemplate != null
                ? manifestTemplate.Descriptor
                : clientExpectation.Descriptor;
            if (!string.Equals(descriptor.StateStreamCatalogHash, StateStreamProfiles.CatalogHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Protocol descriptor does not match the sealed state stream catalog.");

            World = world;
            CommandsBus = commandsBus;
            CommandRegistry = commandRegistry;
            CommandEncoder = commandEncoder;
            IsObjectVisible = isObjectVisible ?? AlwaysVisible;
            CheckpointBoundaryProvider = checkpointBoundaryProvider;
            RecoveryCheckpointProvider = recoveryCheckpointProvider;
            RestoreCheckpointStage = restoreCheckpointStage;
            if (CheckpointBoundaryProvider != null
                && RecoveryCheckpointProvider != null)
            {
                throw new InvalidOperationException(
                    "Protocol context must use one checkpoint provider. The recovery provider already includes its boundary.");
            }
            var hasCheckpointProvider =
                CheckpointBoundaryProvider != null
                || RecoveryCheckpointProvider != null;
            if (hasCheckpointProvider && CommandsBus == null)
            {
                throw new InvalidOperationException(
                    "Checkpoint journal transport requires a RuntimeCommandsBus.");
            }
            if (hasCheckpointProvider
                && journalSubscriptionScope == null)
            {
                throw new InvalidOperationException(
                    "Checkpoint provider requires a journal subscription scope.");
            }
            JournalSubscriptionScope = journalSubscriptionScope;
            CompleteJournalCatchup = completeJournalCatchup;
            if (ManifestTemplate != null
                && JournalSubscriptionScope != null
                && !hasCheckpointProvider)
            {
                throw new InvalidOperationException(
                    "A server journal subscription requires a checkpoint provider.");
            }
            if (ManifestTemplate != null
                && RestoreCheckpointStage != null)
            {
                throw new InvalidOperationException(
                    "Checkpoint staging restore is a client-only protocol hook.");
            }
            if (ManifestTemplate == null
                && RecoveryCheckpointProvider != null)
            {
                throw new InvalidOperationException(
                    "A recovery checkpoint provider is a server-only protocol hook.");
            }
            if (RestoreCheckpointStage != null
                && JournalSubscriptionScope == null)
            {
                throw new InvalidOperationException(
                    "Checkpoint staging restore requires a journal subscription scope.");
            }
            if (JournalSubscriptionScope != null)
            {
                if (CommandsBus == null)
                {
                    throw new InvalidOperationException(
                        "Journal subscription scope requires a RuntimeCommandsBus.");
                }
                ValidateJournalSubscriptionScope(JournalSubscriptionScope);
                if (ManifestTemplate == null
                    && CompleteJournalCatchup == null)
                {
                    throw new InvalidOperationException(
                        "A client journal subscription requires an explicit ECS playback/completion hook.");
                }
            }
            ValidateAssetCatalog();
        }

        public bool IsObjectVisibleForReliableProjection(
            int connectionId,
            RuntimeStore store,
            long objectId)
        {
            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }
            if (JournalSubscriptionScope != null
                && JournalSubscriptionScope.Contains(store.Id))
            {
                return true;
            }

            return IsObjectVisible(
                connectionId,
                store,
                objectId);
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
                $"Protocol manifest store '{storeReference}' is not registered in the authoritative realm.");
        }

        public IReadOnlyList<NetStoreRef> GetManifestStores()
        {
            if (ManifestTemplate == null)
                throw new InvalidOperationException("Client-only protocol context has no authoritative store generations before manifest acceptance.");
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
                throw new InvalidOperationException("Protocol manifest and immutable GameAsset catalog have different sizes.");

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
                        $"Protocol manifest GameAsset entry {manifest.AssetNetId} does not match the immutable session catalog.");
                }
            }
        }

        private void ValidateJournalSubscriptionScope(
            RuntimeCommandJournalScope scope)
        {
            if (scope.IsSessionWide)
            {
                if (ManifestTemplate != null)
                {
                    var manifestStoreIds =
                        new HashSet<FixedString32Bytes>();
                    for (var i = 0;
                         i < ManifestTemplate.Stores.Count;
                         i++)
                    {
                        manifestStoreIds.Add(
                            ManifestTemplate.Stores[i].StoreId);
                    }

                    var activeStoreCount = 0;
                    foreach (var activeStore in
                             RuntimeStores.EnumerateStores(
                                 StoreRealm.Server))
                    {
                        activeStoreCount++;
                        if (!manifestStoreIds.Contains(activeStore.Id))
                        {
                            throw new InvalidOperationException(
                                $"Session-wide journal scope requires active authoritative RuntimeStore '{activeStore.Id}' in the manifest baseline group.");
                        }
                    }

                    if (activeStoreCount != manifestStoreIds.Count)
                    {
                        throw new InvalidOperationException(
                            "Session-wide journal scope requires the manifest to exactly cover all active authoritative RuntimeStores.");
                    }
                }

                return;
            }

            var requiredStoreIds = ClientExpectation.StoreIds;
            for (var i = 0; i < scope.StoreIds.Count; i++)
            {
                var scopedStoreId =
                    scope.StoreIds[i].ToString();
                var found = false;
                for (var storeIndex = 0;
                     storeIndex < requiredStoreIds.Count;
                     storeIndex++)
                {
                    if (string.Equals(
                            requiredStoreIds[storeIndex],
                            scopedStoreId,
                            StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    throw new InvalidOperationException(
                        $"Journal subscription scope references store '{scopedStoreId}' outside the manifest.");
                }
            }
        }

        private static bool AlwaysVisible(int connectionId, RuntimeStore store, long objectId)
        {
            return true;
        }
    }
}
