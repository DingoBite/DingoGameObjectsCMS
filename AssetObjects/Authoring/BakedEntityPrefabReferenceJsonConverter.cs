using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Entities.Serialization;
using UnityEngine;
using UnityEngine.Scripting;
using EntityHash128 = Unity.Entities.Hash128;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    /// <summary>Canonical JSON identity for Unity's native weak Entity-prefab reference.</summary>
    [Preserve]
    public class BakedEntityPrefabReferenceJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(EntityPrefabReference);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(((EntityPrefabReference)value).AssetGUID.ToString());
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return default(EntityPrefabReference);
            if (reader.TokenType == JsonToken.String && Guid.TryParseExact((string)reader.Value, "N", out var guid))
                return guid == Guid.Empty ? default(EntityPrefabReference) : new EntityPrefabReference(new EntityHash128((string)reader.Value));
            var path = reader.Path;
            JToken.Load(reader);
            Debug.LogWarning($"Baked Entity Prefab reference at '{path}' must be a Unity prefab GUID. Using an unavailable template reference.");
            return default(EntityPrefabReference);
        }
    }
}
