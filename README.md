# DingoGameObjectsCMS

`DingoGameObjectsCMS` is a content-first runtime framework for Unity where gameplay definitions come from versioned JSON module packages or native Unity prefabs, are materialized into a runtime object tree, and then bridge into ECS, networking, view and persistent data when needed. Each definition has one authored source; `RuntimeStore` owns mutable runtime state.

The core idea is simple:

1. You describe an object with GACs in a JSON `GameAsset` document or a prefab-root `GameAssetAuthoring` component.
2. `GameAsset` builds a `GameRuntimeObject` composed of `GameRuntimeComponent` instances.
3. `GameRuntimeObject` lives inside a `RuntimeStore`, which can keep a tree of objects, track dirty changes, publish change streams, and link runtime objects to ECS entities.
4. The same runtime layer can then power:
   - ECS entity creation;
   - network replication;
   - commands;
   - modding;
   - persistent data.

This is not just a "ScriptableObject CMS". It is a unified game model where the external content catalog, runtime model, ECS bridge, replication, and modding all speak the same data language.

The [Prefab → GameAsset authoring](AssetObjects/Authoring/README.md) workflow supports native prefab definitions prepared in memory and mounted into the same immutable `LibraryLock` as JSON modules. Its generated index captures prefab identity, resource dependency hashes and saved instance value/resource overrides. The application owner disposes prepared definitions after consumers stop. Prefab definitions do not publish or maintain a separate GA document; JSON modules remain independent authored sources supported by DingoCMS.

Compare a captured catalog with a newly prepared one through `GameAssetLibraryLockBuilder.CompareWithCurrentLibrary(locked, current)`. The current library must be sealed. Comparison uses the supplied packaged and native module metadata, exact and derived identities, GUIDs and materialized hashes; it does not discover a current native catalog through global registration.

For the opt-in high-cardinality DOTS profile, see [DOTS + RuntimeStore Integration](DOTS_INTEGRATION.md).

## Why this solution is valuable

- **Content-first architecture.** Gameplay is described by assets and runtime components instead of growing scene-specific MonoBehaviour graphs.
- **Versioning is part of the model.** Assets carry both `GameAssetKey` and `GUID`, so you can evolve data shape through new asset versions instead of breaking old saves and profiles.
- **One model, many targets.** The same runtime object can become an ECS entity, a network payload, a persistent root object, or a mod asset.
- **Explicit runtime state.** Game state is stored in `RuntimeStore` trees instead of being scattered across scenes.
- **Static data platform.** `RuntimeStores` can hold both server and client realms at the same time, while `RuntimeExecutionContext` selects the active side for high-level code.
- **Dirty-by-design.** Stores accumulate structural and component-level changes as separate streams, so you do not need to re-send or rebuild the whole world every time.
- **Serialization is decoupled from networking.** Runtime serialization is abstracted behind `IRuntimePayloadSerializer`, and the Mirror layer works on top of that abstraction.
- **Mod-friendly storage.** The external base package and optional mod packages use the same keys, serialization rules, and resolver.
- **Flexible runtime authoring.** The same approach can model authored content and runtime-created domain objects such as settings, profiles, meta progression, and saves.

## Core concepts

### `GameAssetKey`

`GameAssetKey` consists of:

- `Mod`
- `Type`
- `Key`
- `Version`

Canonical module layout below `Application.persistentDataPath/assets`:

```text
<mod>/manifest.json
<mod>/<type>/<key>/<key>@<version>.json
<mod>/<resource folders...>
```

Example:

```text
base/characters/player/player@1.2.0.json
```

Version resolution rules:

- `version == null` means an exact request to `0.0.0`
- `version == ""` or a whitespace string means `latest`
- `latest` resolves to the highest available canonical numeric `major.minor.patch` version within the same `(mod, type, key)`

This gives you a useful balance:

- code can lock to an exact data shape when needed;
- higher-level systems can request “the newest compatible asset”.

