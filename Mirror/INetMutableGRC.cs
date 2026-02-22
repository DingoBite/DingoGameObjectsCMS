#if MIRROR
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Mirror;

namespace DingoGameObjectsCMS.Mirror
{
    public enum RuntimeMutateSide : byte
    {
        ServerAuthoritative = 0,
        ClientRemoteApply = 1,
    }

    public readonly struct RuntimeMutateApplied
    {
        public readonly long TargetId;
        public readonly uint CompTypeId;
        public readonly byte[] Payload;

        public RuntimeMutateApplied(long targetId, uint compTypeId, byte[] payload)
        {
            TargetId = targetId;
            CompTypeId = compTypeId;
            Payload = payload;
        }
    }

    public readonly struct RuntimeMutateContext
    {
        public readonly RuntimeStore Store;

        public readonly GameRuntimeObject Owner;
        public readonly long TargetId;
        public readonly uint CompTypeId;

        public readonly RuntimeMutateSide Side;
        public readonly NetworkConnectionToClient Connection;
        public readonly uint Revision;

        public bool IsServer => Side == RuntimeMutateSide.ServerAuthoritative;

        public RuntimeMutateContext(RuntimeStore store, GameRuntimeObject owner, long targetId, uint compTypeId, RuntimeMutateSide side, NetworkConnectionToClient connection = null, uint revision = 0)
        {
            Store = store;
            Owner = owner;
            TargetId = targetId;
            CompTypeId = compTypeId;
            Side = side;
            Connection = connection;
            Revision = revision;
        }

        public RuntimeMutateApplied MakeApplied(byte[] payload) => new(TargetId, CompTypeId, payload);
    }

    public interface INetMutableGRC
    {
        public void ApplyMutatePayload(in RuntimeMutateContext ctx, byte[] payload, List<RuntimeMutateApplied> outApplied);
    }
}
#endif