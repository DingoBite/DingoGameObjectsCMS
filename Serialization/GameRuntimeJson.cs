#if NEWTONSOFT_EXISTS
using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoUnityExtensions.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMS.Serialization
{
    public static class GameRuntimeJson
    {
        public static readonly JsonSerializerSettings Settings = new();

        static GameRuntimeJson()
        {
            Settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            Settings.TypeNameHandling = TypeNameHandling.None;
            Settings.MetadataPropertyHandling = MetadataPropertyHandling.Ignore;
            Settings.ObjectCreationHandling = ObjectCreationHandling.Replace;

            Settings.Converters.Add(new UnityFixedStringJsonConverter());
            AddUnityConverters(Settings);
        }

        public static void AddUnityConverters(JsonSerializerSettings settings)
        {
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Math");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Mathematics");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Hashing");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Graphics");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.NativeArray");
        }
    }

    public sealed class RuntimeComponentJsonConverter : JsonConverter
    {
        private const string TYPE_ID_PROPERTY = "TypeId";
        private const string PAYLOAD_PROPERTY = "Payload";

        public override bool CanConvert(Type objectType)
        {
            return objectType != null
                   && typeof(GameRuntimeComponent).IsAssignableFrom(objectType);
        }

        public override void WriteJson(
            JsonWriter writer,
            object value,
            JsonSerializer serializer)
        {
            if (value is not GameRuntimeComponent component)
            {
                throw new JsonSerializationException(
                    "Runtime component JSON entries cannot be null or use a non-component value.");
            }
            var runtimeType = component.GetType();
            if (!RuntimeComponentTypeRegistry.TryGetId(runtimeType, out var componentTypeId))
            {
                throw new JsonSerializationException(
                    $"Runtime component type '{runtimeType.FullName}' is not registered in the compiled component table.");
            }

            writer.WriteStartObject();
            writer.WritePropertyName(TYPE_ID_PROPERTY);
            writer.WriteValue(componentTypeId);
            writer.WritePropertyName(PAYLOAD_PROPERTY);
            serializer.Serialize(writer, component, runtimeType);
            writer.WriteEndObject();
        }

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                throw new JsonSerializationException(
                    "Runtime component JSON entries cannot be null.");
            }

            var container = JObject.Load(reader);
            ValidateProperties(container);
            var idToken = container[TYPE_ID_PROPERTY];
            if (idToken?.Type != JTokenType.Integer
                || !long.TryParse(idToken.ToString(), out var signedId)
                || signedId < 0
                || signedId > uint.MaxValue)
            {
                throw new JsonSerializationException(
                    "Runtime component JSON entry requires a numeric TypeId.");
            }

            var componentTypeId = (uint)signedId;
            if (!RuntimeComponentTypeRegistry.TryGetType(componentTypeId, out var runtimeType)
                || runtimeType == null)
            {
                throw new JsonSerializationException(
                    $"Runtime component id '{componentTypeId}' is not registered in the compiled component table.");
            }

            var payload = container[PAYLOAD_PROPERTY]
                          ?? throw new JsonSerializationException(
                              "Runtime component JSON entry requires a Payload object.");
            using var payloadReader = payload.CreateReader();
            var component = serializer.Deserialize(payloadReader, runtimeType);
            if (component is not GameRuntimeComponent)
            {
                throw new JsonSerializationException(
                    $"Runtime component id '{componentTypeId}' did not deserialize to a {nameof(GameRuntimeComponent)}.");
            }

            return component;
        }

        private static void ValidateProperties(JObject container)
        {
            foreach (var property in container.Properties())
            {
                if (!string.Equals(property.Name, TYPE_ID_PROPERTY, StringComparison.Ordinal)
                    && !string.Equals(property.Name, PAYLOAD_PROPERTY, StringComparison.Ordinal))
                {
                    throw new JsonSerializationException(
                        $"Runtime component JSON entry contains unsupported property '{property.Name}'.");
                }
            }
        }
    }
}
#endif
