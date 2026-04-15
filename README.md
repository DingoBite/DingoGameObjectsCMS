# DingoGameObjectsCMS

`DingoGameObjectsCMS` is a content-first runtime framework for Unity where gameplay behavior is described by versioned assets, materialized into a runtime object tree, and then bridged into ECS, networking, view, and persistent data when needed.

The core idea is simple:

1. You describe an object with a `GameAsset`, not with scene-specific code.
2. `GameAsset` builds a `GameRuntimeObject` composed of `GameRuntimeComponent` instances.
3. `GameRuntimeObject` lives inside a `RuntimeStore`, which can keep a tree of objects, track dirty changes, publish change streams, and link runtime objects to ECS entities.
4. The same runtime layer can then power:
   - ECS entity creation;
   - network replication;
   - commands;
   - modding;
   - persistent data.

This is not just a "ScriptableObject CMS". It is a unified game model where the asset pipeline, runtime model, ECS bridge, replication, and modding all speak the same data language.

## Why this solution is valuable

- **Content-first architecture.** Gameplay is described by assets and runtime components instead of growing scene-specific MonoBehaviour graphs.
- **Versioning is part of the model.** Assets carry both `GameAssetKey` and `GUID`, so you can evolve data shape through new asset versions instead of breaking old saves and profiles.
- **One model, many targets.** The same runtime object can become an ECS entity, a network payload, a persistent root object, or a mod asset.
- **Explicit runtime state.** Game state is stored in `RuntimeStore` trees instead of being scattered across scenes.
- **Static data platform.** `RuntimeStores` can hold both server and client realms at the same time, while `RuntimeExecutionContext` selects the active side for high-level code.
- **Dirty-by-design.** Stores accumulate structural and component-level changes as separate streams, so you do not need to re-send or rebuild the whole world every time.
- **Serialization is decoupled from networking.** Runtime serialization is abstracted behind `IRuntimePayloadSerializer`, and the Mirror layer works on top of that abstraction.
- **Mod-friendly pipeline.** Built-in assets and external mod packs use the same keys, the same serialization rules, and the same resolver.
- **Flexible runtime authoring.** The same approach can model authored content and runtime-created domain objects such as settings, profiles, meta progression, and saves.

## Core concepts

### `GameAssetKey`

`GameAssetKey` consists of:

- `Mod`
- `Type`
- `Key`
- `Version`

Canonical asset layout:

```text
Assets/GameAssets/<mod>/<type>/<key>/<key>@<version>.asset
```

Example:

```text
Assets/GameAssets/base/characters/player/player@1.2.0.asset
```

Version resolution rules:

- `version == null` means an exact request to `0.0.0`
- `version == ""` or a whitespace string means `latest`
- `latest` resolves to the highest available semver within the same `(mod, type, key)`

This gives you a useful balance:

- code can lock to an exact data shape when needed;
- higher-level systems can request “the newest compatible asset”.

### `GameAssetScriptableObject`

This is the base `ScriptableObject` for the framework. It stores:

- `GameAssetKey`
- a unique `GUID`

The `GUID` identifies a concrete asset/version instance. It is a separate identity from the logical `GameAssetKey`.

### `GameAsset`

`GameAsset` is the versioned description of an object. It stores a list of `GameAssetComponent` entries and can:

- build a `GameRuntimeObject` through `SetupRuntimeObject(...)`
- build a `GameRuntimeCommand` through `CreateRuntimeCommand()`

This is where the asset model becomes the runtime model.

### `GameRuntimeObject`

`GameRuntimeObject` is the base runtime node in the object tree. It stores:

- `Key`
- `AssetGUID`
- `SourceAssetGUID`
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
`SourceAssetGUID` is used for source/presentation linkage and related runtime scenarios. It is not version lineage metadata.

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

This is intentionally not a generic always-live two-way sync. Runtime data can stay authoritative in `RuntimeStore`, while high-frequency simulation can still move into DOTS when needed.

### Asset -> Runtime -> View

The view layer can subscribe to runtime objects through `GameRuntimeObjectView` and `GameRuntimeObjectsCollection` without breaking separation between data and presentation.

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
- runtime components use a manifest-based type id mapping

Required runtime artifact:

```text
Assets/StreamingAssets/runtime_component_types.json
```

It is required for:

- network replication of runtime components;
- deserialization of runtime components by `compTypeId`.

Whenever you add a new `GameRuntimeComponent`, regenerate the manifest through `Tools/Runtime Types/Generate Manifest` or as part of build preprocessing.

## Network synchronization

The networking layer is built on top of Mirror, but it synchronizes `RuntimeStore`.

Server side:

- subscribes to store dirty events
- builds full snapshot or delta payloads
- sends snapshots when the client becomes ready
- waits for `Ack`
- can trigger resync

Client side:

- receives sync messages
- deserializes payloads
- applies snapshot/delta through `RuntimeStoreSnapshotCodec.ApplySync`
- acknowledges successful application
- requests full resync after an application failure

Supported modes:

- `FullSnapshot`
- `DeltaTick`

The key idea is that gameplay networking sees the world as a serializable runtime structure instead of a pile of arbitrary MonoBehaviours.

## Command bus

`RuntimeCommandsBus` is a late-update command queue.

Mechanics:

- a command is a `GameRuntimeCommand` composed of runtime components
- when executed, the bus iterates components and calls `ICommandLogic.Execute(...)`
- the networking layer can intercept the command through `BeforeExecute`

Why it matters:

- commands use the same component language as objects;
- spawn/change logic can be expressed with the same data-oriented primitives as the runtime world.

## Modding and external asset packs

`GameAssetLibraryManifest` builds a library of assets from two sources:

- built-in assets inside the project;
- external mod packs mounted through `manifest.json`.

Capabilities:

- built-in and external assets resolve through the same `GameAssetKey`
- external mods can override built-in assets
- mount points have priority
- assets can be requested by exact version or by `latest`

`ModPackage` lazily loads JSON assets by `GameAssetKey` and reconstructs the required `ScriptableObject`.

This makes modding an extension of the same asset pipeline, not a separate add-on system.

## Editor tooling

The package ships with editor tools for the asset pipeline:

- `GameAssetKeyRebuilder`
  - works only with the canonical layout
  - synchronizes `_key` with the asset path
- `GameAssetVersioningTools`
  - duplicates the selected versioned asset
  - bumps semver
  - generates a new GUID
- `ModBuilder`
  - exports a mod as JSON + `manifest.json`
- `ModImporter`
  - imports a JSON mod back into Unity assets
- `SubAssetFixer`
  - rebuilds sub-assets after import
- `RuntimeComponentTypeManifestGenerator`
  - updates the runtime component type id manifest

These tools are not just editor conveniences. They protect the main contract of the framework: asset shape, versioning, serialization, and runtime reconstruction must stay aligned.

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
- the serialization manifest must stay up to date;
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





