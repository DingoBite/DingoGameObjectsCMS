#if NEWTONSOFT_EXISTS
using System;
using System.Linq;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoUnityExtensions.Serialization;
using Newtonsoft.Json;

namespace DingoGameObjectsCMS.Serialization
{
    public static class GameRuntimeComponentJson
    {
        public static readonly JsonSerializerSettings Settings = new();

        static GameRuntimeComponentJson()
        {
            Settings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            Settings.TypeNameHandling = TypeNameHandling.None;
            Settings.MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead;
            Settings.ObjectCreationHandling = ObjectCreationHandling.Replace;

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
            }).Where(t => t != null && !t.IsAbstract && (typeof(GameRuntimeComponent).IsAssignableFrom(t)));

            Settings.SerializationBinder = new TypeAliasBinder(knownTypes: known, aliasSelector: t => t.Name);
            
            AddUnityConverters(Settings);
        }

        public static void AddUnityConverters(JsonSerializerSettings settings)
        {
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Math");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Mathematics");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Hashing");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Graphics");
        }
    }
}
#endif