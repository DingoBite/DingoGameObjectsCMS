#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoUnityExtensions.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace DingoGameObjectsCMS
{
    public static class UnityMathJsonOptions
    {
        public static readonly JsonSerializerSettings Options = new();

        static UnityMathJsonOptions()
        {
            Options.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
            Options.TypeNameHandling = TypeNameHandling.None;
            Options.MetadataPropertyHandling = MetadataPropertyHandling.ReadAhead;
            
            var known = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => {
                    try { return a.GetTypes(); } catch { return Array.Empty<Type>(); }
                })
                .Where(t => t != null && !t.IsAbstract && typeof(GameRuntimeComponent).IsAssignableFrom(t));

            Options.SerializationBinder = new TypeAliasBinder(
                knownTypes: known,
                aliasSelector: t => t.Name
            );

            
            AddUnityConverters(Options);
        }

        private static void AddUnityConverters(JsonSerializerSettings settings)
        {
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Math");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Mathematics");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Hashing");
            JsonOptions.AddAllConvertersFromNamespace(settings, "Newtonsoft.Json.UnityConverters.Graphics");
        }
    }
    
    
}
#endif