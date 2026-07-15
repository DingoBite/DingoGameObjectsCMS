using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeComponentProjectionChannel : byte
    {
        Spawn = 1,
        ReliableOverride = 2,
        UnreliableMotion = 3,
    }

    public readonly struct RuntimeGameAssetBaselineComponent
    {
        public readonly uint ComponentTypeId;
        public readonly string ComponentTypeKey;

        public RuntimeGameAssetBaselineComponent(uint componentTypeId, string componentTypeKey)
        {
            if (string.IsNullOrWhiteSpace(componentTypeKey))
                throw new ArgumentException("Runtime component type key is required.", nameof(componentTypeKey));

            ComponentTypeId = componentTypeId;
            ComponentTypeKey = componentTypeKey;
        }
    }

    public static class RuntimeComponentVisibilityProjection
    {
        public static bool IsVisible(
            RuntimeReplicationPolicyRegistry policies,
            uint componentTypeId,
            RuntimeComponentProjectionChannel channel)
        {
            if (policies == null)
                throw new ArgumentNullException(nameof(policies));
            if (channel != RuntimeComponentProjectionChannel.Spawn
                && channel != RuntimeComponentProjectionChannel.ReliableOverride
                && channel != RuntimeComponentProjectionChannel.UnreliableMotion)
            {
                throw new ArgumentOutOfRangeException(nameof(channel), channel, null);
            }

            var policy = policies.GetRequired(componentTypeId);
            switch (policy)
            {
                case RuntimeReplicationPolicy.Never:
                    return false;

                case RuntimeReplicationPolicy.BaselineAndReliableOverrides:
                    return channel == RuntimeComponentProjectionChannel.Spawn
                        || channel == RuntimeComponentProjectionChannel.ReliableOverride;

                case RuntimeReplicationPolicy.UnreliableState:
                    return channel == RuntimeComponentProjectionChannel.Spawn
                        || channel == RuntimeComponentProjectionChannel.UnreliableMotion;

                default:
                    throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
            }
        }
    }

    public static class RuntimeConnectionMotionEligibility
    {
        public static bool IsMotionEligible(
            RuntimeConnectionStoreReplicationState state,
            NetObjectRef value,
            uint componentTypeId,
            RuntimeReplicationPolicyRegistry policies)
        {
            if (state == null)
                throw new ArgumentNullException(nameof(state));
            if (!RuntimeComponentVisibilityProjection.IsVisible(
                    policies,
                    componentTypeId,
                    RuntimeComponentProjectionChannel.UnreliableMotion))
                return false;
            if (!value.IsValid || value.Store != state.Store)
                return false;
            // Object membership alone cannot prove that a newly added motion
            // component exists on the replica yet. Coarsely hold this store's
            // motion stream behind every outstanding reliable envelope; once
            // structural component ACKs become explicit this can be narrowed.
            if (state.PendingReliableCount != 0)
                return false;

            return state.IsProjected(value) && state.IsAcknowledged(value);
        }
    }

    public static class RuntimeSpawnPatchProjector
    {
        public static RuntimeObjectPatch Project(
            string runtimeSchemaHash,
            RuntimeReplicationPolicyRegistry policies,
            IReadOnlyList<RuntimeGameAssetBaselineComponent> gameAssetBaseline,
            RuntimeObjectPatch sourceOverrides)
        {
            if (string.IsNullOrWhiteSpace(runtimeSchemaHash))
                throw new ArgumentException("Runtime schema hash is required.", nameof(runtimeSchemaHash));
            if (policies == null)
                throw new ArgumentNullException(nameof(policies));
            if (gameAssetBaseline == null)
                throw new ArgumentNullException(nameof(gameAssetBaseline));
            if (sourceOverrides != null
                && !string.Equals(sourceOverrides.SchemaHash, runtimeSchemaHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runtime override schema hash '{sourceOverrides.SchemaHash}' does not match '{runtimeSchemaHash}'.");
            }

            var baselineById = BuildBaselineLookup(gameAssetBaseline);
            var sourceById = BuildSourceLookup(sourceOverrides);
            var componentTypeIds = new List<uint>(baselineById.Count + sourceById.Count);
            foreach (var pair in baselineById)
            {
                componentTypeIds.Add(pair.Key);
            }

            foreach (var pair in sourceById)
            {
                if (!baselineById.ContainsKey(pair.Key))
                    componentTypeIds.Add(pair.Key);
            }

            componentTypeIds.Sort();
            var result = new RuntimeObjectPatch(runtimeSchemaHash);
            for (var i = 0; i < componentTypeIds.Count; i++)
            {
                var componentTypeId = componentTypeIds[i];
                var hasBaseline = baselineById.TryGetValue(componentTypeId, out var baselineComponent);
                var visible = RuntimeComponentVisibilityProjection.IsVisible(
                    policies,
                    componentTypeId,
                    RuntimeComponentProjectionChannel.Spawn);
                if (!visible)
                {
                    if (hasBaseline)
                    {
                        result.Components.Add(new ComponentPatch(
                            componentTypeId,
                            baselineComponent.ComponentTypeKey,
                            ComponentPatchKind.Remove));
                    }

                    continue;
                }

                if (!sourceById.TryGetValue(componentTypeId, out var source))
                    continue;
                if (hasBaseline
                    && !string.Equals(source.ComponentTypeKey, baselineComponent.ComponentTypeKey, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Runtime override component id {componentTypeId} has key '{source.ComponentTypeKey}', " +
                        $"but the GameAsset baseline uses '{baselineComponent.ComponentTypeKey}'.");
                }

                result.Components.Add(Clone(source));
            }

            return result;
        }

        private static Dictionary<uint, RuntimeGameAssetBaselineComponent> BuildBaselineLookup(
            IReadOnlyList<RuntimeGameAssetBaselineComponent> values)
        {
            var result = new Dictionary<uint, RuntimeGameAssetBaselineComponent>(values.Count);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < values.Count; i++)
            {
                var value = values[i];
                if (string.IsNullOrWhiteSpace(value.ComponentTypeKey))
                    throw new InvalidOperationException($"GameAsset baseline component at index {i} is invalid.");
                if (!result.TryAdd(value.ComponentTypeId, value))
                    throw new InvalidOperationException($"GameAsset baseline contains duplicate component id {value.ComponentTypeId}.");
                if (!keys.Add(value.ComponentTypeKey))
                    throw new InvalidOperationException($"GameAsset baseline contains duplicate component key '{value.ComponentTypeKey}'.");
            }

            return result;
        }

        private static Dictionary<uint, ComponentPatch> BuildSourceLookup(RuntimeObjectPatch source)
        {
            var result = new Dictionary<uint, ComponentPatch>();
            if (source?.Components == null)
                return result;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < source.Components.Count; i++)
            {
                var value = source.Components[i];
                if (value == null)
                    throw new InvalidOperationException($"Runtime override component at index {i} is null.");
                if (string.IsNullOrWhiteSpace(value.ComponentTypeKey))
                    throw new InvalidOperationException($"Runtime override component at index {i} is invalid.");
                if (!result.TryAdd(value.ComponentTypeId, value))
                    throw new InvalidOperationException($"Runtime overrides contain duplicate component id {value.ComponentTypeId}.");
                if (!keys.Add(value.ComponentTypeKey))
                    throw new InvalidOperationException($"Runtime overrides contain duplicate component key '{value.ComponentTypeKey}'.");
            }

            return result;
        }

        private static ComponentPatch Clone(ComponentPatch source)
        {
            var result = new ComponentPatch(
                source.ComponentTypeId,
                source.ComponentTypeKey,
                source.Kind,
                Clone(source.Payload));
            if (source.Fields == null || source.Fields.Count == 0)
                return result;

            var fields = new List<FieldPatch>(source.Fields.Count);
            for (var i = 0; i < source.Fields.Count; i++)
            {
                var field = source.Fields[i];
                if (field == null)
                    throw new InvalidOperationException($"Runtime override component {source.ComponentTypeId} contains a null field patch.");

                fields.Add(new FieldPatch(field.FieldId, field.FieldKey, field.Kind, Clone(field.Payload)));
            }

            fields.Sort(CompareFields);
            result.Fields = fields;
            return result;
        }

        private static byte[] Clone(byte[] value)
        {
            return value == null ? null : (byte[])value.Clone();
        }

        private static int CompareFields(FieldPatch first, FieldPatch second)
        {
            var byId = first.FieldId.CompareTo(second.FieldId);
            return byId != 0 ? byId : string.CompareOrdinal(first.FieldKey, second.FieldKey);
        }
    }
}
