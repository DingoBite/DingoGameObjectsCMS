using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Entities;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeMotionStopContribution : byte
    {
        Ignore = 0,
        Moving = 1,
        Stopped = 2,
    }

    public abstract class RuntimeMotionAdapter<TEvaluationContext>
    {
        public readonly Type ComponentType;
        public readonly Type ProjectionType;

        protected RuntimeMotionAdapter(Type componentType, Type projectionType)
        {
            if (componentType == null)
                throw new ArgumentNullException(nameof(componentType));
            if (componentType.IsAbstract || !typeof(GameRuntimeComponent).IsAssignableFrom(componentType))
            {
                throw new ArgumentException(
                    $"Motion adapter type '{componentType.FullName}' must be a concrete {nameof(GameRuntimeComponent)}.",
                    nameof(componentType));
            }
            if (projectionType == null)
                throw new ArgumentNullException(nameof(projectionType));
            if (!typeof(IComponentData).IsAssignableFrom(projectionType))
            {
                throw new ArgumentException(
                    $"Motion projection type '{projectionType.FullName}' must implement {nameof(IComponentData)}.",
                    nameof(projectionType));
            }

            ComponentType = componentType;
            ProjectionType = projectionType;
        }

        public abstract GameRuntimeComponent TakeComponent(GameRuntimeObject runtimeObject);
        public abstract void ValidateState(GameRuntimeComponent value);
        public abstract RuntimeMotionStopContribution GetStopContribution(GameRuntimeComponent value);
        public abstract void ValidateTarget(EntityManager entityManager, Entity entity);
        public abstract bool TryInterpolateAndApply(
            RuntimeReplicaMotionComponentTimeline timeline,
            double targetTimeSeconds,
            in TEvaluationContext context,
            EntityManager entityManager,
            Entity entity);
        public abstract void BridgeToRuntime(
            EntityManager entityManager,
            Entity entity,
            RuntimeStore store,
            in RuntimeInstance instance,
            GameRuntimeObject runtimeObject);
    }

    public abstract class RuntimeMotionAdapter<TEvaluationContext, TComponent, TProjection>
        : RuntimeMotionAdapter<TEvaluationContext>
        where TComponent : GameRuntimeComponent
        where TProjection : unmanaged, IComponentData
    {
        protected RuntimeMotionAdapter() : base(typeof(TComponent), typeof(TProjection)) { }

        public override GameRuntimeComponent TakeComponent(GameRuntimeObject runtimeObject)
        {
            if (runtimeObject == null)
                throw new ArgumentNullException(nameof(runtimeObject));
            return runtimeObject.TakeRO<TComponent>();
        }

        public override void ValidateState(GameRuntimeComponent value)
        {
            ValidateState(RequireTyped(value));
        }

        public override RuntimeMotionStopContribution GetStopContribution(GameRuntimeComponent value)
        {
            return GetStopContribution(RequireTyped(value));
        }

        public override void ValidateTarget(EntityManager entityManager, Entity entity)
        {
            if (!entityManager.Exists(entity) || !entityManager.HasComponent<TProjection>(entity))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} has no {typeof(TProjection).Name} projection for {typeof(TComponent).Name} replica motion.");
            }
        }

        public override bool TryInterpolateAndApply(
            RuntimeReplicaMotionComponentTimeline timeline,
            double targetTimeSeconds,
            in TEvaluationContext context,
            EntityManager entityManager,
            Entity entity)
        {
            if (!TryInterpolate(timeline, targetTimeSeconds, context, out var interpolated))
                return false;
            ValidateState(interpolated);
            return Apply(entityManager, entity, interpolated);
        }

        public override void BridgeToRuntime(
            EntityManager entityManager,
            Entity entity,
            RuntimeStore store,
            in RuntimeInstance instance,
            GameRuntimeObject runtimeObject)
        {
            Bridge(entityManager, entity, store, instance, runtimeObject);
        }

        protected virtual void ValidateState(TComponent value) { }

        protected virtual RuntimeMotionStopContribution GetStopContribution(TComponent value)
        {
            return RuntimeMotionStopContribution.Ignore;
        }

        protected abstract bool TryInterpolate(
            RuntimeReplicaMotionComponentTimeline timeline,
            double targetTimeSeconds,
            in TEvaluationContext context,
            out TComponent result);

        protected abstract bool Apply(
            EntityManager entityManager,
            Entity entity,
            TComponent value);

        protected abstract void Bridge(
            EntityManager entityManager,
            Entity entity,
            RuntimeStore store,
            in RuntimeInstance instance,
            GameRuntimeObject runtimeObject);

        protected static void RequireFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException($"Motion field '{name}' must be finite.");
        }

        private static TComponent RequireTyped(GameRuntimeComponent value)
        {
            if (value is TComponent typed)
                return typed;
            throw new InvalidOperationException(
                $"Motion adapter expected {typeof(TComponent).FullName}, received {value?.GetType().FullName ?? "null"}.");
        }
    }

    public class RuntimeMotionProfile<TEvaluationContext>
    {
        private readonly RuntimeMotionAdapterBinding[] _bindings;
        private readonly IReadOnlyList<uint> _componentTypeIds;
        private readonly Dictionary<uint, RuntimeMotionAdapterBinding> _bindingByComponentTypeId = new();

        public readonly string CoverageName;

        public IReadOnlyList<uint> ComponentTypeIds => _componentTypeIds;

        public RuntimeMotionProfile(
            RuntimePatchCodecRegistry patchCodecs,
            RuntimeReplicationPolicyRegistry policies,
            IReadOnlyList<RuntimeMotionAdapter<TEvaluationContext>> adapters,
            string coverageName)
        {
            if (patchCodecs == null)
                throw new ArgumentNullException(nameof(patchCodecs));
            if (policies == null || !policies.IsSealed)
                throw new InvalidOperationException("Motion profile requires sealed replication policies.");
            if (adapters == null)
                throw new ArgumentNullException(nameof(adapters));
            if (string.IsNullOrWhiteSpace(coverageName))
                throw new ArgumentException("Motion profile coverage name is required.", nameof(coverageName));

            CoverageName = coverageName;
            _bindings = new RuntimeMotionAdapterBinding[adapters.Count];
            for (var i = 0; i < adapters.Count; i++)
            {
                var adapter = adapters[i] ?? throw new ArgumentException(
                    $"Motion adapter at index {i} is null.",
                    nameof(adapters));
                var componentTypeId = adapter.ComponentType.GetId();
                var codec = patchCodecs.Get(componentTypeId);
                if (codec.ComponentRuntimeType != adapter.ComponentType)
                {
                    throw new InvalidOperationException(
                        $"Motion adapter '{adapter.ComponentType.FullName}' does not match generated codec '{codec.ComponentRuntimeType.FullName}'.");
                }

                var binding = new RuntimeMotionAdapterBinding(componentTypeId, adapter, codec);
                if (!_bindingByComponentTypeId.TryAdd(componentTypeId, binding))
                {
                    throw new InvalidOperationException(
                        $"Motion component '{adapter.ComponentType.FullName}' with type id {componentTypeId} is registered twice in {coverageName}.");
                }
                _bindings[i] = binding;
            }
            Array.Sort(_bindings, (first, second) => first.ComponentTypeId.CompareTo(second.ComponentTypeId));

            var componentTypeIds = new uint[_bindings.Length];
            for (var i = 0; i < _bindings.Length; i++)
            {
                componentTypeIds[i] = _bindings[i].ComponentTypeId;
            }
            _componentTypeIds = Array.AsReadOnly(componentTypeIds);
            policies.ValidateExactCoverage(
                RuntimeReplicationPolicy.UnreliableState,
                componentTypeIds,
                coverageName);
        }

        public static IReadOnlyList<Type> CreateAdapterComponentTypes(
            IReadOnlyList<RuntimeMotionAdapter<TEvaluationContext>> adapters,
            string coverageName)
        {
            if (adapters == null)
                throw new ArgumentNullException(nameof(adapters));
            if (string.IsNullOrWhiteSpace(coverageName))
                throw new ArgumentException("Motion profile coverage name is required.", nameof(coverageName));

            var result = new Type[adapters.Count];
            var types = new HashSet<Type>();
            for (var i = 0; i < adapters.Count; i++)
            {
                var adapter = adapters[i] ?? throw new ArgumentException(
                    $"Motion adapter at index {i} is null.",
                    nameof(adapters));
                if (!types.Add(adapter.ComponentType))
                {
                    throw new InvalidOperationException(
                        $"{coverageName} contains duplicate type '{adapter.ComponentType.FullName}'.");
                }
                result[i] = adapter.ComponentType;
            }
            return Array.AsReadOnly(result);
        }

        public static uint[] ResolveAdapterComponentTypeIds(
            IReadOnlyList<RuntimeMotionAdapter<TEvaluationContext>> adapters,
            string coverageName)
        {
            var componentTypes = CreateAdapterComponentTypes(adapters, coverageName);
            var result = new uint[componentTypes.Count];
            for (var i = 0; i < componentTypes.Count; i++)
            {
                result[i] = componentTypes[i].GetId();
            }
            Array.Sort(result);
            return result;
        }

        public bool IsRegisteredComponent(uint componentTypeId)
        {
            return _bindingByComponentTypeId.ContainsKey(componentTypeId);
        }

        public bool TryCapture(
            NetObjectRef value,
            GameRuntimeObject runtimeObject,
            out RuntimeMotionSample sample)
        {
            if (runtimeObject == null)
                throw new ArgumentNullException(nameof(runtimeObject));

            var states = new List<RuntimeMotionComponentState>(_bindings.Length);
            var hasMotionDriver = false;
            var stopped = true;
            for (var i = 0; i < _bindings.Length; i++)
            {
                var binding = _bindings[i];
                var component = binding.Adapter.TakeComponent(runtimeObject);
                if (component == null)
                    continue;

                binding.Adapter.ValidateState(component);
                states.Add(new RuntimeMotionComponentState(
                    binding.ComponentTypeId,
                    binding.Codec.EncodeCanonical(component, RuntimeMotionPatchCodecContext.Instance)));
                var contribution = binding.Adapter.GetStopContribution(component);
                if (contribution == RuntimeMotionStopContribution.Ignore)
                    continue;
                hasMotionDriver = true;
                stopped &= contribution == RuntimeMotionStopContribution.Stopped;
            }

            if (states.Count == 0)
            {
                sample = default;
                return false;
            }

            var flags = hasMotionDriver && stopped
                ? RuntimeMotionSampleFlags.Stop
                : RuntimeMotionSampleFlags.None;
            sample = new RuntimeMotionSample(value, flags, states);
            return true;
        }

        public RuntimeReplicaMotionPreparedSample PrepareReplicaSample(
            RuntimeMotionSample source,
            EntityManager entityManager,
            Entity entity,
            uint simulationTick,
            double localSimulationTimeSeconds)
        {
            if (source.Components == null)
                throw new ArgumentException("Motion sample has no component state.", nameof(source));

            var components = new RuntimeReplicaMotionPreparedComponent[source.Components.Count];
            for (var i = 0; i < source.Components.Count; i++)
            {
                var state = source.Components[i];
                if (!_bindingByComponentTypeId.TryGetValue(state.ComponentTypeId, out var binding))
                {
                    throw new InvalidOperationException(
                        $"Motion component type id {state.ComponentTypeId} has no adapter in {CoverageName}.");
                }

                binding.Adapter.ValidateTarget(entityManager, entity);
                var decoded = binding.Codec.DecodeCanonical(
                    state.CanonicalState,
                    RuntimeMotionPatchCodecContext.Instance);
                binding.Adapter.ValidateState(decoded);
                components[i] = new RuntimeReplicaMotionPreparedComponent(state.ComponentTypeId, decoded);
            }

            return new RuntimeReplicaMotionPreparedSample(
                simulationTick,
                localSimulationTimeSeconds,
                components);
        }

        public void EvaluateAndApply(
            RuntimeReplicaMotionTimeline timeline,
            double targetTimeSeconds,
            in TEvaluationContext context,
            EntityManager entityManager,
            Entity entity)
        {
            if (timeline == null)
                throw new ArgumentNullException(nameof(timeline));

            for (var i = 0; i < _bindings.Length; i++)
            {
                var binding = _bindings[i];
                if (timeline.TryGetComponentTimeline(binding.ComponentTypeId, out var componentTimeline)
                    && binding.Adapter.TryInterpolateAndApply(
                        componentTimeline,
                        targetTimeSeconds,
                        context,
                        entityManager,
                        entity))
                {
                    timeline.MarkBridgePending(binding.ComponentTypeId);
                }
            }
        }

        public void BridgeToRuntime(
            RuntimeReplicaMotionTimeline timeline,
            EntityManager entityManager,
            Entity entity,
            RuntimeStore store,
            in RuntimeInstance instance,
            GameRuntimeObject runtimeObject)
        {
            if (timeline == null)
                throw new ArgumentNullException(nameof(timeline));

            for (var i = 0; i < _bindings.Length; i++)
            {
                var binding = _bindings[i];
                if (!timeline.IsBridgePending(binding.ComponentTypeId))
                    continue;

                binding.Adapter.BridgeToRuntime(entityManager, entity, store, instance, runtimeObject);
                timeline.ClearBridgePending(binding.ComponentTypeId);
            }
        }

        private readonly struct RuntimeMotionAdapterBinding
        {
            public readonly uint ComponentTypeId;
            public readonly RuntimeMotionAdapter<TEvaluationContext> Adapter;
            public readonly RuntimeComponentPatchCodec Codec;

            public RuntimeMotionAdapterBinding(
                uint componentTypeId,
                RuntimeMotionAdapter<TEvaluationContext> adapter,
                RuntimeComponentPatchCodec codec)
            {
                ComponentTypeId = componentTypeId;
                Adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
                Codec = codec ?? throw new ArgumentNullException(nameof(codec));
            }
        }
    }
}