### `GameAssetScriptableObject`

This is the in-memory Unity reconstruction type used by the framework. It stores:

- `GameAssetKey`
- a unique `GUID`

The `GUID` identifies a concrete asset/version instance. It is a separate identity from the logical `GameAssetKey`. Instances of this type may still be useful in tests or tooling, but Unity `.asset` files are not the canonical authoring format.

### `GameAsset`

`GameAsset` is the versioned description of an object. It stores a list of `GameAssetComponent` entries and can:

- build a `GameRuntimeObject` through `SetupRuntimeObject(...)`
- build a `GameRuntimeCommand` through `CreateRuntimeCommand()`

This is where the asset model becomes the runtime model.

### `GameRuntimeObject`

`GameRuntimeObject` is the base runtime node in the object tree. It stores:

- `Key`
- `AssetGUID`
- `SourceAssetKey`
- a list of `GameRuntimeComponent`
- `InstanceId`
- `StoreId`
- `Realm`

It also supports:

- adding and replacing runtime components
- tracking dirty data changes and dirty component structure changes
- creating ECS entities through `CreateEntity(...)`
- holding runtime linkage to `RuntimeStore`, ECS editing context, and linked `Entity`

Dirty rule:

- `TakeRW<T>()` marks the component dirty automatically;
- if a system mutates a `GameRuntimeComponent` through an already captured reference, query result, or `RuntimeInstance` lookup, it must explicitly call `SetDirty(...)` on `GameRuntimeObject` or `RuntimeStore`.
`SourceAssetKey` is used for source/presentation linkage and related runtime scenarios. It is not version lineage metadata and it is not inheritance — see `GameAssetPrefab` for that.

### `GameAssetPrefab`

A `GameAsset` may declare `Prefab` instead of duplicating another asset: an exact base key plus four sparse override lists — `RemovedComponents`, `OverrideComponents`, `RemovedFields`, `OverrideFields`. They apply in that order.

Components are addressed by `$type` alias; a component in `OverrideComponents` replaces one the base already provides and is otherwise added, always as a whole. Everything else is addressed by a path rooted at the component list:

```json
"Prefab": {
  "Base": { "Mod": "base", "Type": "static", "Key": "tavern_mage", "Version": "0.0.0" },
  "OverrideFields": { "/UnitStack_GAC/InitialMemberCount": 1 }
}
```

A `..` segment walks one level up, so a sibling component is `/A_GAC/../B_GAC/Field` and a document root field is `/../Surfaces/0/Width`. Identity and composition properties stay unreachable however the path gets to them.

A path reaches any nested leaf, so overriding one field never means restating its parent. A value is replaced as a whole — there is no deep merge — which keeps a collection override atomic exactly like the runtime patch lane. `OverrideFields` is applied in ordinal path order, so the result never depends on the order the document happened to list the paths in.

Component position inside the document carries no meaning: materialization order comes from `[GameAssetSetupOrder(n)]` on the component type, lowest first, so an appended component behaves the same as an authored one. Components without the attribute share order `0` and keep their authored order among themselves.

Composition runs in `GameAssetDocumentComposer` while the module document is loaded, before it is deserialized, so everything downstream — materialization, the immutable library lock, the wire — sees an ordinary complete asset and cannot tell a derived asset from a duplicated one. The base may live in any mounted module. The composed document keeps no prefab declaration, so composing twice is a no-op; read lineage from the raw document with `GameAssetDocumentComposer.TryReadBaseKey`.

### `GameRuntimeCommand`

`GameRuntimeCommand` is the command-side runtime payload. It uses the same component language as `GameRuntimeObject`, but represents an intent to execute rather than a persistent node in a store tree.

Practical meaning:

- `GameAsset` can build a command through `CreateRuntimeCommand()`;
- a command is composed of `GameRuntimeComponent` instances, just like an object;
- commands are consumed by `RuntimeCommandsBus`, not stored as runtime world state inside `RuntimeStore`.

