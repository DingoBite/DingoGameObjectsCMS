# GameObject → GameAsset authoring

## Native prefab definitions

`GameAssetAuthoring` defines an asset directly on a prefab asset root or variant. It contains manual GACs through `SerializeReference` and component/converter bindings. A converter reads a component on the same GameObject and returns GACs; it never creates a GRO or changes a Store. The collector rejects missing inputs, incompatible sources, repeated sources and duplicate exact GAC types, and sorts successful output by SetupOrder and full type name. Its result includes the source of every GAC.

`PrefabGameAssetIndex` is a derived `SingletonScriptableObject` containing direct root-authoring references and prefab GUIDs. A product supplies its Editor index path and inclusion filter; the SDK has no product paths. The Editor reconciles saved prefab changes and runs a complete prebuild validation. Renaming or moving a prefab preserves its GUID, while copies and variants have their own definitions. Nested prefab instances retain their source definition rather than defining additional assets in their parent.

`PrefabGameAssetReference` is the native Unity reference to a prefab definition. Its drawer accepts prefab assets and variants and rejects scene objects. Explicit `PrefabGameAssetDefinitions.TryPrepare(...)` builds a caller-owned set of temporary `GameAsset` SOs, resolves native references into existing `GameAssetReference` keys, and retrieves prepared definitions. The automatic key is `_p_unity/prefab/<prefab GUID>/0.0.0`; the GA GUID is the prefab GUID. Preparation is atomic. Unity SO cloning separates serializable GAC data from the authoring source while retaining Unity resource references. Call `Dispose()` after all consumers stop; do not mutate prepared shared definitions. Changes to authoring do not update an already prepared set.

The Inspector previews only definition data, without invoking GameObject lifecycle, materializing a GRO or touching a Store. Scene prefab instances may override values, but cannot change manual GAC types/counts, converter/source bindings or the final GAC type set. Instance source resolution and effective-value collection are Editor-only in phase 1; no hidden runtime prefab identity is serialized on the component.

Build validation covers configured production prefab and scene assets, including scenes outside Build Settings, using isolated Editor loading without saving them. Invalid native definitions or structural overrides block a build. This is the explicit native-authoring contract; the existing CMS content fallback remains unchanged for the legacy contour.

Native definitions are not yet mounted into `LibraryLock`, exported as CMS documents or used by existing gameplay consumers. Library integration, product converter mappings, runtime instance overrides and legacy migration are subsequent work.

## Existing document-producing authoring

This folder defines the reusable Unity-side source for a generated DingoCMS `GameAsset`. A `GameAssetObjectAuthoring` component marks one prefab or scene object as the owner of a GA definition. Any `MonoBehaviour` in its owned hierarchy may implement `IGameAssetComponentAuthoring` and return one `GameAssetComponent`. The collector excludes nested authoring roots, rejects duplicate GAC types, and orders the result deterministically.

The scene or prefab is the authoring source; the generated GA document is the compiled content. The Inspector's **Create GA** action chooses a `GameAssetKey` and optional JSON path inside a mounted DingoCMS module. The EditorServer persists the document through its transactional CMS writer and records the returned GUID. Later source changes queue an editor-side bake of the same key and GUID. Each bake replaces the generated GA's full GAC list with the contributors under its authoring root; edits made directly to that list in CMS are not retained. `OnValidate` only signals that work; it does not write assets or call the Unity asset database. A changed GA becomes visible to runtime only in a newly prepared content session.

The component also exposes **Refresh**, **Reset**, and **Delete** buttons. Refresh forces a bake from the current authoring components into the linked GA; a stale document hash blocks an overwrite. Reset clears only this component's local GUID/hash link and leaves the CMS document in place. Delete asks for confirmation, removes the matching CMS document through a transaction, then clears the local link. Removing a scene object or prefab does not automatically delete its GA.

Only one authoring root owns a generated GA. Prefab instances placed in scenes should share their prefab's definition and express placement-specific values separately, for example through a `GameAssetInstance` patch or a feature-specific placement descriptor. A copied standalone root that needs an independent GA must explicitly create a new one. Scene-only Unity object references and mutable runtime state do not belong in generated GACs.

The saved authoring link includes the Unity source object's `GlobalObjectId`. Bake and Delete require that the current source still has the same ID, so duplicating or moving a root does not silently publish over the original GA. Create requires a saved scene or prefab with a stable source ID.

The CMS module package is the canonical output. The file is JSON below the mounted `Application.persistentDataPath/assets/<module>` library, not a Unity `.asset`. A product that ships a packaged base module must publish the generated GA into that package before a build; saving the mounted library alone does not change an existing packaged copy.

Reset and removed authoring objects can leave generated documents without owners. Such documents must not be deleted by a blind GA sweep because manually authored GA can share the module. An eventual orphan audit should compare an explicit registry of generated GA GUIDs with authoring owners across relevant scenes and prefabs, show candidates for review, and delete selected documents only through CMS after an identity check.

Runtime direction remains unchanged: a feature model creates or restores a GRO/GRC in `RuntimeStore`, and presentation binds to the runtime object through dirty streams. Authoring components never mutate the runtime store directly.
