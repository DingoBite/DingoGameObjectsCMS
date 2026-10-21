# Built-in baked templates and GA-owned products

This path uses Unity baking for built-in technical Entity prefabs while keeping
product composition in GA/GRC definitions. A baked template supplies a known
technical graph, such as collider, physics or rendering components. The product
definition supplies its complete gameplay signature and configures that graph
under the template's explicit contract.

The source prefab, its loaded Entity graph and prepared shared GA definitions are
immutable. A product does not receive a parallel prefab-based gameplay catalog.
The existing ordinary GameObject presentation path remains independent.

## Authoring and build inclusion

1. Put `GameAssetAuthoring` and `BakedEntityPrefabAuthoring` on the technical
   prefab root. Existing automatic authoring preparation produces
   `BakedEntityPrefab_GAC` with the prefab's native `EntityPrefabReference`.
2. Use Unity Bakers to produce the technical component data. Include every
   technical descendant in the root's flat `LinkedEntityGroup`, root first.
   Nested groups must remain inside that graph and list their own root first.
   Technical descendants do not declare independent factory product identities.
3. Reference the technical native GA from a product GAC using ordinary
   `GameAssetReference`. `PrefabGameAssetReference` is the native Unity authoring
   picker; it resolves through the existing native definitions path.
4. Put `BakedEntityPrefabLibraryAuthoring` in a technical library scene/subscene
   and assign its generated `PrefabGameAssetIndex`. Its Baker derives weak
   prefab references from indexed descriptor GACs.
5. In a product-owned Editor initializer, configure the existing native index
   path/filter with `PrefabGameAssetIndexConfiguration.Configure(...)` and the
   library scene paths with
   `BakedEntityPrefabLibraryBuildConfiguration.Configure(...)`. Unity's
   `IEntitySceneBuildAdditions` includes the library and its weak dependencies in
   normal baking/build output. The SDK contains no product-specific paths.

Library scenes are baking/build inputs. They are not runtime maps or a second
runtime scene-loading API. A prefab mentioned only by a GA still needs this
build inclusion path.

See [native prefab authoring](../AssetObjects/Authoring/README.md) for native
identity, variants, immutable preparation and dependency hashes. The descriptor
GRC stores the same Unity prefab GUID as `Hash128` and computes `Prefab`; no
loaded Entity handle is serialized.

## Resolve and prepare before projection

Create `BakedEntityPrefabLoader` for the same live World as the target Store.
Create `BakedEntityPrefabPreparation` with the session's template cache, sealed
catalog and that loader. The bridge resolves an ordinary GA reference against
that exact catalog and reads its `BakedEntityPrefab_GRC`.

```csharp
using DingoECSUtils.DotsPrefabLibAccess;
using DingoGameObjectsCMS.RuntimeObjects;

var loader = new BakedEntityPrefabLoader(store.World);
var preparation = new BakedEntityPrefabPreparation(templates, catalog, loader);
var prepared = await preparation.PrepareAsync(technicalTemplateReference, token);
```

The feature owner explicitly selects Static, Prewarmed or Lazy loading. For
Prewarmed use, await preparation before factory projection and product creation.
Observe `prepared.Status`, `prepared.IsReady` and `prepared.Error`; an unavailable
template follows the caller's semantic unavailable/inert state and must not be
passed to the graph-creation API. Invalid descriptor content is diagnosed through
the existing safe content fallback path.

Retain the owner context across asynchronous work. Cancel and retire replaced
contexts so a late completion cannot create products in their replacements.
Factory `SetupForEntity(...)` remains synchronous; it consumes a ready lease
prepared by its owner. It does not start asynchronous loading or publish partial
projection readiness.

## Compile the complete product and prewarm instances

The following fragment runs at a controlled preparation boundary. `factoryRoot`
is the already created factory root, `factoryInstance` is its RuntimeInstance,
and `productDefinition` is the materialized complete product GA/GRC definition.
The factory component receives its ready template through explicit owner wiring
before projection; lease handles are not serialized configuration.

```csharp
using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Collections;
using Unity.Entities;

var manager = store.World.EntityManager;
using var commands = new EntityCommandBuffer(Allocator.Temp);
var prototype = commands.CreateOwnedPrefab(manager, store, factoryRoot, factoryInstance, prototypeProductId, productDefinition, prepared);
var captured = RuntimeEntityFactoryEcbExtensions.CaptureOwnedPrefab(manager, prototype);

foreach (var productId in prewarmedProductIds)
{
    var product = commands.InstantiateOwnedEntity(manager, factoryRoot, factoryInstance, productId, captured);
    // Set the product's initial placement and state through its known contract.
}

commands.Playback(manager);
```

The graph-aware overloads take an `EntityManager` because graph cloning,
resource-lease retention and cleanup-signature preparation happen immediately.
Registration with the factory and activation complete at the supplied ECB
playback. Keep the World, factory and source prototype valid through that
boundary. Do not advance simulation or expose the factory as ready before it.