This keeps object state and gameplay intent in the same data vocabulary without forcing commands to behave like ordinary runtime objects.

### `GameRuntimeComponent`

`GameRuntimeComponent` is the base runtime component type. It defines runtime shape and can participate in ECS projection through:

- `SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)`
- `AddForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)`
- `RemoveFromEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)`

This boundary is important:

- if a component only stores data, it does not need to participate in ECS at all;
- if it belongs to simulation, it can emit the required ECS components;
- if it must participate in dirty replication, it implements the appropriate dirty markers.

In the current design the ECS-facing contract is built around `EntityCommandBuffer`, not around direct `World` access. This keeps structural edits consistent when a `GameRuntimeObject` is still working with a deferred entity created earlier in the same editing scope.

## Data architecture

### `RuntimeStore`

`RuntimeStore` is a runtime object tree that:

- stores all objects in a store
- distinguishes root objects from children
- keeps parent/child links
- maps `RuntimeInstance.Id` to ECS `Entity`
- accumulates dirty operations
- publishes three change streams:
  - `StructureChanges`
  - `ComponentStructureChanges`
  - `ComponentChanges`

`IsFlushInProgress` is a readonly computed property: it is `true` during an actual flush of a live store, including `StructureChanges`, `ComponentStructureChanges`, `ComponentChanges`, `CommittedBatch`, and `DirtyPublishCompleted` callbacks, and `false` outside the flush. Retirement resets it to `false`, including retirement inside a callback before the flush stack returns. It provides a synchronous observation so the owner can defer a nested synchronous `FlushToQuiescence(...)` call until the active flush has ended; the owner must also respect `Retired`. It does not provide thread safety or schedule retries.

Supported structural operations:

- `Create`
- `CreateChild`
- `AttachChild`
- `DetachChild`
- `MoveChild`
- `Remove`

Removal supports multiple modes:

- remove a whole subtree
- remove a node and move its children to root
- remove a node and reparent its children to the parent

This gives you more than an entity list. It gives you a hierarchical runtime world model.

### `RuntimeStores`

`RuntimeStores` is the static data platform entry point.

Responsibilities:

- store all server-side `RuntimeStore` instances
- store all client-side `RuntimeStore` instances
- keep net-direction metadata
- hold the ECS `World` used by newly created stores


Before creating or resolving stores you must call `RuntimeStores.SetupWorld(world)`. Store creation is fail-fast if no valid `World` has been registered.

This layer is intentionally low-level. It knows about both realms at once and is used by infrastructure such as replication and ECS linkage.

### `RuntimeExecutionContext`

`RuntimeExecutionContext` is the high-level execution selector on top of `RuntimeStores`.

It exposes:

- current execution phase
- stable runtime role
- active read realm
- active write realm
- whether store mutation is allowed
- active store dictionary for the current phase

This allows project code to react to runtime role changes without hard-coding `ServerStores` versus `ClientStores` everywhere.

### `RS`

`RS` is the narrow high-level access point for application/domain code.

Usage model:

- call `RS.Bind(storeId)`
- receive an `IReadonlyBind<RuntimeStore>`
- read the current active store from `bind.V`

`RS` resolves the store through `RuntimeExecutionContext`, reads only the already existing store in the active realm, and automatically rebinds when the active execution side changes.

If `bind.V == null`, the store has not been built or injected yet by another layer. Explicit store creation/loading should go through infrastructure code such as `RuntimeStores` or `RS.Set(...)`.


Recommended rule:

- infrastructure may talk to `RuntimeStores` explicitly;
- high-level models, binders, and view code should prefer `RS`.

### Dirty model

Key markers:

- `IStoreDataDirty` means component data changes should be replicated
- `IStoreStructDirtyIgnore` means structural changes for that component should be ignored

Practical meaning:

- you explicitly control what becomes part of delta replication;
- local-only components do not need to generate network noise;
- store structure and component data exist as separate change channels.


Explicit dirty notification:

