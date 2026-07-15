using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Fail-closed policy gate for patches received from protocol v2. This is
    /// deliberately independent from patch decoding: the binary codec proves
    /// shape and schema, while this gate proves that the patch kind belongs to
    /// the component's declared replication lane.
    /// </summary>
    public class RuntimeInboundNetworkPatchPolicyValidator
    {
        private readonly RuntimeReplicationPolicyRegistry _policies;

        public RuntimeInboundNetworkPatchPolicyValidator(RuntimeReplicationPolicyRegistry policies)
        {
            if (policies == null || !policies.IsSealed)
                throw new InvalidOperationException("Inbound network patch validation requires sealed replication policies.");
            _policies = policies;
        }

        public void ValidateSpawn(
            RuntimeObjectPatch patch,
            IReadOnlyCollection<uint> gameAssetBaselineComponentTypeIds)
        {
            if (gameAssetBaselineComponentTypeIds == null)
                throw new ArgumentNullException(nameof(gameAssetBaselineComponentTypeIds));

            var baseline = new HashSet<uint>(gameAssetBaselineComponentTypeIds);
            if (baseline.Count != gameAssetBaselineComponentTypeIds.Count)
                throw new InvalidOperationException("GameAsset baseline contains duplicate component type ids.");

            var patches = ValidateCommon(patch, isSpawn: true, baseline);
            foreach (var componentTypeId in baseline)
            {
                if (_policies.GetRequired(componentTypeId) != RuntimeReplicationPolicy.Never)
                    continue;
                if (!patches.TryGetValue(componentTypeId, out var componentPatch)
                    || componentPatch.Kind != ComponentPatchKind.Remove)
                {
                    throw new InvalidOperationException(
                        $"Inbound spawn must remove Never component {componentTypeId} from its GameAsset baseline.");
                }
            }
        }

        public void ValidateDelta(RuntimeObjectPatch patch)
        {
            ValidateCommon(patch, isSpawn: false, null);
        }

        private Dictionary<uint, ComponentPatch> ValidateCommon(
            RuntimeObjectPatch patch,
            bool isSpawn,
            HashSet<uint> baseline)
        {
            if (patch == null)
                throw new ArgumentNullException(nameof(patch));
            if (patch.Representation != RuntimeObjectPatchRepresentation.RuntimeBinary)
            {
                throw new InvalidOperationException(
                    $"Inbound network patch must use {RuntimeObjectPatchRepresentation.RuntimeBinary}, received {patch.Representation}.");
            }

            var components = patch.Components ?? throw new InvalidOperationException("Inbound network patch has null components.");
            var result = new Dictionary<uint, ComponentPatch>(components.Count);
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i]
                                ?? throw new InvalidOperationException($"Inbound network patch component at index {i} is null.");
                if (!result.TryAdd(component.ComponentTypeId, component))
                    throw new InvalidOperationException($"Inbound network patch contains duplicate component {component.ComponentTypeId}.");

                var policy = _policies.GetRequired(component.ComponentTypeId);
                switch (policy)
                {
                    case RuntimeReplicationPolicy.Never:
                        ValidateNever(component, isSpawn, baseline);
                        break;
                    case RuntimeReplicationPolicy.BaselineAndReliableOverrides:
                        ValidateReliable(component);
                        break;
                    case RuntimeReplicationPolicy.UnreliableState:
                        ValidateHot(component, isSpawn, baseline);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
                }
            }

            return result;
        }

        private static void ValidateNever(
            ComponentPatch component,
            bool isSpawn,
            HashSet<uint> baseline)
        {
            if (!isSpawn
                || component.Kind != ComponentPatchKind.Remove
                || baseline == null
                || !baseline.Contains(component.ComponentTypeId))
            {
                throw new InvalidOperationException(
                    $"Never component {component.ComponentTypeId} cannot carry inbound network state or presence. " +
                    "Only removal from an exact GameAsset spawn baseline is allowed.");
            }

            RequirePayloadless(component);
        }

        private static void ValidateReliable(ComponentPatch component)
        {
            if (component.Kind == ComponentPatchKind.AddPresence)
            {
                throw new InvalidOperationException(
                    $"Reliable component {component.ComponentTypeId} cannot use payloadless AddPresence.");
            }
            if (component.Kind != ComponentPatchKind.Add
                && component.Kind != ComponentPatchKind.Remove
                && component.Kind != ComponentPatchKind.Fields
                && component.Kind != ComponentPatchKind.Custom)
            {
                throw new InvalidOperationException(
                    $"Reliable component {component.ComponentTypeId} has unsupported inbound patch kind {component.Kind}.");
            }
            if (component.Kind == ComponentPatchKind.Remove)
                RequirePayloadless(component);
        }

        private static void ValidateHot(
            ComponentPatch component,
            bool isSpawn,
            HashSet<uint> baseline)
        {
            if (component.Kind != ComponentPatchKind.AddPresence
                && component.Kind != ComponentPatchKind.Remove)
            {
                throw new InvalidOperationException(
                    $"Hot component {component.ComponentTypeId} cannot use inbound {component.Kind}; " +
                    "hot state belongs exclusively to its typed packed state stream.");
            }

            RequirePayloadless(component);
            if (!isSpawn || baseline == null)
                return;

            var baselineContains = baseline.Contains(component.ComponentTypeId);
            if (component.Kind == ComponentPatchKind.AddPresence && baselineContains)
            {
                throw new InvalidOperationException(
                    $"Hot component {component.ComponentTypeId} AddPresence requires an absent GameAsset baseline component.");
            }
            if (component.Kind == ComponentPatchKind.Remove && !baselineContains)
            {
                throw new InvalidOperationException(
                    $"Hot component {component.ComponentTypeId} Remove requires a present GameAsset baseline component.");
            }
        }

        private static void RequirePayloadless(ComponentPatch component)
        {
            if (component.Payload != null
                || component.CanonicalJson != null
                || (component.Fields?.Count ?? 0) != 0)
            {
                throw new InvalidOperationException(
                    $"Inbound structural component patch {component.ComponentTypeId}/{component.Kind} must be payloadless.");
            }
        }
    }
}
