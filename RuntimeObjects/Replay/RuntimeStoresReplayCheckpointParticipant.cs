using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;
using Hash128 = UnityEngine.Hash128;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public class RuntimeStoresReplayObjectSnapshot
    {
        public long ObjectId;
        public Hash128 InstanceGuid;
        public long ParentObjectId;
        public int SiblingIndex;
        public GameAssetKey ExactAssetKey;
        public Hash128 AssetGuid;
        public string MaterializedContentHash;
        public byte[] PatchPayload;
    }

    public class RuntimeStoresReplayStoreSnapshot
    {
        public FixedString32Bytes StoreId;
        public StoreRealm Realm;
        public StoreNetDir NetDirection;
        public ulong CapturedRevision;
        public readonly List<RuntimeStoresReplayObjectSnapshot> Objects = new();
    }

    public class RuntimeStoresReplaySnapshot
    {
        public readonly List<RuntimeStoresReplayStoreSnapshot> Stores = new();
    }

    public readonly struct RuntimeReplayStableObjectKey :
        IEquatable<RuntimeReplayStableObjectKey>
    {
        public readonly FixedString32Bytes StoreId;
        public readonly Hash128 InstanceGuid;

        public RuntimeReplayStableObjectKey(
            FixedString32Bytes storeId,
            Hash128 instanceGuid)
        {
            StoreId = storeId;
            InstanceGuid = instanceGuid;
        }

        public bool Equals(RuntimeReplayStableObjectKey other)
        {
            return StoreId.Equals(other.StoreId)
                   && InstanceGuid == other.InstanceGuid;
        }

        public override bool Equals(object obj)
        {
            return obj is RuntimeReplayStableObjectKey other
                   && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(StoreId, InstanceGuid);
        }
    }

    public class RuntimeStoresReplayCheckpointParticipant :
        IRuntimeReplayCheckpointParticipant,
        IRuntimeReplayCheckpointPrevalidator,
        IRuntimeReplayCheckpointValidationContextContributor
    {
        public const uint SECTION_ID = 0x00010000u;
        public const uint SECTION_VERSION = 1u;
        public const int MAX_STORES = 1024;
        public const int MAX_OBJECTS_PER_STORE = 1_000_000;
        public const int MAX_PATCH_BYTES = 64 * 1024 * 1024;

        private readonly World _world;
        private readonly StoreRealm _realm;
        private readonly GameAssetLibraryLock _assetLock;
        private readonly GameAssetTemplateCache _templates;
        private readonly RuntimeReplayStoreScope _storeScope;
        private readonly RuntimeObjectPatchBinaryCodec _patchCodec = new();
        private readonly RuntimeReplayReferenceClosureBinding
            _referenceClosureBinding;

        public uint SectionId => SECTION_ID;
        public uint CurrentVersion => SECTION_VERSION;

        public RuntimeStoresReplayCheckpointParticipant(
            World world,
            StoreRealm realm,
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templates,
            RuntimeReplayStoreScope storeScope,
            RuntimeReplayReferenceClosureBinding referenceClosureBinding =
                null)
        {
            _world = world != null && world.IsCreated
                ? world
                : throw new ArgumentException(
                    "Runtime-store replay checkpoints require a created ECS world.",
                    nameof(world));
            _realm = realm;
            _assetLock = assetLock
                         ?? throw new ArgumentNullException(nameof(assetLock));
            _templates = templates
                         ?? throw new ArgumentNullException(nameof(templates));
            _storeScope = storeScope
                          ?? throw new ArgumentNullException(
                              nameof(storeScope));
            _referenceClosureBinding = referenceClosureBinding;
        }

        public void Capture(RuntimeReplayCheckpointWriter writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            FlushScopedStoresToQuiescence();
            WriteSnapshot(writer, CaptureSnapshot(), includeRevision: true);
        }

        public void Prevalidate(RuntimeReplayCheckpointReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var snapshot = ReadSnapshot(reader);
            ValidateSnapshot(snapshot);
            ValidateScope(snapshot);
        }

        public void ContributeValidationContext(
            RuntimeReplayCheckpointReader reader,
            RuntimeReplayCheckpointValidationContext context)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            var snapshot = ReadSnapshot(reader);
            ValidateScope(snapshot);
            if (_referenceClosureBinding == null)
            {
                return;
            }
            _referenceClosureBinding.Publish(
                context,
                BuildReferenceClosure(snapshot));
        }

        public void Restore(RuntimeReplayCheckpointReader reader)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            var snapshot = ReadSnapshot(reader);
            ValidateSnapshot(snapshot);
            ValidateScope(snapshot);
            RestoreSnapshot(snapshot);
        }

        public void AppendFingerprint(RuntimeReplayCheckpointWriter writer)
        {
            if (writer == null)
            {
                throw new ArgumentNullException(nameof(writer));
            }

            FlushScopedStoresToQuiescence();
            WriteSnapshot(writer, CaptureSnapshot(), includeRevision: false);
        }

        private void FlushScopedStoresToQuiescence()
        {
            for (var i = 0; i < _storeScope.Count; i++)
            {
                var storeId = _storeScope.TakeStoreId(i);
                if (!RuntimeStores.TryGetRuntimeStore(
                        storeId,
                        _realm,
                        out var store)
                    || store == null
                    || store.Retired)
                {
                    throw new InvalidOperationException(
                        $"Replay RuntimeStore scope requires active store "
                        + $"'{storeId}' in realm {_realm}.");
                }
                store.FlushToQuiescence();
            }
        }

        private void ValidateScope(RuntimeStoresReplaySnapshot snapshot)
        {
            if (snapshot.Stores.Count != _storeScope.Count)
            {
                throw new InvalidOperationException(
                    $"Replay checkpoint contains {snapshot.Stores.Count} "
                    + $"RuntimeStores, but the selected scope requires "
                    + $"{_storeScope.Count}.");
            }

            for (var i = 0; i < snapshot.Stores.Count; i++)
            {
                var storeId = snapshot.Stores[i].StoreId;
                if (!_storeScope.Contains(storeId))
                {
                    throw new InvalidOperationException(
                        $"Replay checkpoint store '{storeId}' is outside "
                        + "the selected RuntimeStore scope.");
                }
            }
        }

        private RuntimeStoresReplaySnapshot CaptureSnapshot()
        {
            var snapshot = new RuntimeStoresReplaySnapshot();
            var stores = new RuntimeStore[_storeScope.Count];
            for (var i = 0; i < stores.Length; i++)
            {
                var storeId = _storeScope.TakeStoreId(i);
                if (!RuntimeStores.TryGetRuntimeStore(
                        storeId,
                        _realm,
                        out var store)
                    || store == null
                    || store.Retired)
                {
                    throw new InvalidOperationException(
                        $"Replay RuntimeStore scope requires active store "
                        + $"'{storeId}' in realm {_realm}.");
                }
                stores[i] = store;
            }
            if (stores.Length > MAX_STORES)
            {
                throw new InvalidOperationException(
                    $"Replay checkpoint has {stores.Length} stores; maximum is {MAX_STORES}.");
            }

            var persistentContext =
                RuntimePersistentPatchCodecContext.ForActiveRealm(_realm);
            for (var i = 0; i < stores.Length; i++)
            {
                var store = stores[i];
                var storeSnapshot = new RuntimeStoresReplayStoreSnapshot
                {
                    StoreId = store.Id,
                    Realm = store.Realm,
                    NetDirection = RuntimeStores.GetNetDir(store.Id),
                    CapturedRevision = store.StoreRevision,
                };
                CaptureStoreObjects(
                    store,
                    storeSnapshot,
                    persistentContext);
                snapshot.Stores.Add(storeSnapshot);
            }

            return snapshot;
        }

        private void CaptureStoreObjects(
            RuntimeStore store,
            RuntimeStoresReplayStoreSnapshot target,
            RuntimePersistentPatchCodecContext persistentContext)
        {
            var roots = store.Parents.V.Values
                .Where(runtimeObject => runtimeObject != null)
                .OrderBy(runtimeObject => runtimeObject.InstanceId)
                .ToArray();
            for (var i = 0; i < roots.Length; i++)
            {
                CaptureObjectSubtree(
                    store,
                    roots[i],
                    RuntimeStore.STORE_ROOT_OBJECT_ID,
                    i,
                    target,
                    persistentContext);
            }

            if (target.Objects.Count != store.All.V.Count)
            {
                throw new InvalidOperationException(
                    $"RuntimeStore '{store.Id}' checkpoint traversed {target.Objects.Count} objects, "
                    + $"but the store exposes {store.All.V.Count}.");
            }
        }

        private void CaptureObjectSubtree(
            RuntimeStore store,
            GameRuntimeObject runtimeObject,
            long parentObjectId,
            int siblingIndex,
            RuntimeStoresReplayStoreSnapshot target,
            RuntimePersistentPatchCodecContext persistentContext)
        {
            if (target.Objects.Count >= MAX_OBJECTS_PER_STORE)
            {
                throw new InvalidOperationException(
                    $"RuntimeStore '{store.Id}' exceeds the replay limit of "
                    + $"{MAX_OBJECTS_PER_STORE} objects.");
            }

            var origin = runtimeObject.Origin;
            var patch = _templates.BuildOverrides(
                runtimeObject,
                _assetLock,
                persistentContext);
            target.Objects.Add(new RuntimeStoresReplayObjectSnapshot
            {
                ObjectId = runtimeObject.InstanceId,
                InstanceGuid = origin.InstanceGuid,
                ParentObjectId = parentObjectId,
                SiblingIndex = siblingIndex,
                ExactAssetKey = origin.Asset.ExactKey,
                AssetGuid = origin.Asset.AssetGuid,
                MaterializedContentHash =
                    origin.Asset.MaterializedContentHash,
                PatchPayload = _patchCodec.Encode(patch),
            });

            if (!store.TryTakeChildren(
                    runtimeObject.InstanceId,
                    out var children))
            {
                return;
            }

            for (var i = 0; i < children.Count; i++)
            {
                var childId = children[i];
                if (!store.TryTakeRO(childId, out var child)
                    || child == null)
                {
                    throw new InvalidOperationException(
                        $"RuntimeStore '{store.Id}' hierarchy references missing child {childId}.");
                }

                CaptureObjectSubtree(
                    store,
                    child,
                    runtimeObject.InstanceId,
                    i,
                    target,
                    persistentContext);
            }
        }

        private void ValidateSnapshot(RuntimeStoresReplaySnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (snapshot.Stores.Count > MAX_STORES)
            {
                throw new InvalidOperationException(
                    $"Replay checkpoint has {snapshot.Stores.Count} stores; maximum is {MAX_STORES}.");
            }

            var storeIds = new HashSet<FixedString32Bytes>();
            var globalGuids = new HashSet<Hash128>();
            for (var storeIndex = 0;
                 storeIndex < snapshot.Stores.Count;
                 storeIndex++)
            {
                var store = snapshot.Stores[storeIndex]
                            ?? throw new InvalidOperationException(
                                $"Replay checkpoint store {storeIndex} is null.");
                if (store.StoreId.Length == 0
                    || !storeIds.Add(store.StoreId))
                {
                    throw new InvalidOperationException(
                        $"Replay checkpoint contains invalid or duplicate store '{store.StoreId}'.");
                }
                if (store.Realm != _realm)
                {
                    throw new InvalidOperationException(
                        $"Replay store '{store.StoreId}' belongs to realm {store.Realm}, expected {_realm}.");
                }
                if (store.NetDirection != StoreNetDir.None
                    && store.NetDirection != StoreNetDir.S2C
                    && store.NetDirection != StoreNetDir.C2S)
                {
                    throw new InvalidOperationException(
                        $"Replay store '{store.StoreId}' has invalid network direction {store.NetDirection}.");
                }
                if (store.Objects.Count > MAX_OBJECTS_PER_STORE)
                {
                    throw new InvalidOperationException(
                        $"Replay store '{store.StoreId}' has {store.Objects.Count} objects; "
                        + $"maximum is {MAX_OBJECTS_PER_STORE}.");
                }

                ValidateStoreObjects(store, globalGuids);
            }
        }

        private void ValidateStoreObjects(
            RuntimeStoresReplayStoreSnapshot store,
            HashSet<Hash128> globalGuids)
        {
            var objectIds = new HashSet<long>();
            var siblingCounts = new Dictionary<long, int>();
            for (var i = 0; i < store.Objects.Count; i++)
            {
                var runtimeObject = store.Objects[i]
                                    ?? throw new InvalidOperationException(
                                        $"Replay store '{store.StoreId}' object {i} is null.");
                if (runtimeObject.ObjectId < RuntimeStore.FIRST_USER_OBJECT_ID
                    || !objectIds.Add(runtimeObject.ObjectId))
                {
                    throw new InvalidOperationException(
                        $"Replay store '{store.StoreId}' contains invalid or duplicate object id {runtimeObject.ObjectId}.");
                }
                if (!runtimeObject.InstanceGuid.isValid
                    || !globalGuids.Add(runtimeObject.InstanceGuid))
                {
                    throw new InvalidOperationException(
                        $"Replay checkpoint contains invalid or duplicate instance GUID '{runtimeObject.InstanceGuid}'.");
                }
                if (runtimeObject.ParentObjectId
                    != RuntimeStore.STORE_ROOT_OBJECT_ID
                    && !objectIds.Contains(runtimeObject.ParentObjectId))
                {
                    throw new InvalidOperationException(
                        $"Replay object '{store.StoreId}/{runtimeObject.ObjectId}' "
                        + $"references parent {runtimeObject.ParentObjectId} that was not declared first.");
                }

                var expectedSiblingIndex =
                    siblingCounts.GetValueOrDefault(
                        runtimeObject.ParentObjectId);
                if (runtimeObject.SiblingIndex != expectedSiblingIndex)
                {
                    throw new InvalidOperationException(
                        $"Replay object '{store.StoreId}/{runtimeObject.ObjectId}' has sibling index "
                        + $"{runtimeObject.SiblingIndex}; expected {expectedSiblingIndex}.");
                }
                siblingCounts[runtimeObject.ParentObjectId] =
                    expectedSiblingIndex + 1;
                ValidateObjectAsset(runtimeObject);
            }
        }

        private void ValidateObjectAsset(
            RuntimeStoresReplayObjectSnapshot runtimeObject)
        {
            if (!runtimeObject.AssetGuid.isValid
                || string.IsNullOrWhiteSpace(
                    runtimeObject.MaterializedContentHash)
                || runtimeObject.PatchPayload == null
                || runtimeObject.PatchPayload.Length > MAX_PATCH_BYTES)
            {
                throw new InvalidOperationException(
                    $"Replay object {runtimeObject.ObjectId} has an invalid exact GA identity or patch.");
            }

            var blueprint = _templates.ResolveStrict(
                new GameAssetReference(runtimeObject.ExactAssetKey),
                _assetLock);
            if (blueprint.Asset.AssetGuid != runtimeObject.AssetGuid
                || blueprint.Asset.ExactKey
                != runtimeObject.ExactAssetKey
                || !string.Equals(
                    blueprint.Asset.MaterializedContentHash,
                    runtimeObject.MaterializedContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Replay object {runtimeObject.ObjectId} exact GameAsset "
                    + $"'{runtimeObject.ExactAssetKey}' no longer matches the installed dependency.");
            }

            var patch = _patchCodec.Decode(runtimeObject.PatchPayload);
            if (!string.Equals(
                    patch.SchemaHash,
                    _templates.CodecRegistry.SchemaHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Replay object {runtimeObject.ObjectId} patch schema '{patch.SchemaHash}' "
                    + $"does not match runtime schema '{_templates.CodecRegistry.SchemaHash}'.");
            }
        }

        private RuntimeReplayObjectReferenceClosure BuildReferenceClosure(
            RuntimeStoresReplaySnapshot snapshot)
        {
            var stableInstances =
                new Dictionary<
                    RuntimeReplayStableObjectKey,
                    RuntimeInstance>();
            var referencesByRuntimeId =
                new Dictionary<
                    (FixedString32Bytes StoreId, long ObjectId),
                    RuntimePatchObjectReference>();

            for (var storeIndex = 0;
                 storeIndex < snapshot.Stores.Count;
                 storeIndex++)
            {
                var store = snapshot.Stores[storeIndex];
                for (var objectIndex = 0;
                     objectIndex < store.Objects.Count;
                     objectIndex++)
                {
                    var runtimeObject = store.Objects[objectIndex];
                    var key = new RuntimeReplayStableObjectKey(
                        store.StoreId,
                        runtimeObject.InstanceGuid);
                    var runtimeInstance = new RuntimeInstance
                    {
                        StoreId = store.StoreId,
                        Id = runtimeObject.ObjectId,
                        Epoch = 1u,
                    };
                    stableInstances.Add(key, runtimeInstance);
                    referencesByRuntimeId.Add(
                        (store.StoreId, runtimeObject.ObjectId),
                        new RuntimePatchObjectReference(
                            store.StoreId,
                            runtimeObject.InstanceGuid));
                }
            }

            var patchContext = new RuntimePersistentPatchCodecContext(
                value =>
                {
                    if (!referencesByRuntimeId.TryGetValue(
                            (value.StoreId, value.Id),
                            out var reference))
                    {
                        throw new InvalidOperationException(
                            $"Replay materialization reference "
                            + $"'{value.StoreId}/{value.Id}' is outside the "
                            + "validated RuntimeStore closure.");
                    }

                    return reference;
                },
                reference =>
                {
                    var key = new RuntimeReplayStableObjectKey(
                        reference.StoreId,
                        reference.ObjectGuid);
                    if (!stableInstances.TryGetValue(
                            key,
                            out var runtimeInstance))
                    {
                        throw new InvalidOperationException(
                            $"Replay persistent reference "
                            + $"'{reference.StoreId}/{reference.ObjectGuid}' "
                            + "is outside the validated RuntimeStore closure.");
                    }

                    return runtimeInstance;
                });

            var entries =
                new List<RuntimeReplayObjectReferenceClosureEntry>(
                    stableInstances.Count);
            for (var storeIndex = 0;
                 storeIndex < snapshot.Stores.Count;
                 storeIndex++)
            {
                var store = snapshot.Stores[storeIndex];
                for (var objectIndex = 0;
                     objectIndex < store.Objects.Count;
                     objectIndex++)
                {
                    var source = store.Objects[objectIndex];
                    var materialized = _templates.Materialize(
                        new GameAssetInstance(
                            source.InstanceGuid,
                            new GameAssetReference(
                                source.ExactAssetKey),
                            _patchCodec.Decode(source.PatchPayload)),
                        _assetLock,
                        patchContext);
                    var componentTypeIds = new uint[
                        materialized.Components.Count];
                    for (var componentIndex = 0;
                         componentIndex < materialized.Components.Count;
                         componentIndex++)
                    {
                        var component =
                            materialized.Components[componentIndex]
                            ?? throw new InvalidOperationException(
                                $"Replay object "
                                + $"'{store.StoreId}/{source.InstanceGuid}' "
                                + "materialized a null runtime component.");
                        if (!RuntimeComponentTypeRegistry.TryGetId(
                                component.GetType(),
                                out componentTypeIds[componentIndex]))
                        {
                            throw new InvalidOperationException(
                                $"Replay object "
                                + $"'{store.StoreId}/{source.InstanceGuid}' "
                                + $"materialized unregistered runtime component "
                                + $"'{component.GetType().FullName}'.");
                        }
                    }

                    entries.Add(
                        new RuntimeReplayObjectReferenceClosureEntry(
                            new RuntimeReplayObjectRef(
                                store.StoreId,
                                source.InstanceGuid),
                            componentTypeIds));
                }
            }

            return new RuntimeReplayObjectReferenceClosure(entries);
        }

        private void RestoreSnapshot(RuntimeStoresReplaySnapshot snapshot)
        {
            if (snapshot.Stores.Count == 0)
            {
                throw new InvalidOperationException(
                    "Runtime-store replay checkpoint cannot publish an empty authoritative store group.");
            }

            var stagedStores = new List<RuntimeStore>(
                snapshot.Stores.Count);
            var stagedById =
                new Dictionary<FixedString32Bytes, RuntimeStore>();
            var stableInstances =
                new Dictionary<RuntimeReplayStableObjectKey, RuntimeInstance>();
            var directions =
                new Dictionary<FixedString32Bytes, StoreNetDir>();
            var published = false;
            try
            {
                PrepareStagedStores(
                    snapshot,
                    stagedStores,
                    stagedById,
                    stableInstances,
                    directions);
                var patchContext = CreateStagedPatchContext(
                    stagedById,
                    stableInstances);
                MaterializeStagedStores(
                    snapshot,
                    stagedById,
                    patchContext);
                ProjectStagedStores(snapshot, stagedById);
                for (var i = 0; i < snapshot.Stores.Count; i++)
                {
                    var source = snapshot.Stores[i];
                    var staged = stagedById[source.StoreId];
                    staged.FlushToQuiescence();
                }
                PlaybackProjectionCommands();
                for (var i = 0; i < snapshot.Stores.Count; i++)
                {
                    var source = snapshot.Stores[i];
                    var staged = stagedById[source.StoreId];
                    RuntimeStores.FinalizeRestoredSnapshot(
                        staged,
                        source.CapturedRevision);
                }

                RuntimeStores.PublishPreparedRestoreStores(
                    stagedStores,
                    directions,
                    replaceRealm: false);
                published = true;
                PlaybackProjectionCommands();
            }
            catch
            {
                if (!published)
                {
                    RetireStagedStores(stagedStores);
                }
                throw;
            }
        }

        private void PrepareStagedStores(
            RuntimeStoresReplaySnapshot snapshot,
            List<RuntimeStore> stagedStores,
            Dictionary<FixedString32Bytes, RuntimeStore> stagedById,
            Dictionary<RuntimeReplayStableObjectKey, RuntimeInstance>
                stableInstances,
            Dictionary<FixedString32Bytes, StoreNetDir> directions)
        {
            for (var storeIndex = 0;
                 storeIndex < snapshot.Stores.Count;
                 storeIndex++)
            {
                var source = snapshot.Stores[storeIndex];
                var staged = RuntimeStores.PrepareRestoreStore(
                    source.StoreId,
                    source.Realm);
                stagedStores.Add(staged);
                stagedById.Add(source.StoreId, staged);
                directions.Add(source.StoreId, source.NetDirection);
                for (var objectIndex = 0;
                     objectIndex < source.Objects.Count;
                     objectIndex++)
                {
                    var sourceObject = source.Objects[objectIndex];
                    stableInstances.Add(
                        new RuntimeReplayStableObjectKey(
                            source.StoreId,
                            sourceObject.InstanceGuid),
                        new RuntimeInstance
                        {
                            StoreId = source.StoreId,
                            Id = sourceObject.ObjectId,
                            Epoch = staged.Epoch,
                        });
                }
            }
        }

        private RuntimePersistentPatchCodecContext CreateStagedPatchContext(
            IReadOnlyDictionary<FixedString32Bytes, RuntimeStore> stagedById,
            IReadOnlyDictionary<
                RuntimeReplayStableObjectKey,
                RuntimeInstance> stableInstances)
        {
            return new RuntimePersistentPatchCodecContext(
                value =>
                {
                    if (!stagedById.TryGetValue(
                            value.StoreId,
                            out var store)
                        || value.Epoch != store.Epoch
                        || !store.TryTakeRO(
                            value.Id,
                            out var runtimeObject)
                        || runtimeObject == null)
                    {
                        throw new InvalidOperationException(
                            $"Staged runtime reference '{value.StoreId}/{value.Id}' epoch {value.Epoch} is not materialized.");
                    }

                    return new RuntimePatchObjectReference(
                        value.StoreId,
                        runtimeObject.GUID);
                },
                reference =>
                {
                    var key = new RuntimeReplayStableObjectKey(
                        reference.StoreId,
                        reference.ObjectGuid);
                    if (!stableInstances.TryGetValue(
                            key,
                            out var runtimeInstance))
                    {
                        throw new InvalidOperationException(
                            $"Replay persistent reference '{reference.StoreId}/{reference.ObjectGuid}' is outside the restored store closure.");
                    }

                    return runtimeInstance;
                });
        }

        private void MaterializeStagedStores(
            RuntimeStoresReplaySnapshot snapshot,
            IReadOnlyDictionary<FixedString32Bytes, RuntimeStore> stagedById,
            RuntimePersistentPatchCodecContext patchContext)
        {
            for (var storeIndex = 0;
                 storeIndex < snapshot.Stores.Count;
                 storeIndex++)
            {
                var source = snapshot.Stores[storeIndex];
                var staged = stagedById[source.StoreId];
                for (var objectIndex = 0;
                     objectIndex < source.Objects.Count;
                     objectIndex++)
                {
                    var sourceObject = source.Objects[objectIndex];
                    staged.Spawn(
                        sourceObject.ObjectId,
                        new GameAssetInstance(
                            sourceObject.InstanceGuid,
                            new GameAssetReference(
                                sourceObject.ExactAssetKey),
                            _patchCodec.Decode(
                                sourceObject.PatchPayload)),
                        _assetLock,
                        _templates,
                        patchContext,
                        sourceObject.ParentObjectId
                        == RuntimeStore.STORE_ROOT_OBJECT_ID
                            ? null
                            : sourceObject.ParentObjectId,
                        sourceObject.SiblingIndex);
                }
            }
        }

        private static void ProjectStagedStores(
            RuntimeStoresReplaySnapshot snapshot,
            IReadOnlyDictionary<FixedString32Bytes, RuntimeStore> stagedById)
        {
            for (var storeIndex = 0;
                 storeIndex < snapshot.Stores.Count;
                 storeIndex++)
            {
                var source = snapshot.Stores[storeIndex];
                var staged = stagedById[source.StoreId];
                for (var objectIndex = 0;
                     objectIndex < source.Objects.Count;
                     objectIndex++)
                {
                    var sourceObject = source.Objects[objectIndex];
                    if (sourceObject.ParentObjectId
                        == RuntimeStore.STORE_ROOT_OBJECT_ID)
                    {
                        staged.CreateEntitySubtree(
                            sourceObject.ObjectId);
                    }
                }
            }
        }

        private void PlaybackProjectionCommands()
        {
            var playback = _world.GetExistingSystemManaged<
                EndSimulationEntityCommandBufferSystem>()
                           ?? throw new InvalidOperationException(
                               "Replay restore requires EndSimulationEntityCommandBufferSystem.");
            playback.Update();
        }

        private void RetireStagedStores(
            IReadOnlyList<RuntimeStore> stagedStores)
        {
            for (var i = 0; i < stagedStores.Count; i++)
            {
                stagedStores[i]?.Retire();
            }

            if (stagedStores.Count > 0)
            {
                PlaybackProjectionCommands();
            }
        }

        private static void WriteSnapshot(
            RuntimeReplayCheckpointWriter writer,
            RuntimeStoresReplaySnapshot snapshot,
            bool includeRevision)
        {
            writer.WriteInt32(snapshot.Stores.Count);
            for (var storeIndex = 0;
                 storeIndex < snapshot.Stores.Count;
                 storeIndex++)
            {
                var store = snapshot.Stores[storeIndex];
                writer.WriteString(store.StoreId.ToString());
                writer.WriteByte((byte)store.Realm);
                writer.WriteByte((byte)store.NetDirection);
                if (includeRevision)
                {
                    writer.WriteUInt64(store.CapturedRevision);
                }
                writer.WriteInt32(store.Objects.Count);
                for (var objectIndex = 0;
                     objectIndex < store.Objects.Count;
                     objectIndex++)
                {
                    var runtimeObject = store.Objects[objectIndex];
                    writer.WriteInt64(runtimeObject.ObjectId);
                    writer.WriteString(runtimeObject.InstanceGuid.ToString());
                    writer.WriteInt64(runtimeObject.ParentObjectId);
                    writer.WriteInt32(runtimeObject.SiblingIndex);
                    WriteGameAssetKey(
                        writer,
                        runtimeObject.ExactAssetKey);
                    writer.WriteString(runtimeObject.AssetGuid.ToString());
                    writer.WriteString(
                        runtimeObject.MaterializedContentHash);
                    writer.WriteBytes(runtimeObject.PatchPayload);
                }
            }
        }

        private static RuntimeStoresReplaySnapshot ReadSnapshot(
            RuntimeReplayCheckpointReader reader)
        {
            var snapshot = new RuntimeStoresReplaySnapshot();
            var storeCount = ReadCount(
                reader,
                MAX_STORES,
                "runtime store");
            for (var storeIndex = 0;
                 storeIndex < storeCount;
                 storeIndex++)
            {
                var storeIdText = reader.ReadString();
                if (string.IsNullOrWhiteSpace(storeIdText))
                {
                    throw new FormatException(
                        $"Replay runtime store {storeIndex} has no StoreId.");
                }

                var store = new RuntimeStoresReplayStoreSnapshot
                {
                    StoreId = new FixedString32Bytes(storeIdText),
                    Realm = (StoreRealm)reader.ReadByte(),
                    NetDirection = (StoreNetDir)reader.ReadByte(),
                    CapturedRevision = reader.ReadUInt64(),
                };
                var objectCount = ReadCount(
                    reader,
                    MAX_OBJECTS_PER_STORE,
                    "runtime object");
                for (var objectIndex = 0;
                     objectIndex < objectCount;
                     objectIndex++)
                {
                    store.Objects.Add(
                        new RuntimeStoresReplayObjectSnapshot
                        {
                            ObjectId = reader.ReadInt64(),
                            InstanceGuid = ReadHash128(
                                reader,
                                "instance GUID"),
                            ParentObjectId = reader.ReadInt64(),
                            SiblingIndex = reader.ReadInt32(),
                            ExactAssetKey = ReadGameAssetKey(reader),
                            AssetGuid = ReadHash128(
                                reader,
                                "asset GUID"),
                            MaterializedContentHash =
                                reader.ReadString(),
                            PatchPayload = reader.ReadBytes(
                                MAX_PATCH_BYTES),
                        });
                }
                snapshot.Stores.Add(store);
            }

            return snapshot;
        }

        private static int ReadCount(
            RuntimeReplayCheckpointReader reader,
            int maximum,
            string label)
        {
            var count = reader.ReadInt32();
            if (count < 0 || count > maximum)
            {
                throw new FormatException(
                    $"Replay {label} count {count} is outside 0..{maximum}.");
            }

            return count;
        }

        private static void WriteGameAssetKey(
            RuntimeReplayCheckpointWriter writer,
            in GameAssetKey key)
        {
            writer.WriteString(key.Mod);
            writer.WriteString(key.Type);
            writer.WriteString(key.Key);
            writer.WriteString(key.Version);
        }

        private static GameAssetKey ReadGameAssetKey(
            RuntimeReplayCheckpointReader reader)
        {
            return new GameAssetKey(
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString(),
                reader.ReadString());
        }

        private static Hash128 ReadHash128(
            RuntimeReplayCheckpointReader reader,
            string label)
        {
            var text = reader.ReadString();
            try
            {
                var value = Hash128.Parse(text);
                if (!value.isValid)
                {
                    throw new FormatException(
                        $"Replay {label} '{text}' is invalid.");
                }

                return value;
            }
            catch (Exception exception)
                when (exception is not FormatException)
            {
                throw new FormatException(
                    $"Replay {label} '{text}' is invalid.",
                    exception);
            }
        }
    }
}