- `GameRuntimeObject.SetDirty<T>()`
- `GameRuntimeObject.SetDirtyById(...)`
- `RuntimeStore.SetDirty<T>(instanceId)`
- `RuntimeStore.SetDirty<T>(runtimeInstance)`

Use this when data was changed outside the normal `TakeRW<T>()` path. Typical case: an ECS or bridge system resolves a `GRC`, mutates its fields directly, and then explicitly notifies the store that component data changed.
### Realm and network direction

The framework supports realm separation for stores:

- `StoreRealm.Server`
- `StoreRealm.Client`

An integration layer can additionally split stores by network direction:

- `None`
- `S2C`
- `C2S`

This allows identical store ids on server and client while keeping ownership and replication policy explicit.

## Data flow

### Asset -> Runtime -> ECS

The main flow is:

```text
GameAsset
  -> SetupRuntimeObject(...)
  -> GameRuntimeObject
  -> RuntimeStore
  -> CreateEntity(...)
  -> ECS Entity + ECS Components
```

ECS is not the source of truth here. The runtime model is.

For ECS projection there are now two distinct layers:

- `GameRuntimeObject` / `GameRuntimeComponent` mutation, which changes authoritative runtime data;
- ECS projection hooks on `GameRuntimeComponent`, which receive an `EntityCommandBuffer` and materialize or remove ECS-side representation.

During initial ECB projection the root entity carries `RuntimeProjectionPending`. The final projection command removes the tag, so generic ECS consumers can explicitly exclude incomplete roots.

Checkpoint staging projects each subtree with its restored store cohort.
Projection hooks resolve cross-store references through
`RuntimeStore.TryResolveProjectionStore`, which validates the staged epoch and
never falls back to live stores outside that cohort. The scope is released even
if projection fails; ordinary projection continues to resolve active stores.
Preparing a checkpoint therefore does not publish its stores or expose a
partially restored graph through `RS`.

Restore publication commits the entire scoped cohort to its realm before
notifying binding observers. If an observer throws, publication still retires
the replaced stores and publishes lifecycle notifications before rethrowing the
original exception. A checkpoint stage reports `IsPublished` from the actual
registry references even when that callback fails. Its disposal retains a
published cohort; failures before the registry swap still abort staged stores.
Server and Client publication follow the same rule and leave the other realm
unchanged.

Hot checkpoint owners can use `RuntimeObjects.Replay.RuntimeReplayNativeRows<T>`
for retained unmanaged rows. It owns one persistent `NativeList<T>` and provides
capture preparation, non-clearing capacity reservation, capacity-preserving reset,
reserve estimation and disposal.
Capture jobs may borrow its array or parallel writer without copying; they must
finish before capacity changes, reset, preparation or disposal. This storage
mechanism does not choose entity queries, state authority, validation policy or
a cold wire format.

`RuntimeReplayOwnedStorage(order)` declares a public readonly storage field for
the Editor-only `RuntimeReplayOwnedStorageLifecycleGenerator`. Its explicit
profile supplies the owner `Type`, output path and cleanup policy. The owner is
a public concrete top-level non-generic `IDisposable` class with a public
parameterless constructor, `Reset()` and `Dispose()`. Marked storage has the same
constructor and method contract; every `RuntimeReplayNativeRows<T>` field must
be declared. The generated partial owns only the constructor, reset and disposal
with direct typed calls in declaration order. Queries, capacity and gameplay
validation remain with the owner, and jobs must finish before lifecycle calls.
`AggregateFailures` attempts every cleanup and retains nested cleanup failures
and initialization failures. `FailFast` stops at the first cleanup failure;
during constructor cleanup that failure replaces the initialization exception.
Generation validates and emits the whole explicit profile batch before writing.
`AreOutputsCurrent` checks source without writing; `GenerateAndWrite` reuses
unchanged bytes and timestamps. Inherited, static, mutable, missing and duplicate
storage declarations fail before output mutation. A product binding owns build
ordering and stale-output rejection; lifecycle output carries `.Lifecycle.g.cs`
provenance and must be regenerated rather than hand-edited.

