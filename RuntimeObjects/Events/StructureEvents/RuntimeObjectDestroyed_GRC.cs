using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Events.StructureEvents
{
    [Serializable, Preserve, HideInTypeMenu]
    public sealed class RuntimeObjectDestroyed_GRC : GameRuntimeComponent, IStoreStructDirtyIgnore
    {
        public RemoveMode RemoveMode;
    }
}