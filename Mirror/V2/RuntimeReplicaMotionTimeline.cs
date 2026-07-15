using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using Unity.Entities;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public readonly struct RuntimeReplicaMotionPreparedComponent
    {
        public readonly uint ComponentTypeId;
        public readonly GameRuntimeComponent Component;

        public RuntimeReplicaMotionPreparedComponent(uint componentTypeId, GameRuntimeComponent component)
        {
            ComponentTypeId = componentTypeId;
            Component = component ?? throw new ArgumentNullException(nameof(component));
        }
    }

    public class RuntimeReplicaMotionPreparedSample
    {
        public readonly uint SimulationTick;
        public readonly double LocalSimulationTimeSeconds;
        public readonly IReadOnlyList<RuntimeReplicaMotionPreparedComponent> Components;

        public RuntimeReplicaMotionPreparedSample(
            uint simulationTick,
            double localSimulationTimeSeconds,
            IReadOnlyList<RuntimeReplicaMotionPreparedComponent> components)
        {
            RequireFinite(localSimulationTimeSeconds, nameof(localSimulationTimeSeconds));
            if (components == null)
                throw new ArgumentNullException(nameof(components));
            if (components.Count == 0 || components.Count > RuntimeStateStreamProtocol.MAX_COMPONENTS_PER_SAMPLE)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(components),
                    $"Prepared motion component count {components.Count} is outside 1..{RuntimeStateStreamProtocol.MAX_COMPONENTS_PER_SAMPLE}.");
            }

            var copy = new RuntimeReplicaMotionPreparedComponent[components.Count];
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component.Component == null)
                    throw new ArgumentException($"Prepared motion component at index {i} is null.", nameof(components));
                copy[i] = new RuntimeReplicaMotionPreparedComponent(component.ComponentTypeId, component.Component);
            }
            Array.Sort(copy, CompareComponents);
            for (var i = 1; i < copy.Length; i++)
            {
                if (copy[i - 1].ComponentTypeId == copy[i].ComponentTypeId)
                {
                    throw new ArgumentException(
                        $"Prepared motion sample contains duplicate component type id {copy[i].ComponentTypeId}.",
                        nameof(components));
                }
            }

            SimulationTick = simulationTick;
            LocalSimulationTimeSeconds = localSimulationTimeSeconds;
            Components = Array.AsReadOnly(copy);
        }

        private static int CompareComponents(
            RuntimeReplicaMotionPreparedComponent first,
            RuntimeReplicaMotionPreparedComponent second)
        {
            return first.ComponentTypeId.CompareTo(second.ComponentTypeId);
        }

        private static void RequireFinite(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName, "Prepared motion sample time must be finite.");
        }
    }

    public class RuntimeReplicaMotionComponentTimeline
    {
        public const int MAX_SAMPLES = 16;
        private const double TIME_EPSILON = 0.000000001d;

        private readonly List<RuntimeReplicaMotionComponentSample> _samples;
        private readonly Type _componentRuntimeType;

        public int Count => _samples.Count;

        private RuntimeReplicaMotionComponentTimeline(
            uint simulationTick,
            double localSimulationTimeSeconds,
            GameRuntimeComponent component)
        {
            _componentRuntimeType = component.GetType();
            _samples = new List<RuntimeReplicaMotionComponentSample>(MAX_SAMPLES)
            {
                new RuntimeReplicaMotionComponentSample(simulationTick, localSimulationTimeSeconds, component),
            };
        }

        private RuntimeReplicaMotionComponentTimeline(
            List<RuntimeReplicaMotionComponentSample> samples,
            Type componentRuntimeType)
        {
            _samples = samples;
            _componentRuntimeType = componentRuntimeType;
        }

        public bool TryTakeBracket<TComponent>(
            double targetTimeSeconds,
            out TComponent first,
            out TComponent second,
            out float alpha)
            where TComponent : GameRuntimeComponent
        {
            RequireFinite(targetTimeSeconds, nameof(targetTimeSeconds));
            if (!typeof(TComponent).IsAssignableFrom(_componentRuntimeType))
            {
                throw new InvalidOperationException(
                    $"Motion timeline stores '{_componentRuntimeType.FullName}', not '{typeof(TComponent).FullName}'.");
            }

            var firstIndex = -1;
            var secondIndex = -1;
            for (var i = 0; i < _samples.Count; i++)
            {
                var sample = _samples[i];
                if (sample.LocalSimulationTimeSeconds <= targetTimeSeconds + TIME_EPSILON)
                {
                    firstIndex = i;
                    continue;
                }

                secondIndex = i;
                break;
            }

            if (firstIndex < 0 && secondIndex < 0)
            {
                first = null;
                second = null;
                alpha = 0f;
                return false;
            }
            if (firstIndex < 0)
                firstIndex = secondIndex;
            if (secondIndex < 0)
                secondIndex = firstIndex;

            first = TakeTyped<TComponent>(_samples[firstIndex].Component);
            second = TakeTyped<TComponent>(_samples[secondIndex].Component);
            alpha = 0f;
            if (firstIndex == secondIndex)
                return true;

            var firstTime = _samples[firstIndex].LocalSimulationTimeSeconds;
            var secondTime = _samples[secondIndex].LocalSimulationTimeSeconds;
            var duration = secondTime - firstTime;
            alpha = duration <= TIME_EPSILON
                ? 1f
                : Saturate((float)((targetTimeSeconds - firstTime) / duration));
            return true;
        }

        internal static RuntimeReplicaMotionComponentTimeline Create(
            uint simulationTick,
            double localSimulationTimeSeconds,
            GameRuntimeComponent component)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(component));
            RequireFinite(localSimulationTimeSeconds, nameof(localSimulationTimeSeconds));
            return new RuntimeReplicaMotionComponentTimeline(simulationTick, localSimulationTimeSeconds, component);
        }

        internal RuntimeReplicaMotionComponentTimeline CloneAndAppend(
            uint simulationTick,
            double localSimulationTimeSeconds,
            GameRuntimeComponent component)
        {
            ValidateAppend(simulationTick, localSimulationTimeSeconds, component);
            var retainedCount = Math.Min(_samples.Count, MAX_SAMPLES - 1);
            var startIndex = _samples.Count - retainedCount;
            var nextSamples = new List<RuntimeReplicaMotionComponentSample>(MAX_SAMPLES);
            for (var i = startIndex; i < _samples.Count; i++)
            {
                nextSamples.Add(_samples[i]);
            }
            nextSamples.Add(new RuntimeReplicaMotionComponentSample(
                simulationTick,
                localSimulationTimeSeconds,
                component));
            return new RuntimeReplicaMotionComponentTimeline(nextSamples, _componentRuntimeType);
        }

        internal void ValidateAppend(
            uint simulationTick,
            double localSimulationTimeSeconds,
            GameRuntimeComponent component)
        {
            if (component == null)
                throw new ArgumentNullException(nameof(component));
            RequireFinite(localSimulationTimeSeconds, nameof(localSimulationTimeSeconds));
            if (component.GetType() != _componentRuntimeType)
            {
                throw new InvalidOperationException(
                    $"Motion component type changed from '{_componentRuntimeType.FullName}' to '{component.GetType().FullName}'.");
            }

            var last = _samples[_samples.Count - 1];
            if (!RuntimeStateStreamSequence.IsNewer(simulationTick, last.SimulationTick))
            {
                throw new InvalidOperationException(
                    $"Motion component tick {simulationTick} is not newer than {last.SimulationTick}.");
            }
            if (localSimulationTimeSeconds + TIME_EPSILON < last.LocalSimulationTimeSeconds)
            {
                throw new InvalidOperationException(
                    $"Motion component time {localSimulationTimeSeconds} precedes {last.LocalSimulationTimeSeconds}.");
            }
        }

        private static TComponent TakeTyped<TComponent>(GameRuntimeComponent component)
            where TComponent : GameRuntimeComponent
        {
            if (component is TComponent typed)
                return typed;
            throw new InvalidOperationException(
                $"Motion timeline contains '{component?.GetType().FullName ?? "null"}', not '{typeof(TComponent).FullName}'.");
        }

        private static float Saturate(float value)
        {
            if (value <= 0f)
                return 0f;
            return value >= 1f ? 1f : value;
        }

        private static void RequireFinite(double value, string parameterName)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName, "Motion timeline time must be finite.");
        }
    }

    public class RuntimeReplicaMotionTimeline : IComponentData
    {
        private const double TIME_EPSILON = 0.000000001d;

        private Dictionary<uint, RuntimeReplicaMotionComponentTimeline> _componentTimelines = new();
        private readonly HashSet<uint> _pendingBridgeIds = new();

        public uint LastAcceptedSimulationTick { get; private set; }
        public double LastLocalSimulationTimeSeconds { get; private set; }
        public byte HasAcceptedSimulationTick { get; private set; }
        public byte Stale { get; set; }
        public bool HasPendingBridges => _pendingBridgeIds.Count > 0;

        public void Commit(RuntimeReplicaMotionPreparedSample prepared)
        {
            ValidateCommit(prepared);

            var nextTimelines = new Dictionary<uint, RuntimeReplicaMotionComponentTimeline>(prepared.Components.Count);
            for (var i = 0; i < prepared.Components.Count; i++)
            {
                var preparedComponent = prepared.Components[i];
                RuntimeReplicaMotionComponentTimeline nextTimeline;
                if (_componentTimelines.TryGetValue(preparedComponent.ComponentTypeId, out var currentTimeline))
                {
                    nextTimeline = currentTimeline.CloneAndAppend(
                        prepared.SimulationTick,
                        prepared.LocalSimulationTimeSeconds,
                        preparedComponent.Component);
                }
                else
                {
                    nextTimeline = RuntimeReplicaMotionComponentTimeline.Create(
                        prepared.SimulationTick,
                        prepared.LocalSimulationTimeSeconds,
                        preparedComponent.Component);
                }
                nextTimelines.Add(preparedComponent.ComponentTypeId, nextTimeline);
            }

            _componentTimelines = nextTimelines;
            LastAcceptedSimulationTick = prepared.SimulationTick;
            LastLocalSimulationTimeSeconds = prepared.LocalSimulationTimeSeconds;
            HasAcceptedSimulationTick = 1;
            Stale = 0;
        }

        public bool TryGetComponentTimeline(
            uint componentTypeId,
            out RuntimeReplicaMotionComponentTimeline componentTimeline)
        {
            return _componentTimelines.TryGetValue(componentTypeId, out componentTimeline);
        }

        public void MarkBridgePending(uint componentTypeId)
        {
            _pendingBridgeIds.Add(componentTypeId);
        }

        public bool IsBridgePending(uint componentTypeId)
        {
            return _pendingBridgeIds.Contains(componentTypeId);
        }

        public void ClearBridgePending(uint componentTypeId)
        {
            _pendingBridgeIds.Remove(componentTypeId);
        }

        public void ClearPendingBridges()
        {
            _pendingBridgeIds.Clear();
        }

        private void ValidateCommit(RuntimeReplicaMotionPreparedSample prepared)
        {
            if (prepared == null)
                throw new ArgumentNullException(nameof(prepared));
            if (double.IsNaN(prepared.LocalSimulationTimeSeconds)
                || double.IsInfinity(prepared.LocalSimulationTimeSeconds))
            {
                throw new InvalidOperationException("Prepared motion sample time must be finite.");
            }
            if (prepared.Components == null
                || prepared.Components.Count == 0
                || prepared.Components.Count > RuntimeStateStreamProtocol.MAX_COMPONENTS_PER_SAMPLE)
            {
                throw new InvalidOperationException(
                    $"Prepared motion component count is outside 1..{RuntimeStateStreamProtocol.MAX_COMPONENTS_PER_SAMPLE}.");
            }
            if (HasAcceptedSimulationTick != 0)
            {
                if (!RuntimeStateStreamSequence.IsNewer(prepared.SimulationTick, LastAcceptedSimulationTick))
                {
                    throw new InvalidOperationException(
                        $"Motion tick {prepared.SimulationTick} is not newer than {LastAcceptedSimulationTick}.");
                }
                if (prepared.LocalSimulationTimeSeconds + TIME_EPSILON < LastLocalSimulationTimeSeconds)
                {
                    throw new InvalidOperationException(
                        $"Motion time {prepared.LocalSimulationTimeSeconds} precedes {LastLocalSimulationTimeSeconds}.");
                }
            }

            var componentTypeIds = new HashSet<uint>();
            for (var i = 0; i < prepared.Components.Count; i++)
            {
                var component = prepared.Components[i];
                if (component.Component == null)
                    throw new InvalidOperationException($"Prepared motion component at index {i} is null.");
                if (!componentTypeIds.Add(component.ComponentTypeId))
                {
                    throw new InvalidOperationException(
                        $"Prepared motion sample contains duplicate component type id {component.ComponentTypeId}.");
                }
                if (_componentTimelines.TryGetValue(component.ComponentTypeId, out var timeline))
                {
                    timeline.ValidateAppend(
                        prepared.SimulationTick,
                        prepared.LocalSimulationTimeSeconds,
                        component.Component);
                }
            }
        }
    }

    internal readonly struct RuntimeReplicaMotionComponentSample
    {
        public readonly uint SimulationTick;
        public readonly double LocalSimulationTimeSeconds;
        public readonly GameRuntimeComponent Component;

        public RuntimeReplicaMotionComponentSample(
            uint simulationTick,
            double localSimulationTimeSeconds,
            GameRuntimeComponent component)
        {
            SimulationTick = simulationTick;
            LocalSimulationTimeSeconds = localSimulationTimeSeconds;
            Component = component;
        }
    }
}