This is intentionally not a generic always-live two-way sync. Runtime data can stay authoritative in `RuntimeStore`, while high-frequency simulation can still move into DOTS when needed.

### Asset -> Runtime -> View

The view layer can subscribe to runtime objects through `GameRuntimeObjectView` and `GameRuntimeObjectsCollection` without breaking separation between data and presentation.

`GameRuntimeObjectsDirtyCollection.RefreshInclusion(IEnumerable<long> keys)`
reevaluates selected keys when an external membership filter changes. It adds
newly included rows and releases excluded rows through the normal collection
lifecycle. Rows that remain included keep their containers and prepared resources;
their operations and reveal effects are not replayed. This refresh does not write
Store data or mark objects dirty. It snapshots the supplied keys and aborts if a
callback invalidates the current binding, including a rebind to the same Store.

### Asset -> Runtime -> Network

### Runtime -> Persistence

The framework also fits persistent/runtime domain data well:

- `settings`
- `profiles`
- `metas`
- `saves`

Current recommended pattern:

- persistent data is just regular named `RuntimeStore` data; there is no special persistence-only store layer inside the framework;
- choose store boundaries by domain and access patterns, not by an artificial split between `persistent` and `gameplay`;
- use authored assets only where versioned content authoring actually matters; domain data such as profiles, settings, meta, and save roots can be created directly as runtime objects;
- keep migration, compatibility, and disk/cloud save policy at the project level.

`DingoGameObjectsCMS` does not ship a full disk/cloud persistence service, but it already provides the runtime model, serialization primitives, and change tracking needed to build one.

## Serialization


Serialization is built around `IRuntimePayloadSerializer`.

Why this matters:

- the runtime layer does not depend on a concrete format;
- Mirror is not the owner of serialization;
- the current JSON implementation can later be replaced by a binary or other optimized format.

Current state:

- the default serializer is `JsonRuntimePayloadSerializer`
- the global swap point is `RuntimePayloadSerialization`
- runtime components use a generated, compiled `Type -> Id` table with direct CLR `Type` handles, compact numeric ids, reserved numeric slots, and `RegistryHash`

Required project artifact:

```text
<project>/Generated/RuntimeComponentTypes.Generated.cs
<project>/Generated/RuntimePatchCodecs.Generated.cs
```

It is compiled into the player and is used for:

- network replication of runtime components;
- deserialization of runtime components by `compTypeId`.

Entries contain only the compact payload `Id` and a direct `RuntimeType = typeof(T)`. The CLR `Type` itself is the generator and runtime identity; there is no string component key, type name, assembly name, or alias contract. Removed slots are retained only as numeric `ReservedIds`, without removed CLR metadata. `RegistryHash` covers the active `Type -> Id` table and numeric reservations and is intended for compatibility checks between builds. Runtime never reads this table from JSON or discovers component types with reflection.

`GameRuntimeObject` and `GameRuntimeCommand` JSON encode each component as an explicit numeric `TypeId` plus `Payload`. They do not use Json.NET `$type`, a serialization binder, CLR names, assembly names, runtime assembly scans, or legacy aliases.

During active development the table is regenerated through the project binding for `RuntimeComponentTypeManifestGenerator` or as part of build preprocessing. That binding supplies the project's explicit active-type scope and passes the previous generated static `CreateManifest` directly; there is no provider-name lookup. Types that leave the scope disappear from the active table while their ids become append-only numeric reservations. Diagnostic JSON, if a project wants it, belongs only in a disposable Editor `.temp` directory and is never runtime authority. This does not change external mod `manifest.json` files: those remain data owned by the mod packaging protocol.

