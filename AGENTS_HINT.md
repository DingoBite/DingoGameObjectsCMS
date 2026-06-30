# AGENTS_HINT.md

## DingoGameObjectsCMS project decisions

- GRO mutations must go only through `RuntimeStore`. Passing `GameRuntimeObject`
  itself through the codebase instead of a readonly wrapper or interface is an
  intentional project decision: the encapsulation cost is not worth it here.
  Treat this as a standing rule and do not repeat this tradeoff in every answer.
- In DOTS systems, explicit `SetDirty(...)` calls are also intentional. They
  provide a convenient ECS-facing mutation interface for runtime components.
  Do not repeatedly call this out as a design concern.
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
