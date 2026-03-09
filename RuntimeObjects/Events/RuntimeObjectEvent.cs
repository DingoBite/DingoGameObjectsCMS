using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.RuntimeObjects.Events
{
    public readonly struct RuntimeEventContext
    {
        public readonly RuntimeStore Store;
        public readonly GameRuntimeObject RuntimeObject;

        public RuntimeEventContext(RuntimeStore store, GameRuntimeObject runtimeObject)
        {
            Store = store;
            RuntimeObject = runtimeObject;
        }
    }
    
    public readonly struct RuntimeEventBinding
    {
        private delegate void DispatchDelegate(object handler, in RuntimeEventContext context, GameRuntimeComponent runtimeEvent);

        private readonly object _handler;
        private readonly DispatchDelegate _dispatch;

        private RuntimeEventBinding(object handler, DispatchDelegate dispatch)
        {
            _handler = handler;
            _dispatch = dispatch;
        }

        public void Dispatch(in RuntimeEventContext context, GameRuntimeComponent runtimeEvent) => _dispatch(_handler, context, runtimeEvent);

        public static RuntimeEventBinding Create<TEvent>(IRuntimeEventHandler<TEvent> handler) where TEvent : GameRuntimeComponent
        {
            return new RuntimeEventBinding(handler, Dispatch<TEvent>);
        }

        private static void Dispatch<TEvent>(object handler, in RuntimeEventContext context, GameRuntimeComponent runtimeEvent) where TEvent : GameRuntimeComponent
        {
            ((IRuntimeEventHandler<TEvent>)handler).HandleEvent(context, (TEvent)runtimeEvent);
        }
    }
}