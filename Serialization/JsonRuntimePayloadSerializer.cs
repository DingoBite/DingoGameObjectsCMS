#if NEWTONSOFT_EXISTS
using System;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Newtonsoft.Json;

namespace DingoGameObjectsCMS.Serialization
{
    public sealed class JsonRuntimePayloadSerializer : IRuntimePayloadSerializer
    {
        public byte[] Serialize<T>(T value)
        {
            if (value == null)
                return Array.Empty<byte>();

            var json = JsonConvert.SerializeObject(value, Formatting.None, GameRuntimeComponentJson.Settings);
            return Encoding.UTF8.GetBytes(json);
        }

        public T Deserialize<T>(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return default;

            var json = Encoding.UTF8.GetString(payload);
            return JsonConvert.DeserializeObject<T>(json, GameRuntimeComponentJson.Settings);
        }

        public byte[] SerializeRuntimeObject(GameRuntimeObject value) => Serialize(value);

        public GameRuntimeObject DeserializeRuntimeObject(byte[] payload) => Deserialize<GameRuntimeObject>(payload);

        public byte[] SerializeRuntimeComponent(GameRuntimeComponent value)
        {
            if (value == null)
                return Array.Empty<byte>();

            var json = JsonConvert.SerializeObject(value, value.GetType(), Formatting.None, GameRuntimeComponentJson.Settings);
            return Encoding.UTF8.GetBytes(json);
        }

        public GameRuntimeComponent DeserializeRuntimeComponent(uint compTypeId, byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return null;

            if (!RuntimeComponentTypeRegistry.TryGetType(compTypeId, out var compType) || compType == null)
                return null;

            var json = Encoding.UTF8.GetString(payload);
            return JsonConvert.DeserializeObject(json, compType, GameRuntimeComponentJson.Settings) as GameRuntimeComponent;
        }
    }
}
#endif
