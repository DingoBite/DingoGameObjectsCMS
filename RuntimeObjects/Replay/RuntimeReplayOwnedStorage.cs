using System;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public class RuntimeReplayOwnedStorageAttribute : Attribute
    {
        public readonly int Order;

        public RuntimeReplayOwnedStorageAttribute(int order)
        {
            Order = order;
        }
    }
}