The generated patch-codec artifact follows the same rule. Active component
entries are keyed by direct `Type` handles. Generated codecs address fields by
compact numeric ids and direct member access. The patch schema contains active
components and their current generated field layout only: removed patch fields
do not leave tombstones or names behind. Persistent component-slot reservations
belong to the separate runtime component type registry. Both runtime-binary and
authoring patches persist only `ComponentTypeId`/`FieldId`; CLR types and
`FieldInfo` exist only in the compiled lookup tables. The artifact contains no
string component identity, CLR/assembly names, value signatures, or
removed-entry records. `SchemaHash`, direct codecs, and static `CreateManifest`
live in one compiled C# authority. Patch generation does not read or publish a
mutable `runtime_patch_schema.json` file.

Generation refuses partial assembly discovery and reports the assembly and loader
failure rather than reconciling an incomplete type set. Generated namespaces and
symbols must be valid C# identifiers. Types uses `IsOutputCurrent`; Patch and DOTS
use `AreOutputsCurrent` to check expected source without creating or repairing
files. Repeated `GenerateAndWrite` preserves unchanged output and its timestamp.
DOTS checks the previous compiled manifest hash and active/reserved ID ledger,
then invokes the optional profile `ValidateManifest` callback on the new
reconciled manifest before emission or writing. The read-only check invokes the
same callback, so products can enforce coverage without SDK dependencies on their
domain. Explicit additional fingerprint inputs must exist; the generated
fingerprint source and its metadata remain excluded from its own input set.

`SchemaHash` fingerprints the current complete generated wire layout and `FieldId`-to-member mapping at generation time, including primitive kinds, recursive struct/list/array layouts, enum underlying types and named numeric values. This Editor-only fingerprint contributes only the final digest to the compiled artifact; no CLR-name identities, field-name ledgers or value signatures are added to runtime manifests. Reordering discovery or declarations without changing the canonical layout does not change the digest.

Generated patch codecs and canonical authoring JSON support one-dimensional,
zero-based `T[]` arrays of the same deterministic atoms, enums and value structs
supported in `List<T>`. Arrays preserve null separately from an empty collection,
use the existing bounded collection length encoding and `CustomList` field lane,
and generate direct indexed serialization, structural equality and deep clones.
Value structs may contain supported collection fields; direct jagged and
multidimensional arrays are rejected. Existing list layouts and their schema
identities remain unchanged; array layouts have their own fingerprint kind.

## Network synchronization

Mirror is the transport/framework; the DingoCMS runtime protocol synchronizes
authoritative `RuntimeStore` generations through the `RuntimeProtocol*` stack.
A strict session manifest validates the protocol version, build, runtime schema,
and exact GA catalog before a replica store is created. There is one current
protocol implementation and no compatibility adapter for older wire contracts.
The complete session manifest must fit one reliable Mirror message. The endpoint
measures its packed wire size, including its message identity, against the active
transport budget before the handshake reaches `ManifestSent`. An oversized or
unencodable manifest is rejected immediately with `InvalidManifest`; it cannot
start replica creation or wait silently for a baseline.
Parent-first binary baselines and ordered reliable deltas are staged and
published atomically. Delivery sequence, baseline id, store revision, ACK,
bounded pending queues, and resync are independent protocol concepts.

Replica publication commits the complete store cohort before notifying binding
observers. A notification failure still retires replaced stores and publishes
lifecycle changes, then reports the original failure or collected failures.
Committed baseline stages remain `Published`; abort and failure cleanup cannot
retire the active cohort. Protocol coordinators log a committed notification
failure and finish checkpoint commit, receiver acknowledgement and readiness.
Failures before publication retain the staging rejection/resync path.

