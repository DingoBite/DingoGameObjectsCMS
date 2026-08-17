using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using DingoGameObjectsCMS.AssetObjects;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    /// <summary>
    /// One authored placement that overrides the asset it references.
    /// </summary>
    [Serializable, Preserve]
    public readonly struct GameAssetOverrideSite
    {
        public readonly GameAssetKey Owner;
        public readonly string Path;
        public readonly GameAssetReference Asset;
        public readonly GameAssetOverrides Overrides;

        public GameAssetOverrideSite(
            GameAssetKey owner,
            string path,
            GameAssetReference asset,
            GameAssetOverrides overrides)
        {
            Owner = owner;
            Path = path;
            Asset = asset;
            Overrides = overrides;
        }
    }

    /// <summary>
    /// Finds every <see cref="GameAssetInstance"/> that carries authored
    /// overrides inside an asset, at any depth.
    ///
    /// A placement can sit anywhere in an authored graph — inside a component,
    /// a nested record, a list or a dictionary — and there is no compile-time
    /// surface that enumerates them, so this walk is reflective by necessity.
    /// It is a build-time step: the session registers what it finds once, while
    /// it is sealed.
    ///
    /// The walk is deterministic — declared field order, list order, ordinal
    /// dictionary order — because the registration order it feeds decides the
    /// identity of nothing but must stay stable across runs.
    /// </summary>
    public static class GameAssetOverrideDiscovery
    {
        public static IReadOnlyList<GameAssetOverrideSite> Collect(GameAsset asset)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            var state = new DiscoveryState(asset.Key);
            state.VisitObject(asset.Components, $"GameAsset '{asset.Key}'.Components");
            state.VisitFields(asset, asset.GetType(), typeof(GameAsset), $"GameAsset '{asset.Key}'");
            return state.TakeSites();
        }
    }

    public class GameAssetOverrideReferenceComparer : IEqualityComparer<object>
    {
        public static readonly GameAssetOverrideReferenceComparer Instance = new();

        public new bool Equals(object left, object right) => ReferenceEquals(left, right);

        public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }

    public class DiscoveryState
    {
        private readonly GameAssetKey _owner;
        private readonly List<GameAssetOverrideSite> _sites = new();
        private readonly HashSet<object> _visited = new(GameAssetOverrideReferenceComparer.Instance);

        public DiscoveryState(GameAssetKey owner)
        {
            _owner = owner;
        }

        public IReadOnlyList<GameAssetOverrideSite> TakeSites()
        {
            return Array.AsReadOnly(_sites.ToArray());
        }

        public void VisitObject(object value, string path)
        {
            if (value == null)
                return;

            if (value is GameAssetInstance instance)
            {
                if (instance.Overrides is { HasAny: true })
                    _sites.Add(new GameAssetOverrideSite(_owner, path, instance.Asset, instance.Overrides));

                return;
            }

            var type = value.GetType();
            if (IsTerminal(type))
                return;

            if (!type.IsValueType && !_visited.Add(value))
                return;

            if (value is IDictionary dictionary)
            {
                VisitDictionary(dictionary, path);
                return;
            }
            if (value is IEnumerable enumerable)
            {
                var index = 0;
                foreach (var item in enumerable)
                {
                    VisitObject(item, $"{path}[{index}]");
                    index++;
                }

                return;
            }

            VisitFields(value, type, typeof(object), path);
        }

        public void VisitFields(object value, Type type, Type stopBefore, string path)
        {
            for (var current = type; current != null && current != stopBefore; current = current.BaseType)
            {
                var fields = current.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                for (var index = 0; index < fields.Length; index++)
                {
                    if (fields[index].IsStatic)
                        continue;

                    VisitObject(fields[index].GetValue(value), $"{path}.{fields[index].Name}");
                }
            }
        }

        private void VisitDictionary(IDictionary dictionary, string path)
        {
            var entries = new List<DictionaryEntry>();
            foreach (DictionaryEntry entry in dictionary)
            {
                entries.Add(entry);
            }
            entries.Sort((left, right) => string.CompareOrdinal(left.Key?.ToString(), right.Key?.ToString()));
            for (var index = 0; index < entries.Count; index++)
            {
                VisitObject(entries[index].Key, $"{path}[{index}].Key");
                VisitObject(entries[index].Value, $"{path}[{index}].Value");
            }
        }

        /// <summary>
        /// Stops the walk at leaves and at graphs that cannot contain a
        /// placement: raw JSON carried by an override, an already-decoded
        /// runtime patch, and Unity object references.
        /// </summary>
        private static bool IsTerminal(Type type)
        {
            return type.IsPrimitive
                   || type.IsEnum
                   || type == typeof(string)
                   || type == typeof(decimal)
                   || type == typeof(Hash128)
                   || type == typeof(GameAssetKey)
                   || typeof(JToken).IsAssignableFrom(type)
                   || typeof(RuntimeObjectPatch).IsAssignableFrom(type)
                   || typeof(UnityEngine.Object).IsAssignableFrom(type);
        }
    }
}
