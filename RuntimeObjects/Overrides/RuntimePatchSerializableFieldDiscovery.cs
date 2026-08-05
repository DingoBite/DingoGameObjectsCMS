using System;
using System.Collections.Generic;
using System.Reflection;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    /// <summary>
    /// Defines the single serialized-field contract shared by runtime patch
    /// schema generation and canonical authoring materialization.
    /// </summary>
    public static class RuntimePatchSerializableFieldDiscovery
    {
        public static List<FieldInfo> Collect(Type runtimeType)
        {
            if (runtimeType == null)
                throw new ArgumentNullException(nameof(runtimeType));

            var result = new List<FieldInfo>();
            for (var current = runtimeType;
                 current != null
                 && current != typeof(GameRuntimeComponent)
                 && current != typeof(object);
                 current = current.BaseType)
            {
                var fields = current.GetFields(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic
                    | BindingFlags.DeclaredOnly);
                for (var index = 0; index < fields.Length; index++)
                {
                    var field = fields[index];
                    if (field.IsStatic
                        || field.IsLiteral
                        || field.GetCustomAttribute<NonSerializedAttribute>(
                            inherit: false) != null)
                    {
                        continue;
                    }

                    if (!field.IsPublic
                        && field.GetCustomAttribute<SerializeField>(
                            inherit: false) == null
                        && field.GetCustomAttribute<SerializeReference>(
                            inherit: false) == null)
                    {
                        continue;
                    }

                    result.Add(field);
                }
            }

            result.Sort(CompareFields);
            for (var index = 1; index < result.Count; index++)
            {
                if (string.Equals(
                        result[index - 1].Name,
                        result[index].Name,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Runtime patch component '{runtimeType.FullName}' "
                        + $"hides serialized field '{result[index].Name}'. "
                        + "Serialized field names must be unique across its "
                        + "inheritance chain.");
                }
            }

            return result;
        }

        private static int CompareFields(FieldInfo first, FieldInfo second)
        {
            var name = string.CompareOrdinal(first.Name, second.Name);
            if (name != 0)
                return name;
            return string.CompareOrdinal(
                first.DeclaringType?.FullName,
                second.DeclaringType?.FullName);
        }
    }
}
