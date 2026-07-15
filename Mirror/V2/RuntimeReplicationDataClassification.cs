using System;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Semantic replication lanes. These describe mutation meaning, not the
    /// physical message that happens to carry it.
    /// </summary>
    public enum RuntimeReplicationDataClass : byte
    {
        StructuralReliable = 1,
        ReliableState = 2,
        HotUnreliable = 3,
    }

    public static class RuntimeReplicationDataClassification
    {
        public static bool TryClassifyState(
            RuntimeReplicationPolicy policy,
            out RuntimeReplicationDataClass dataClass)
        {
            switch (policy)
            {
                case RuntimeReplicationPolicy.BaselineAndReliableOverrides:
                    dataClass = RuntimeReplicationDataClass.ReliableState;
                    return true;
                case RuntimeReplicationPolicy.UnreliableState:
                    dataClass = RuntimeReplicationDataClass.HotUnreliable;
                    return true;
                case RuntimeReplicationPolicy.Never:
                    dataClass = default;
                    return false;
                default:
                    throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
            }
        }

        public static RuntimeReplicationDataClass ClassifyStructuralOperation(
            RuntimeStoreDeltaOperationKind operation)
        {
            switch (operation)
            {
                case RuntimeStoreDeltaOperationKind.Spawn:
                case RuntimeStoreDeltaOperationKind.Remove:
                case RuntimeStoreDeltaOperationKind.Reparent:
                case RuntimeStoreDeltaOperationKind.Move:
                    return RuntimeReplicationDataClass.StructuralReliable;
                case RuntimeStoreDeltaOperationKind.Patch:
                    throw new ArgumentException(
                        "A patch operation must be classified by its component patch kinds.",
                        nameof(operation));
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        public static RuntimeReplicationDataClass ClassifyComponentPatch(
            ComponentPatchKind patchKind,
            RuntimeReplicationPolicy policy)
        {
            if (policy != RuntimeReplicationPolicy.Never
                && policy != RuntimeReplicationPolicy.BaselineAndReliableOverrides
                && policy != RuntimeReplicationPolicy.UnreliableState)
            {
                throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
            }
            if (policy == RuntimeReplicationPolicy.Never)
            {
                throw new InvalidOperationException("A Never component cannot produce a network component patch.");
            }

            switch (patchKind)
            {
                case ComponentPatchKind.Add:
                    if (policy == RuntimeReplicationPolicy.UnreliableState)
                    {
                        throw new InvalidOperationException(
                            "A hot component add cannot carry canonical state; use AddPresence and its typed state stream.");
                    }
                    return RuntimeReplicationDataClass.StructuralReliable;
                case ComponentPatchKind.AddPresence:
                    if (policy != RuntimeReplicationPolicy.UnreliableState)
                    {
                        throw new InvalidOperationException(
                            "A payloadless presence add is reserved for hot components.");
                    }
                    return RuntimeReplicationDataClass.StructuralReliable;
                case ComponentPatchKind.Remove:
                    return RuntimeReplicationDataClass.StructuralReliable;
                case ComponentPatchKind.Fields:
                case ComponentPatchKind.Custom:
                    if (policy == RuntimeReplicationPolicy.UnreliableState)
                    {
                        throw new InvalidOperationException(
                            "Hot component fields use a typed state stream, not a semantic component patch.");
                    }
                    return RuntimeReplicationDataClass.ReliableState;
                case ComponentPatchKind.None:
                    throw new ArgumentException("An empty component patch has no replication data class.", nameof(patchKind));
                default:
                    throw new ArgumentOutOfRangeException(nameof(patchKind), patchKind, null);
            }
        }
    }
}
