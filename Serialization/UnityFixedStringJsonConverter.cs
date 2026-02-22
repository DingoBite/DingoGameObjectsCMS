#if NEWTONSOFT_EXISTS
using System;
using Newtonsoft.Json;
using Unity.Collections;

namespace DingoGameObjectsCMS.Serialization
{
    public sealed class UnityFixedStringJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType)
        {
            return objectType == typeof(FixedString32Bytes) ||
                   objectType == typeof(FixedString64Bytes) ||
                   objectType == typeof(FixedString128Bytes) ||
                   objectType == typeof(FixedString512Bytes) ||
                   objectType == typeof(FixedString4096Bytes);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(value?.ToString() ?? string.Empty);
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            var value = reader.TokenType == JsonToken.Null ? string.Empty : reader.Value?.ToString() ?? string.Empty;

            if (objectType == typeof(FixedString32Bytes))
                return (FixedString32Bytes)value;

            if (objectType == typeof(FixedString64Bytes))
                return (FixedString64Bytes)value;

            if (objectType == typeof(FixedString128Bytes))
                return (FixedString128Bytes)value;

            if (objectType == typeof(FixedString512Bytes))
                return (FixedString512Bytes)value;

            if (objectType == typeof(FixedString4096Bytes))
                return (FixedString4096Bytes)value;

            return existingValue;
        }
    }
}
#endif