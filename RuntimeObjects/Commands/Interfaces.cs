using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using Unity.Collections;

namespace DingoGameObjectsCMS.RuntimeObjects.Commands
{
    public interface ICommandParameter
    {
        public NativeArray<byte> Serialize();
    }

    public interface ICommandLogic
    {
        public void Execute(GameRuntimeCommand command);
    }

    public enum RuntimeReplayStoreScopeDisposition : byte
    {
        Included = 0,
        OutsideScope = 1,
    }

    public interface IRuntimeReplayStoreScopedCommand
    {
        public RuntimeReplayStoreScopeDisposition ClassifyReplayStoreScope(
            RuntimeReplayStoreScope storeScope);
    }
}
