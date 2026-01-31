using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Serialization;

namespace DingoGameObjectsCMS
{
    public sealed class TypeAliasBinder : ISerializationBinder
    {
        private readonly Dictionary<string, Type> _nameToType;
        private readonly Dictionary<Type, string> _typeToName;

        public TypeAliasBinder(IEnumerable<Type> knownTypes, Func<Type, string> aliasSelector)
        {
            if (knownTypes == null)
                throw new ArgumentNullException(nameof(knownTypes));
            aliasSelector ??= (t => t.Name);

            var types = knownTypes.Where(t => t != null).Distinct().ToArray();

            var names = new Dictionary<Type, string>(types.Length);
            foreach (var t in types)
            {
                var alias = aliasSelector(t);
                if (string.IsNullOrWhiteSpace(alias))
                    alias = t.Name;

                names[t] = alias;
            }

            ResolveCollisions(names, disambiguate: t => t.FullName ?? t.Name);

            ResolveCollisions(names, disambiguate: t =>
            {
                var asm = t.Assembly.GetName().Name ?? "UnknownAsm";
                var full = t.FullName ?? t.Name;
                return $"{asm}:{full}";
            });

            var dup = names.GroupBy(kv => kv.Value, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);

            if (dup != null)
            {
                var list = string.Join(", ", dup.Select(kv => kv.Key.FullName ?? kv.Key.Name));
                throw new InvalidOperationException($"Type name collision after disambiguation for key '{dup.Key}': {list}");
            }

            _typeToName = names;
            _nameToType = names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
        }

        public void BindToName(Type serializedType, out string assemblyName, out string typeName)
        {
            assemblyName = null;
            typeName = _typeToName.TryGetValue(serializedType, out var alias) ? alias : (serializedType.FullName ?? serializedType.Name);
        }

        public Type BindToType(string assemblyName, string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;

            if (_nameToType.TryGetValue(typeName, out var t))
                return t;

            return !string.IsNullOrEmpty(assemblyName) ? Type.GetType($"{typeName}, {assemblyName}") : Type.GetType(typeName);
        }

        private static void ResolveCollisions(Dictionary<Type, string> current, Func<Type, string> disambiguate)
        {
            var groups = current.GroupBy(kv => kv.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();

            if (groups.Count == 0)
                return;

            foreach (var g in groups)
            {
                foreach (var kv in g)
                {
                    current[kv.Key] = disambiguate(kv.Key);
                }
            }
        }
    }
}