#if NEWTONSOFT_EXISTS
using System;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using Newtonsoft.Json;

namespace DingoGameObjectsCMS.Serialization
{
    public static class GameAssetJsonRuntime
    {
        public static readonly JsonSerializerSettings Settings = new();

        static GameAssetJsonRuntime()
        {
            Settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            Settings.TypeNameHandling = TypeNameHandling.None;
            Settings.MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead;
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
            GameRuntimeComponentJson.AddUnityConverters(Settings);
        }

        public static string ToJson(this GameAssetScriptableObject asset, bool pretty = true) => JsonConvert.SerializeObject(asset, typeof(GameAssetScriptableObject), pretty ? Formatting.Indented : Formatting.None, Settings);

        public static T FromJson<T>(this string json) where T : GameAssetScriptableObject
        {
            var inst = UnityEngine.ScriptableObject.CreateInstance<T>();
            JsonConvert.PopulateObject(json, inst, Settings);
            return inst;
        }
    }
}
#endif