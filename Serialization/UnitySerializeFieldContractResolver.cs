#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using UnityEngine;

namespace DingoGameObjectsCMS.Serialization
{
    public sealed class UnitySerializeFieldContractResolver : DefaultContractResolver
    {
        protected override List<MemberInfo> GetSerializableMembers(Type objectType)
        {
            var members = new List<MemberInfo>();

            foreach (var t in EnumerateTypeHierarchy(objectType))
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

                foreach (var f in t.GetFields(flags))
                {
                    if (f.IsStatic)
                        continue;

                    if (f.IsPublic)
                    {
                        members.Add(f);
                        continue;
                    }

                    if (HasUnitySerializeField(f) || HasUnitySerializeReference(f) || HasJsonProperty(f))
                    {
                        members.Add(f);
                        continue;
                    }
                }
            }

            return members;
        }

        protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
        {
            var prop = base.CreateProperty(member, memberSerialization);
            prop.Readable = true;
            prop.Writable = true;
            return prop;
        }

        private static bool HasUnitySerializeField(FieldInfo f) =>
            f.GetCustomAttribute<SerializeField>() != null;

        private static bool HasUnitySerializeReference(FieldInfo f) =>
            f.GetCustomAttribute<SerializeReference>() != null;

        private static bool HasJsonProperty(FieldInfo f) =>
            f.GetCustomAttribute<JsonPropertyAttribute>() != null;

        private static IEnumerable<Type> EnumerateTypeHierarchy(Type t)
        {
            while (t != null && t != typeof(UnityEngine.Object))
            {
                yield return t;
                t = t.BaseType;
            }
        }
    }
}
#endif