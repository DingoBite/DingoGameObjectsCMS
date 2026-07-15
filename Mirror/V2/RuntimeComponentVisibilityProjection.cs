using System;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeComponentProjectionChannel : byte
    {
        Spawn = 1,
        ReliableOverride = 2,
        HotState = 3,
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
                && channel != RuntimeComponentProjectionChannel.HotState)
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
                    // Spawn visibility means structural presence only. Hot
                    // fields are exclusively owned by the typed state stream
                    // and must never be copied into RuntimeObjectPatch.
                    return channel == RuntimeComponentProjectionChannel.Spawn
                        || channel == RuntimeComponentProjectionChannel.HotState;

                default:
                    throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
            }
        }

        public static RuntimeComponentPatchProjectionMode GetPatchProjectionMode(
            RuntimeReplicationPolicyRegistry policies,
            uint componentTypeId)
        {
            if (policies == null || !policies.IsSealed)
                throw new InvalidOperationException("A sealed replication policy registry is required for network patch projection.");

            switch (policies.GetRequired(componentTypeId))
            {
                case RuntimeReplicationPolicy.Never:
                    return RuntimeComponentPatchProjectionMode.Excluded;
                case RuntimeReplicationPolicy.BaselineAndReliableOverrides:
                    return RuntimeComponentPatchProjectionMode.SemanticDiff;
                case RuntimeReplicationPolicy.UnreliableState:
                    return RuntimeComponentPatchProjectionMode.StructuralPresence;
                default:
                    throw new InvalidOperationException(
                        $"Component {componentTypeId} has no supported network patch projection mode.");
            }
        }
    }

    public static class RuntimeConnectionStateStreamEligibility
    {
        public static bool IsEligible(
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
                    RuntimeComponentProjectionChannel.HotState))
                return false;
            if (!value.IsValid || value.Store != state.Store)
                return false;
            // Object membership alone cannot prove that a newly added hot-state
            // component exists on the replica yet. Coarsely hold this store's
            // stream behind every outstanding reliable envelope; once
            // structural component ACKs become explicit this can be narrowed.
            if (state.PendingReliableCount != 0)
                return false;

            return state.IsProjected(value) && state.IsAcknowledged(value);
        }
    }

    public static class RuntimeSpawnPatchProjector
    {
        public static RuntimeObjectPatch Project(
            GameRuntimeObject runtimeObject,
            GameAssetTemplateCache templateCache,
            GameAssetLibraryLock assetLock,
            RuntimePatchCodecContext networkPatchContext,
            RuntimeReplicationPolicyRegistry policies)
        {
            if (runtimeObject == null)
                throw new ArgumentNullException(nameof(runtimeObject));
            if (templateCache == null)
                throw new ArgumentNullException(nameof(templateCache));
            if (assetLock == null)
                throw new ArgumentNullException(nameof(assetLock));
            if (networkPatchContext == null)
                throw new ArgumentNullException(nameof(networkPatchContext));
            if (policies == null || !policies.IsSealed)
                throw new InvalidOperationException("A sealed replication policy registry is required to project a network spawn.");

            return templateCache.BuildProjectedOverrides(
                runtimeObject,
                assetLock,
                networkPatchContext,
                componentTypeId => RuntimeComponentVisibilityProjection.GetPatchProjectionMode(
                    policies,
                    componentTypeId));
        }
    }
}