`CreateOwnedPrefab(...)` clones the loaded technical graph, detaches its Unity
scene ownership, applies the product definition's complete GRC projection, and
holds an independent native resource lease. It marks the resulting prototype
graph as prefab preparation data. Native scene streaming cannot independently
own the lifetime of the compiled graph.

`CaptureOwnedPrefab(...)` identifies the graph-aware path.
`InstantiateOwnedEntity(manager, ...)` clones all linked entities with Unity's
Entity-reference remapping, restores fresh cleanup signatures and retains a
separate lease. It removes only the temporary disablement introduced during
preparation; authored Disabled states remain intact.

The root and each product/prototype require different nonzero ProductIds;
factory root id zero remains reserved. Technical descendants belong to their
logical product root and do not receive separate GROs or factory ProductIds.
The compiled prototype is derived local preparation data, not another gameplay
object to advance or checkpoint. Actual products retain the normal persistent
`StoreId + FactoryId + ProductId` addressing contract.

The existing flat-prefab and archetype ECB paths remain available. Calling the
ECB-only instantiate overload with a captured baked graph is rejected rather
than silently dropping graph resource ownership.

## Product projection contract

The product GRC knows the template's components and reference layout. Projection
may add the product's declared components and configure known component values,
buffers and references. Systems subsequently operate on the resulting stable
signature.

Projection does not create or delete entities, replace/reorder the technical
graph, or change any `LinkedEntityGroup`. It does not mutate the source prefab,
another product, or shared prepared definition data. This API is not an arbitrary
graph merger. A different topology requires another authored technical template
and an explicit product contract.

Shared blob assets and native Unity resources remain owned by their resource
leases. Do not dispose source collider blobs or mutate shared source materials
from an instance. External resources bound by the product, such as its own mesh
or texture leases, retain their existing feature ownership; the native template
lease does not acquire those unrelated dependencies automatically.

## Retire topology before releasing resources

1. Stop new product creation, presentation consumers and simulation work; complete
   jobs that access the factory's graph.
2. Remove the owning factory GRO through its RuntimeStore and complete the
   established GRC editing ECB boundary. Its linked products/prototypes retire
   with the root, including their nested technical graphs.
3. Let technical cleanup owners finish and remove their cleanup components.
   `RuntimeBakedEntityGraphCleanupSystem` releases a graph's retained lease only
   when the graph's descendants and other root cleanup consumers are gone.
   `RuntimeBakedEntityFactory.ReleaseRetiredGraphs(manager)` is also available
   at an explicit cleanup boundary; it does not bypass pending cleanup work.
4. Dispose preparation-owner leases such as `prepared`. These are independent
   from the leases held by compiled prototypes and products.
5. Await `loader.RetireAsync()` while the World and required native loading/
   cleanup systems still exist. Remaining reservations keep retirement pending.
   Dispose the World only after consumers and resources have retired.

Repeated lease disposal is idempotent. Loader `Dispose()` starts retirement; use
the awaited `RetireAsync()` boundary when shutdown must be complete. Keep the
owning prepared GA catalog alive until its Stores and projections stop using it.

The ECS cleanup component carries only a process-local ownership token. Actual
leases and graph snapshots belong to the World-scoped cleanup system registry.
This keeps lifecycle bookkeeping out of managed ECS components. The token is
derived state and is neither a persistent product identity nor a network handle.

At restore, reconstruct technical topology and full product signatures before
applying persisted ECS rows. Entity handles, renderer bindings and technical
graph references are derived state rebuilt for the new World; authoritative
state uses the existing generated persistence and stable-product keys.

## Generated runtime contracts

Include `BakedEntityPrefab_GRC` in the product's existing runtime component
generation scope. Generate the component manifest first with
`RuntimeComponentTypeManifestGenerator.GenerateAndWrite(profile)`, then pass its
resulting manifest into the product's
`RuntimePatchSchemaGenerationCore.GenerateAndWrite(profile)` configuration.
Compile the generated C# before materializing the descriptor through the runtime
template cache. Regenerate from inputs; never hand-edit the manifest or codecs.

SDK example component namespaces remain excluded from product generation.
Patch generation uses only the supplied compiled component ledger and preserves
its numeric ids. Product authoring and content remain owned by the application;
the SDK does not generate example packages or assets in its consumer project.

`BakedEntityPrefab_GRC.PrefabGuid` uses the existing supported Hash128 codec.
The computed `EntityPrefabReference` property is not an additional serialized
state model. The native Unity prefab reference on the GAC retains its normal
Unity authoring representation.

Technical checks cover native descriptor serialization and immutable preparation,
derived library references, graph remapping and ownership rejection, and resource
release after root/descendant cleanup. Real baked-prefab loading and rendering
require an explicitly invoked integration fixture using the same production
preparation and ownership path.
