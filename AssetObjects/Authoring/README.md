# Prefab → GameAsset authoring

## Native prefab definitions

`GameAssetAuthoring` defines an asset directly on a prefab asset root or variant. It contains manual GACs through `SerializeReference` and component/converter bindings. During Editor validation and preparation, unbound owned components receive the first available converter in ordinal full type-name order. Existing bindings and their configured values are retained. A binding with a source and `None` converter explicitly excludes that source, including after save, reload and variant inheritance; deleting the binding allows automatic setup again. Validation queues preparation on the main Editor thread and does not save assets itself. Automatic composition belongs to prefab definitions and Prefab Stage roots, never scene instances.

A converter reads an assigned component on the same GameObject or an owned descendant and returns GACs; it never creates a GRO or changes a Store. The nearest `GameAssetAuthoring` ancestor of a source must be its owner, including inactive objects. Sources outside that hierarchy or beneath another authoring are rejected, also for `None` bindings. The collector also rejects missing sources, incompatible converters, repeated sources and duplicate exact GAC types, and sorts successful output by SetupOrder and full type name. Its result includes the source of every GAC. Manual GACs remain available for definition data without a native component source; they are not implicit overrides of converter output. The effective-component Inspector preview is collapsed by default.

Converters may override `Prepare(Component, Func<UnityEngine.Object, string>)` to refresh generated resource metadata from their native source. The Editor calls preparation before collection and bake, and persists dirty generated values before Play/build. `Convert` remains runtime code and reads baked metadata without AssetDatabase. Preparation must invalidate unusable metadata before a failed resolver can leave an older identity behind. Real preparation exceptions are logged with binding context; rejected content uses the existing fallback policy.

`PrefabGameAssetIndex` is a derived `SingletonScriptableObject` containing direct root-authoring references, prefab GUIDs and dependency content hashes. A product supplies its Editor index path and inclusion filter; the SDK has no product paths. The Editor reconciles saved prefab and resource changes and runs a complete prebuild validation. Renaming or moving a prefab preserves its GUID, while copies and variants have their own definitions. Nested prefab instances retain their source definition rather than defining additional assets in their parent.

`PrefabGameAssetReference` is the native Unity reference to a prefab definition. Its drawer accepts prefab assets and variants and rejects scene objects. Explicit `PrefabGameAssetDefinitions.TryPrepare(...)` builds a caller-owned set of temporary `GameAsset` SOs, resolves native references into existing `GameAssetReference` keys, and retrieves prepared definitions. The automatic key is `_p_unity/prefab/<prefab GUID>/0.0.0`; the GA GUID is the prefab GUID. Preparation is atomic. Unity SO cloning separates serializable GAC data from the authoring source while retaining Unity resource references. Call `Dispose()` after all consumers stop; do not mutate prepared shared definitions. Changes to authoring do not update an already prepared set.

The Inspector previews only definition data, without invoking GameObject lifecycle, materializing a GRO or touching a Store. Prefab instances in scenes or parent prefabs may override values, but cannot change manual GAC types/counts, converter/source bindings or the final GAC type set. The Editor records their effective GAC snapshots in `PrefabGameAssetIndex.Overrides`, including Unity resource references. Hidden generated `SourceDefinitionGuid` and `RuntimeOverrideId` metadata links a built instance to its source and snapshot; authors do not enter these values. Saving a scene persists its generated metadata, and scene build processing prepares it for player use. Record identity uses the containing asset GUID and the object's `GlobalObjectId` file/prefab ids. These records are generated data, not new prefab definitions or published GA documents.

Validation covers configured production prefab and scene assets that depend on `GameAssetAuthoring`, including scenes outside Build Settings, using isolated Editor loading without saving them. Ordinary reconciliation, Play and build preparation use the same validation path. Content diagnostics identify the object, source GUID, reason and chosen fallback. Invalid native definitions retain their prefab key/GUID with an empty inert component set; invalid instance overrides resolve to the source definition. These defaults are prepared in memory and require no authored fallback asset. Infrastructure faults such as conflicting production indices still block preparation/builds.

## Runtime preparation and ownership

Pass the prepared set to `GameAssetLibraryLockBuilder.Build(templates, modules, definitions)`. The builder mounts native root definitions alongside JSON modules, registers effective instance snapshots as derived assets, then seals the lock. Native module id `_p_unity` is reserved; module, exact-key and GUID collisions are errors. Resolve instance references through `definitions.TryResolveReference(authoring, ...)` after mounting, so an instance with generated overrides obtains its derived key. Equal effective overrides share a derived content identity. The existing template, catalog, save and replica paths use the resulting exact key and GUID.

