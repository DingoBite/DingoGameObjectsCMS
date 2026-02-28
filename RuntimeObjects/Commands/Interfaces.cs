using DingoGameObjectsCMS.RuntimeObjects.Objects;
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
}