#if MIRROR
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.Serialization;

namespace DingoGameObjectsCMS.Mirror
{
    [System.Obsolete("Use DingoGameObjectsCMS.Serialization.RuntimePayloadSerialization instead.")]
    public static class RuntimeNetSerialization
    {
        public static byte[] Serialize<T>(T value) => RuntimePayloadSerialization.Current.Serialize(value);

        public static T Deserialize<T>(byte[] payload) => RuntimePayloadSerialization.Current.Deserialize<T>(payload);

        public static byte[] SerializeRuntimeObject(GameRuntimeObject value) => RuntimePayloadSerialization.Current.SerializeRuntimeObject(value);

        public static GameRuntimeObject DeserializeRuntimeObject(byte[] payload) => RuntimePayloadSerialization.Current.DeserializeRuntimeObject(payload);

        public static byte[] SerializeRuntimeComponent(GameRuntimeComponent value) => RuntimePayloadSerialization.Current.SerializeRuntimeComponent(value);

        public static GameRuntimeComponent DeserializeRuntimeComponent(uint compTypeId, byte[] payload) =>
            RuntimePayloadSerialization.Current.DeserializeRuntimeComponent(compTypeId, payload);
    }
}
#endif
