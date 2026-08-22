using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror.Protocol
{
    [Serializable, Preserve]
    public sealed class NetworkOwner_GRC : GameRuntimeComponent, IStoreDataDirty
    {
        public int ConnectionId = -1;
    }
}