Gameplay composition owns a `DingoNetworkManager.BindRuntimeProtocol` binding.
Its returned `DingoRuntimeProtocolBinding` is disposed before the checkpoint
owners, stores and ECS World it borrows. Retirement clears the factory, command
getter and both endpoint references first, attempts every endpoint cleanup, and
stops the owning manager's Mirror session. A deferred transport disconnect is
completed locally before borrowed gameplay dependencies can retire. Cleanup
failures remain observable. Rebinding first retires the previous registration,
endpoints and owning Mirror session; cleanup failure prevents publication of the
replacement. Disposing an older binding preserves a replacement binding; the
existing individual setters first retire an owned registration before selecting
unowned configuration. Null replacements preserve the current owner. Either
order of the two setters can then configure the complete unowned pair; cleanup
failure prevents the replacement setter from publishing its delegate.
Client coordinators likewise clear only their own outbound command dispatcher,
so late disposal cannot disconnect a replacement coordinator from the bus.

Replication data has three explicit classes:

- `StructuralReliable`: object and GRC presence, hierarchy/order,
  client-visible ownership, and construction;
- `ReliableState`: durable semantic state such as health, inventory, carried
  units, and match state;
- `HotUnreliable`: transforms, velocity, aim, projectile/combat samples, and
  animation.

Hot state uses `RuntimeStateStreamProfile<TSample>`,
`RuntimeStateStreamCollector<TSample>`, and
`RuntimeStateStreamReceiver<TSample>`. Profiles own quantization and packed
sample encoding. Frames carry a stream type id, tick, and per-stream sequence;
they do not use `RuntimeObjectPatch` or ordinary semantic GRC diff.

The client coordinator is the one wire ingress: it authorizes the store,
resolves the profile, decodes the envelope, validates its header and canonical
packed samples, and only then publishes the profile-validated frame to the
typed receiver. Project adapters never decode raw wire payloads. A connection
coalescer accepts the profile itself, including its
`RuntimeStateStreamLifetime`; retained-state heartbeat is enabled only for
`EphemeralStreamEntity`, never for `StructuralRuntimeObject`.

Component presence changes are structural. A hot component uses payloadless
`AddPresence`/`Remove`; ordinary `Add`, `Fields`, and `Custom` carry semantic
state and are rejected for `UnreliableState`. Do not toggle component presence
for frequent state such as attacks; keep a stable `CombatState`-shaped
component with mode, target, cooldown, and sequence. ECS-only projectiles,
particles, short hit areas, and temporary targets do not need a GRO unless they
acquire a meaningful durable identity.

Complete-set reconciliation is an immutable segmented `Begin..End` cycle and
is applied atomically only at `End`. It is capped at 10 segments / 500 ms at
20 Hz. An interest revoke aborts the old outgoing cycle and restarts it from
the current eligible set. A rejected single-frame reconciliation drops its
provisional buffer and leaves its sequence uncommitted, so the exact frame can
be retried without publishing partial data.

`RuntimeNetworkTelemetry` exposes payload bytes/second per wire stream, encode
time and allocations across preparation, `Pack`/coalescing, canonical
validation, and final wire encoding, dirty components per committed tick,
current projected and last ACKed membership per connection, baseline sizes,
and resync count.
The existing `RuntimeProtocol*` coordinators and `RuntimeStoreNetServer` /
`RuntimeStoreNetClient` endpoints expose snapshots without per-GRC adapters.
Keep per-connection interest filtering and shadow state. If measurements prove
duplicate encoding material, group identical interest memberships and encode a
common payload once while retaining per-connection delivery and ACK state.

## Command bus

`RuntimeCommandsBus` is a late-update command queue.

Mechanics:

- a command is a `GameRuntimeCommand` composed of runtime components
- when executed, the bus iterates components and calls `ICommandLogic.Execute(...)`
- the networking layer can intercept the command through `BeforeExecute`

Why it matters:

- commands use the same component language as objects;
- spawn/change logic can be expressed with the same data-oriented primitives as the runtime world.

[`RuntimeReplayCommandRegistry.ContainsComponentType(Type)`](RuntimeObjects/Commands/RuntimeReplayCommandRegistry.cs)
queries the existing registration by exact CLR component type, including before
the registry is sealed. It does not match a base type to a registered derived
type, execute codecs, instantiate commands or change the catalog. A null type
is rejected. Host Editor/build checks may use it with their explicit assembly
and namespace scope to require replay coverage. This read-only query does not
change generic SDK live command admission or replay store-scope handling.