`Assets` contains prepared root definitions. `ResourceAssets` also contains effective override snapshots and is the source for resource traversal; scene-specific resource references must not be omitted by iterating only `Assets`. Serializable GAC values and references are frozen until the next preparation. Unity resources retain their native references rather than becoming copied resources.

The Editor reconciles synchronously before entering Play. Scene processing for Play and builds applies the generated index's saved snapshot identities by the scene's real GUID/local object ID, without reconverting unsaved values or saving the scene. A processed copy may lose its Unity prefab connection; its previously baked identity is retained only when the source exists in the index and any override belongs to that same source. Unknown or mismatched identities are cleared. For an explicit EditMode preview, call `PrefabGameAssetOverrideEditorUtility.ApplyIndexedSceneMetadata(scene, index)` before resolving its authorings. This also handles copied scenes and obsolete override IDs after a base definition change.

`PrefabGameAssetContentHashUtility.Compute(...)` aggregates sorted dependency GUIDs and their individual Unity hashes, including the source asset, rather than relying on Unity's cached aggregate prefab hash. `NativeContentHash` fingerprints these source hashes and generated override fingerprints. The native module hash also incorporates materialized template hashes and participates in the session's existing module compatibility contract. This covers resource-only changes even when the GRC baseline and its `MaterializedContentHash` are unchanged. A native mount requires captured content hashes from the generated index.

A product may pass `ResourceDependencies` as the third `PrefabGameAssetIndexConfiguration.Configure(...)` argument to resolve additional resource paths from collected GACs, for example Addressables references serialized only as GUIDs. These paths participate in hashing root definitions and effective instance snapshots even when Unity's dependency graph omits them. The two-argument configuration clears this optional callback; temporary Editor configurations must restore all three values.

The successful reconciliation retains the union of paths used by that same hash
calculation. Import, save, move and deletion events invalidate the index for
those dependencies even outside the authoring inclusion filter. Configuration
changes rebuild the dependency set. Unrelated resources and imports of the
generated index do not schedule another hierarchy scan or rewrite the index.

`GameAssetLibraryLockBuilder.CompareWithCurrentLibrary(locked, currentSnapshot)`
compares captured module, key, GUID and materialized-hash metadata from two
library snapshots. Obtain the current sealed snapshot through the same `Build`
or environment path used for the session, including its JSON modules and native
definitions. Comparison does not consult a separate filesystem catalog or live
definition SOs. Resource-only changes report `MOD_CHANGED`; changed runtime
content reports `CONTENT_HASH_MISMATCH` with both captured hashes. Derived instance
snapshots are checked by their exact keys, GUIDs and materialized hashes as well
as the native module fingerprint.

The application/session owner may use `GameAssetRuntimeEnvironment(codecs, context, index)` to own preparation, mounting and temporary definitions. Stop consumers before calling its idempotent `Dispose()`: it clears templates and disposes native definition SOs, while source prefabs, the index and Unity resources remain owned by Unity. With direct preparation, the caller owns `PrefabGameAssetDefinitions.Dispose()` and template lifetime. A new preparation/session is required to observe authoring changes. Authoring and reference resolution do not create or own a live GRO or GameObject lifecycle; feature owners continue to create objects through `RuntimeStore`.

Editor converter discovery caches its runtime type catalog and source matches until domain reload. Native save callbacks prepare and mark metadata without saving the same prefab recursively; the caller owns that save. Explicit reconciliation may save prepared prefabs outside the callback. Deferred preparation and reconciliation wait while metadata baking is active.

Each reconciliation owns one synchronous `GameAssetAuthoringPreparation`. It prepares and collects each source definition and effective instance once while assets are stable. Validation and override snapshot creation share one hierarchy pass, and root dependency hashing reuses the collected components. Saved prefabs invalidate the preparation; asset imports, moves and deletions also invalidate it through the Editor import version, including resources outside the index inclusion filter. Preview-scene results are released before closing their scene. No mutable authoring results are cached between reconciliations. Standalone Inspector collection and validation create their own preparation scopes.

`GameAssetLibraryLockBuilder.CompareWithCurrentLibrary(locked, current)` requires the explicit sealed current library. It compares packaged and native module fingerprints, exact keys, GUIDs and materialized hashes, including derived snapshots without latest aliases. It reads captured catalog metadata rather than global package registration or live authoring, so the result belongs to the supplied preparation.

## JSON module definitions

Definitions may also be authored directly as JSON documents through DingoCMS. Its transactional writer, filesystem module packages and generic EditorServer remain available independently of native prefab authoring. Both sources mount into the same immutable library and use the existing runtime template, save and replication contracts.

A native prefab is the sole authored source for its definition. It does not create or maintain a separate GA document or require a publish step. Feature models still create and mutate GRO/GRC through `RuntimeStore`, while GameObject presentation binds through dirty streams.
