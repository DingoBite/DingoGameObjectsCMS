#if NEWTONSOFT_EXISTS
using System;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMS.Serialization
{
    public static class GameAssetJson
    {
        public static readonly JsonSerializerSettings Settings = new();
        public static readonly JsonSerializer JsonSerializer;

        static GameAssetJson()
        {
            Settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            Settings.TypeNameHandling = TypeNameHandling.Auto;
            Settings.MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead;
            Settings.ObjectCreationHandling = ObjectCreationHandling.Replace;
            Settings.ContractResolver = new UnitySerializeFieldContractResolver();

            var known = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a =>
            {
                try
                {
                    return a.GetTypes();
                }
                catch
                {
                    return Array.Empty<Type>();
                }
            }).Where(t => t != null && !t.IsAbstract && (typeof(GameAssetScriptableObject).IsAssignableFrom(t) || typeof(GameAssetComponent).IsAssignableFrom(t)));

            Settings.SerializationBinder = new TypeAliasBinder(knownTypes: known, aliasSelector: t => t.Name);
            Settings.Converters.Add(new UnityFixedStringJsonConverter());
            GameRuntimeJson.AddUnityConverters(Settings);
            JsonSerializer = JsonSerializer.Create(Settings);
        }

        public static string ToJson(this GameAssetScriptableObject asset, bool pretty = true) => JsonConvert.SerializeObject(asset, typeof(GameAssetScriptableObject), pretty ? Formatting.Indented : Formatting.None, Settings);
        public static GameAssetScriptableObject FromJson(string json) => JsonConvert.DeserializeObject<GameAssetScriptableObject>(json, Settings);
        public static GameAssetScriptableObject FromJObject(JObject json) => json?.ToObject<GameAssetScriptableObject>(JsonSerializer);

        public static T FromJson<T>(this string json) where T : GameAssetScriptableObject
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }
    }
}
#endif