## Modding and external asset packs

`GameAssetLibraryManifest` mounts the asset library directly from the filesystem:

- mod root: `Application.persistentDataPath/assets`;
- built-in-like mod: `Application.persistentDataPath/assets/base`;
- external mods: sibling folders with `manifest.json`.

Capabilities:

- base and external assets resolve through the same `GameAssetKey`
- external mods can override base assets
- mod folders are indexed without ScriptableObject configuration
- assets can be requested by exact version or by `latest`

`ModPackage` lazily loads JSON assets by `GameAssetKey` and reconstructs the required `ScriptableObject`.

At session startup the library is captured as an immutable content snapshot. Editing disk files does not mutate an already running gameplay catalog; a new session is required to mount the new revision.

## External authoring and package delivery

The distributable unit is an external module folder containing canonical JSON, resources, and a derived `manifest.json`. A clean installation receives `base` as an external content package at `Application.persistentDataPath/assets/base`; it is not rebuilt from or imported into the Unity project.

Immediate children of the assets root whose names begin with `.` are operational directories and are not mounted as modules. The assets root may therefore be a Git working tree without `.git` entering runtime module discovery.

Author content directly through the sibling [DingoGameObjectsCMSEditorServer](../DingoGameObjectsCMSEditorServer/README.md). Its MCP and Web clients edit staged `JObject` changesets in the mounted AppData library, validate the result, derive the manifest, and publish with content-hash conflict protection. There is no Unity `AssetDatabase` build/import round trip.

Unity Editor generators remain only for checked-in compiled runtime contracts such as the direct-`Type` component table, compact ids, numeric reservations, and registry hash. They compile code/system contracts; they do not author or package content.

## Dependencies

### Direct submodule dependencies

| Dependency | Repository | Branch | Why it is needed |
| --- | --- | --- | --- |
| `DingoProjectAppStructure` | `https://github.com/DingoBite/DingoProjectAppStructure.git` | `not pinned in .gitmodules` | `AppModelBase`, app root lifecycle, external dependencies |
| `UnityBindVariables` | `https://github.com/DingoBite/UnityBindVariables` | `not pinned in .gitmodules` | `Bind`, `BindDict`, reactive containers used by `RuntimeStore` and the view layer |
| `DingoUnityExtensions` | `https://github.com/DingoBite/DingoUnityExtensions` | `dev` | singletons, pools, view providers, serialization helpers, utilities |

Notes:

- these are the direct dependencies of `DingoGameObjectsCMS` itself;
- other submodules in the superproject may be used by a host game, but they are not required by this framework directly.

### Packages and external libraries

- `Unity.Entities` / `Unity.Collections` — ECS bridge
- `Mirror` — networking layer for `Mirror/`
- `Newtonsoft.Json` — default serialization and mod JSON
- `NaughtyAttributes` — editor UX

## Limitations and trade-offs

- the framework intentionally adds its own runtime layer on top of ECS instead of replacing ECS;
- high-level code is expected to go through `RuntimeExecutionContext` / `RS`, while low-level infrastructure may still work with explicit realms;
- generated serialization schemas must stay up to date and checked in;
- a full persistence service is not included out of the box;
- the Mirror layer is optional, but if you use it, you must respect the snapshot/delta contract;
- versioning helps with shape evolution, but migration policy still has to be designed at the project level.

## When this approach shines

This approach is especially strong when you care about several of the following:

- asset-driven gameplay
- versioned content pipelines
- a shared data model for ECS, networking, and persistence
- mod support
- an explicit authoritative runtime model
- the ability to serialize the game world as an object tree

If reduced to one sentence:

> `DingoGameObjectsCMS` turns `GameAsset` and `GameRuntimeObject` into a shared source of truth for runtime state, ECS integration, replication, modding, and persistence.




