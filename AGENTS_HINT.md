# AGENTS_HINT.md

## DingoGameObjectsCMS project decisions

- GRO mutations must go only through `RuntimeStore`. Passing `GameRuntimeObject`
  itself through the codebase instead of a readonly wrapper or interface is an
  intentional project decision: the encapsulation cost is not worth it here.
  Treat this as a standing rule and do not repeat this tradeoff in every answer.
- In DOTS systems, explicit `SetDirty(...)` calls are also intentional. They
  provide a convenient ECS-facing mutation interface for runtime components.
  Do not repeatedly call this out as a design concern.
- `TakeRW(...)` is write intent and schedules the object for dirty processing.
  Use it only when mutation is guaranteed. For conditional updates, take the
  object and component through `TryTakeRO(...)` / `TakeRO<T>()`, compare the
  current and next value first, mutate the captured component only after a real
  change is known, then call `SetDirty<T>(id)` exactly once. Never mutate a
  component obtained through the readonly access path without the matching
  explicit `SetDirty(...)` call.
- Consumers that need to reconcile results from several dirty streams must
  collect their stream-local changes and perform the combined reconciliation
  once from `RuntimeStore.DirtyPublishCompleted`. Mutations made by that
  callback belong to the next dirty publish version.
- `GameRuntimeObjectsDirtyCollection.CompareKeys(...)` is the canonical parent
  ordering customization point and must stay deterministic. The base hooks use
  incremental binary insertion. Existing subclasses may still override
  `SortKeys(...)`; overriding either ordering hook intentionally selects the
  compatible one-full-sort-per-publish fallback.
- `GameRuntimeComponent` instances are intentionally added to entities as
  managed objects. This has known CPU cost, and that cost is accepted for this
  project. Do not keep warning about it unless the user asks to revisit the
  performance model.
- Do not manually inspect, reconstruct, or edit
  `Assets/StreamingAssets/runtime_component_types.json` as a normal workflow.
  When runtime component types change, use the Unity menu helper instead:
  `Tools/Runtime Types/Generate Manifest` or
  `Tools/Runtime Types/Regenerate Manifest`. Treat the manifest as generated
  output unless the user explicitly asks to debug the manifest file itself.
