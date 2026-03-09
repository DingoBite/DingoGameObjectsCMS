using DingoGameObjectsCMS.RuntimeObjects.Objects;

namespace DingoGameObjectsCMS.RuntimeObjects.Events
{
    public interface IRuntimeEventBindingsProvider : IStoreStructDirtyIgnore
    {
        public void RegisterRuntimeEventHandlers(RuntimeEventBindingSink sink);
    }
    
    public interface IRuntimeEventHandler<in TEvent> where TEvent : GameRuntimeComponent
    {
        public void HandleEvent(in RuntimeEventContext context, TEvent runtimeEvent);
    }

}