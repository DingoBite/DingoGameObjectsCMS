using System;
using System.Collections.Generic;
using System.Reflection;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Editor
{
    public class GameAssetInstanceEditorEnvironment
    {
        public readonly RuntimePatchCodecRegistry Registry;
        public readonly RuntimePatchCodecContext PatchContext;
        public readonly Func<GameAssetReference, GameAsset> ResolveAsset;

        public GameAssetInstanceEditorEnvironment(
            RuntimePatchCodecRegistry registry,
            RuntimePatchCodecContext patchContext,
            Func<GameAssetReference, GameAsset> resolveAsset)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            PatchContext = patchContext ?? throw new ArgumentNullException(nameof(patchContext));
            ResolveAsset = resolveAsset ?? throw new ArgumentNullException(nameof(resolveAsset));
        }
    }

    public static class GameAssetInstanceAuthoring
    {
        public static void Materialize(
            GameAssetInstance instance,
            GameAssetInstanceEditorEnvironment environment,
            out GameAsset resolvedAsset,
            out Dictionary<uint, GameRuntimeComponent> baseline,
            out Dictionary<uint, GameRuntimeComponent> current)
        {
            if (environment == null)
                throw new ArgumentNullException(nameof(environment));

            resolvedAsset = environment.ResolveAsset(instance.Asset)
                ?? throw new InvalidOperationException($"Cannot resolve authored GameAsset '{instance.Asset.RequestedKey}'.");
            baseline = MaterializeComponents(resolvedAsset);
            if (instance.Patch == null)
            {
                current = CloneComponents(baseline, environment.Registry);
                return;
            }
            if (instance.Patch.Representation != RuntimeObjectPatchRepresentation.AuthoringCanonicalJson)
            {
                throw new InvalidOperationException(
                    $"Authored GameAsset instance '{instance.InstanceGuid}' contains {instance.Patch.Representation}; "
                    + $"only {RuntimeObjectPatchRepresentation.AuthoringCanonicalJson} is valid in project assets.");
            }

            var runtimePatch = new RuntimeObjectPatchAuthoringCodec(environment.Registry)
                .MaterializeRuntimePatch(baseline, instance.Patch, environment.PatchContext);
            current = new RuntimeObjectPatchEngine(environment.Registry, environment.PatchContext)
                .ApplyPatch(baseline, runtimePatch);
        }

        public static Dictionary<uint, GameRuntimeComponent> MaterializeComponents(GameAsset asset)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            var runtimeObject = new GameRuntimeObject();
            asset.SetupRuntimeObject(runtimeObject);
            return TakeComponents(runtimeObject);
        }

        public static Dictionary<uint, GameRuntimeComponent> TakeComponents(GameRuntimeObject runtimeObject)
        {
            if (runtimeObject == null)
                throw new ArgumentNullException(nameof(runtimeObject));

            var result = new Dictionary<uint, GameRuntimeComponent>();
            var components = runtimeObject.Components;
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i]
                    ?? throw new InvalidOperationException($"Runtime object contains a null component at index {i}.");
                if (!RuntimeComponentTypeRegistry.TryGetId(component.GetType(), out var typeId))
                    throw new InvalidOperationException($"Runtime component '{component.GetType().FullName}' is absent from the generated manifest.");
                if (!result.TryAdd(typeId, component))
                    throw new InvalidOperationException($"Runtime object contains duplicate component type id {typeId}.");
            }

            return result;
        }

        public static Dictionary<uint, GameRuntimeComponent> CloneComponents(
            IReadOnlyDictionary<uint, GameRuntimeComponent> source,
            RuntimePatchCodecRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));

            var result = new Dictionary<uint, GameRuntimeComponent>();
            if (source == null)
                return result;

            foreach (var pair in source)
            {
                result.Add(pair.Key, registry.Get(pair.Key).Clone(pair.Value));
            }

            return result;
        }

        public static void RebuildPatch(
            ref GameAssetInstance instance,
            RuntimePatchCodecRegistry registry,
            RuntimePatchCodecContext patchContext,
            IReadOnlyDictionary<uint, GameRuntimeComponent> baseline,
            IReadOnlyDictionary<uint, GameRuntimeComponent> current)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (patchContext == null)
                throw new ArgumentNullException(nameof(patchContext));

            var patch = new RuntimeObjectPatchAuthoringCodec(registry)
                .BuildAuthoringPatch(baseline, current, patchContext);
            instance.Patch = patch.IsEmpty ? null : patch;
        }

        public static RuntimeObjectPatch ClonePatch(RuntimeObjectPatch patch)
        {
            if (patch == null)
                return null;

            return RuntimeObjectPatchAuthoringCodec.ClonePatch(patch);
        }
    }

    public enum GameAssetInstanceComponentOverrideState : byte
    {
        Inherited = 0,
        Overridden = 1,
        Added = 2,
        Removed = 3,
    }

    public class GameAssetInstanceOverrideHost
    {
        private readonly List<uint> _componentTypeIds = new();
        private GameAssetInstanceEditorEnvironment _environment;
        private Action<GameAssetInstance> _commit;
        private Dictionary<uint, GameRuntimeComponent> _baseline;
        private Dictionary<uint, GameRuntimeComponent> _current;

        public GameAssetInstance Value { get; private set; }
        public GameAsset ResolvedAsset { get; private set; }
        public RuntimePatchCodecRegistry Registry => _environment?.Registry;
        public RuntimePatchCodecContext PatchContext => _environment?.PatchContext;
        public IReadOnlyDictionary<uint, GameRuntimeComponent> Baseline => _baseline;
        public IReadOnlyDictionary<uint, GameRuntimeComponent> Current => _current;
        public IReadOnlyList<uint> ComponentTypeIds => _componentTypeIds;

        public void Bind(
            GameAssetInstance instance,
            GameAssetInstanceEditorEnvironment environment,
            Action<GameAssetInstance> commit)
        {
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
            _commit = commit ?? throw new ArgumentNullException(nameof(commit));
            Value = instance;
            GameAssetInstanceAuthoring.Materialize(
                instance,
                environment,
                out var resolvedAsset,
                out _baseline,
                out _current);
            ResolvedAsset = resolvedAsset;
            RefreshComponentTypeIds();
        }

        public bool TryTake(
            uint typeId,
            out GameRuntimeComponent baseline,
            out GameRuntimeComponent current)
        {
            baseline = null;
            current = null;
            _baseline?.TryGetValue(typeId, out baseline);
            _current?.TryGetValue(typeId, out current);
            return baseline != null || current != null;
        }

        public ComponentPatch BuildComponentPatch(uint typeId)
        {
            if (!TryTake(typeId, out var baseline, out var current))
                return null;
            return Registry.Get(typeId).BuildPatch(baseline, current, PatchContext);
        }

        public GameAssetInstanceComponentOverrideState GetComponentState(uint typeId)
        {
            TryTake(typeId, out var baseline, out var current);
            if (current == null)
                return GameAssetInstanceComponentOverrideState.Removed;
            if (baseline == null)
                return GameAssetInstanceComponentOverrideState.Added;
            return Registry.Get(typeId).BuildPatch(baseline, current, PatchContext) == null
                ? GameAssetInstanceComponentOverrideState.Inherited
                : GameAssetInstanceComponentOverrideState.Overridden;
        }

        public void RevertComponent(uint typeId)
        {
            if (!_baseline.TryGetValue(typeId, out var baseline))
            {
                _current.Remove(typeId);
            }
            else
            {
                _current[typeId] = Registry.Get(typeId).Clone(baseline);
            }

            RefreshComponentTypeIds();
            Commit();
        }

        public void RemoveComponent(uint typeId)
        {
            _current.Remove(typeId);
            RefreshComponentTypeIds();
            Commit();
        }

        public void AddComponent(uint typeId, GameRuntimeComponent component)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(component));
            if (_current.ContainsKey(typeId))
                throw new InvalidOperationException($"Runtime component type id {typeId} is already present in the authored instance.");

            _current.Add(typeId, component);
            RefreshComponentTypeIds();
            Commit();
        }

        public void CommitComponent(uint typeId, GameRuntimeComponent component)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(component));
            if (!_current.ContainsKey(typeId))
                throw new InvalidOperationException($"Runtime component type id {typeId} is absent from the authored instance.");

            _current[typeId] = component;
            Commit();
        }

        public void RevertField(uint typeId, FieldInfo field)
        {
            if (field == null)
                throw new ArgumentNullException(nameof(field));
            if (!_baseline.TryGetValue(typeId, out var baseline)
                || !_current.TryGetValue(typeId, out var current))
            {
                throw new InvalidOperationException($"Field '{field.Name}' requires both baseline and current component {typeId}.");
            }

            var baselineClone = Registry.Get(typeId).Clone(baseline);
            field.SetValue(current, field.GetValue(baselineClone));
            _current[typeId] = current;
            Commit();
        }

        private void Commit()
        {
            var instance = Value;
            GameAssetInstanceAuthoring.RebuildPatch(
                ref instance,
                Registry,
                PatchContext,
                _baseline,
                _current);
            Value = instance;
            _commit(instance);
        }

        private void RefreshComponentTypeIds()
        {
            _componentTypeIds.Clear();
            var ids = new HashSet<uint>();
            if (_baseline != null)
            {
                foreach (var pair in _baseline)
                {
                    ids.Add(pair.Key);
                }
            }
            if (_current != null)
            {
                foreach (var pair in _current)
                {
                    ids.Add(pair.Key);
                }
            }

            _componentTypeIds.AddRange(ids);
            _componentTypeIds.Sort();
        }
    }
}
