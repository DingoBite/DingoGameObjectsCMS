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
        public static readonly JsonSerializerSettings DataSettings = new();
        public static readonly JsonSerializer JsonSerializer;

        static GameAssetJson()
        {
            ConfigureBaseSettings(Settings);
            Settings.TypeNameHandling = TypeNameHandling.Auto;

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
            ConfigureBaseSettings(DataSettings);
            DataSettings.TypeNameHandling = TypeNameHandling.None;
            DataSettings.SerializationBinder = Settings.SerializationBinder;
            JsonSerializer = JsonSerializer.Create(Settings);
        }

        public static string ToJson(this GameAssetScriptableObject asset, bool pretty = true) => JsonConvert.SerializeObject(asset, typeof(GameAssetScriptableObject), pretty ? Formatting.Indented : Formatting.None, Settings);
        public static GameAssetScriptableObject FromJson(string json) => JsonConvert.DeserializeObject<GameAssetScriptableObject>(json, Settings);
        public static GameAssetScriptableObject FromJObject(JObject json) => json?.ToObject<GameAssetScriptableObject>(JsonSerializer);

        public static T FromJson<T>(this string json) where T : GameAssetScriptableObject
        {
            return JsonConvert.DeserializeObject<T>(json, Settings);
        }

        private static void ConfigureBaseSettings(JsonSerializerSettings settings)
        {
            settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            // An unset optional member is absent from the document rather than
            // an explicit null, so adding one does not rewrite every asset.
            settings.NullValueHandling = NullValueHandling.Ignore;
            settings.MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead;
            settings.ObjectCreationHandling = ObjectCreationHandling.Replace;
            settings.ContractResolver = new UnitySerializeFieldContractResolver();
            settings.Converters.Add(new UnityFixedStringJsonConverter());
            GameRuntimeJson.AddUnityConverters(settings);
        }
    }
}
#endif
