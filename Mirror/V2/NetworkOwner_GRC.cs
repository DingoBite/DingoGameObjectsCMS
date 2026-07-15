using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Server-only authority metadata. It is intentionally never projected to
    /// ECS or replicated to clients.
    /// </summary>
    [Serializable, Preserve]
    public sealed class NetworkOwner_GRC : GameRuntimeComponent, IStoreDataDirty
    {
        public int ConnectionId = -1;
    }
}
