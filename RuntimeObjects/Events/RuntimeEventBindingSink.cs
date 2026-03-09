using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.RuntimeObjects.Events
{
    public sealed class RuntimeEventBindingSink
    {
        private readonly Dictionary<uint, List<RuntimeEventBinding>> _bindings;

        public RuntimeEventBindingSink(Dictionary<uint, List<RuntimeEventBinding>> bindings)
        {
            _bindings = bindings;
        }

        public void Listen<TEvent>(IRuntimeEventHandler<TEvent> handler) where TEvent : GameRuntimeComponent
        {
            if (handler == null)
                return;

            var eventTypeId = typeof(TEvent).GetId();
            if (!_bindings.TryGetValue(eventTypeId, out var list))
            {
                list = new List<RuntimeEventBinding>();
                _bindings[eventTypeId] = list;
            }

            list.Add(RuntimeEventBinding.Create(handler));
        }
    }
}