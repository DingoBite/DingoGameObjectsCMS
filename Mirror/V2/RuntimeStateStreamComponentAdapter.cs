using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Entities;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeStateStreamStopContribution : byte
    {
        Ignore = 0,
        Moving = 1,
        Stopped = 2,
    }

    public abstract class RuntimeStateStreamComponentAdapter<TEvaluationContext>
    {
        public readonly Type ComponentType;
        public readonly Type ProjectionType;

        protected RuntimeStateStreamComponentAdapter(Type componentType, Type projectionType)
        {
            if (componentType == null)
                throw new ArgumentNullException(nameof(componentType));
            if (componentType.IsAbstract || !typeof(GameRuntimeComponent).IsAssignableFrom(componentType))
                throw new ArgumentException($"State stream component type '{componentType.FullName}' is invalid.", nameof(componentType));
            if (projectionType == null || !typeof(IComponentData).IsAssignableFrom(projectionType))
                throw new ArgumentException($"State stream projection type '{projectionType?.FullName}' is invalid.", nameof(projectionType));
            ComponentType = componentType;
            ProjectionType = projectionType;
        }

        public abstract void ValidateState(GameRuntimeComponent value);
        public abstract RuntimeStateStreamStopContribution GetStopContribution(GameRuntimeComponent value);
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

    public abstract class RuntimeStateStreamComponentAdapter<TEvaluationContext, TComponent, TProjection>
        : RuntimeStateStreamComponentAdapter<TEvaluationContext>
        where TComponent : GameRuntimeComponent
        where TProjection : unmanaged, IComponentData
    {
        protected RuntimeStateStreamComponentAdapter() : base(typeof(TComponent), typeof(TProjection)) { }

        public override void ValidateState(GameRuntimeComponent value) => ValidateState(RequireTyped(value));

        public override RuntimeStateStreamStopContribution GetStopContribution(GameRuntimeComponent value)
        {
            return GetStopContribution(RequireTyped(value));
        }

        public override void ValidateTarget(EntityManager entityManager, Entity entity)
        {
            if (!entityManager.Exists(entity) || !entityManager.HasComponent<TProjection>(entity))
            {
                throw new InvalidOperationException(
                    $"Entity {entity} has no {typeof(TProjection).Name} projection for {typeof(TComponent).Name} state stream.");
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
        protected virtual RuntimeStateStreamStopContribution GetStopContribution(TComponent value)
        {
            return RuntimeStateStreamStopContribution.Ignore;
        }

        protected abstract bool TryInterpolate(
            RuntimeReplicaMotionComponentTimeline timeline,
            double targetTimeSeconds,
            in TEvaluationContext context,
            out TComponent result);

        protected abstract bool Apply(EntityManager entityManager, Entity entity, TComponent value);
        protected abstract void Bridge(
            EntityManager entityManager,
            Entity entity,
            RuntimeStore store,
            in RuntimeInstance instance,
            GameRuntimeObject runtimeObject);

        protected static void RequireFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new InvalidOperationException($"State stream field '{name}' must be finite.");
        }

        private static TComponent RequireTyped(GameRuntimeComponent value)
        {
            if (value is TComponent typed)
                return typed;
            throw new InvalidOperationException(
                $"State stream adapter expected {typeof(TComponent).FullName}, received {value?.GetType().FullName ?? "null"}.");
        }
    }
}
