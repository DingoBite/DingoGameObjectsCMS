using DingoGameObjectsCMS.RuntimeObjects.Objects;

namespace DingoGameObjectsCMS.Serialization
{
    public interface IRuntimePayloadSerializer
    {
        byte[] Serialize<T>(T value);
        T Deserialize<T>(byte[] payload);

        byte[] SerializeRuntimeObject(GameRuntimeObject value);
        GameRuntimeObject DeserializeRuntimeObject(byte[] payload);

        byte[] SerializeRuntimeComponent(GameRuntimeComponent value);
        GameRuntimeComponent DeserializeRuntimeComponent(uint compTypeId, byte[] payload);
    }
}
