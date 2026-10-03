using System;
using System.Collections.Generic;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    public enum GameAssetAuthoringSourceKind
    {
        Manual,
        Converted
    }

    public class GameAssetAuthoringSource
    {
        public readonly GameAssetAuthoringSourceKind Kind;
        public readonly int Index;
        public readonly Component Component;
        public readonly GameAssetAuthoringConverter Converter;

        public string Description => Kind == GameAssetAuthoringSourceKind.Manual ? $"Manual GAC #{Index + 1}" : $"{Component.GetType().Name} → {Converter.GetType().Name} (binding #{Index + 1})";

        public GameAssetAuthoringSource(GameAssetAuthoringSourceKind kind, int index, Component component = null, GameAssetAuthoringConverter converter = null)
        {
            Kind = kind;
            Index = index;
            Component = component;
            Converter = converter;
        }
    }

    public class GameAssetAuthoringCollection
    {
        public readonly IReadOnlyList<GameAssetComponent> Components;
        public readonly IReadOnlyList<GameAssetAuthoringSource> Sources;

        public GameAssetAuthoringCollection(IReadOnlyList<GameAssetComponent> components, IReadOnlyList<GameAssetAuthoringSource> sources)
        {
            var copiedComponents = new GameAssetComponent[components.Count];
            var copiedSources = new GameAssetAuthoringSource[sources.Count];
            for (var index = 0; index < components.Count; index++)
            {
                copiedComponents[index] = components[index];
                copiedSources[index] = sources[index];
            }
            Components = Array.AsReadOnly(copiedComponents);
            Sources = Array.AsReadOnly(copiedSources);
        }
    }

    public static class GameAssetAuthoringCollector
    {
        public static bool TryCollect(GameAssetAuthoring authoring, out GameAssetAuthoringCollection collection, out string error)
        {
            collection = null;
            error = null;
            if (authoring == null)
            {
                error = "A GameAsset authoring component is required.";
                return false;
            }
            if (authoring.Components == null || authoring.Converters == null)
            {
                error = $"GameAsset authoring '{authoring.name}' has a missing serialized list.";
                return false;
            }

            var components = new List<GameAssetComponent>();
            var sources = new List<GameAssetAuthoringSource>();
            var sourceByType = new Dictionary<Type, GameAssetAuthoringSource>();
            for (var index = 0; index < authoring.Components.Count; index++)
            {
                var source = new GameAssetAuthoringSource(GameAssetAuthoringSourceKind.Manual, index);
                if (!TryAdd(authoring.Components[index], source, components, sources, sourceByType, out error))
                    return false;
            }

            var assignedComponents = new HashSet<Component>();
            for (var index = 0; index < authoring.Converters.Count; index++)
            {
                var binding = authoring.Converters[index];
                if (binding == null || binding.Source == null || binding.Converter == null)
                {
                    error = $"GameAsset authoring '{authoring.name}' binding #{index + 1} requires both a source and a converter.";
                    return false;
                }
                if (binding.Source.gameObject != authoring.gameObject)
                {
                    error = $"GameAsset authoring '{authoring.name}' binding #{index + 1} uses '{binding.Source.name}' outside its own GameObject.";
                    return false;
                }
                if (!assignedComponents.Add(binding.Source))
                {
                    error = $"GameAsset authoring '{authoring.name}' assigns '{binding.Source.GetType().FullName}' more than once.";
                    return false;
                }

                IReadOnlyList<GameAssetComponent> converted;
                try
                {
                    var supportedType = binding.Converter.SourceComponentType;
                    if (supportedType == null || !typeof(Component).IsAssignableFrom(supportedType) || !supportedType.IsInstanceOfType(binding.Source))
                    {
                        error = $"GameAsset authoring '{authoring.name}' binding #{index + 1}: converter '{binding.Converter.GetType().FullName}' does not support '{binding.Source.GetType().FullName}'.";
                        return false;
                    }
                    converted = binding.Converter.Convert(binding.Source);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, authoring);
                    error = $"GameAsset authoring '{authoring.name}' binding #{index + 1}: converter '{binding.Converter.GetType().FullName}' failed: {exception.Message}";
                    return false;
                }
                if (converted == null)
                {
                    error = $"GameAsset authoring '{authoring.name}' binding #{index + 1}: converter '{binding.Converter.GetType().FullName}' returned no component list.";
                    return false;
                }

                var source = new GameAssetAuthoringSource(GameAssetAuthoringSourceKind.Converted, index, binding.Source, binding.Converter);
                for (var componentIndex = 0; componentIndex < converted.Count; componentIndex++)
                {
                    if (!TryAdd(converted[componentIndex], source, components, sources, sourceByType, out error))
                        return false;
                }
            }

            var order = new List<int>(components.Count);
            for (var index = 0; index < components.Count; index++)
            {
                order.Add(index);
            }
            order.Sort((left, right) => Compare(components[left], components[right]));
            var orderedComponents = new List<GameAssetComponent>(components.Count);
            var orderedSources = new List<GameAssetAuthoringSource>(components.Count);
            for (var index = 0; index < order.Count; index++)
            {
                orderedComponents.Add(components[order[index]]);
                orderedSources.Add(sources[order[index]]);
            }
            collection = new GameAssetAuthoringCollection(orderedComponents, orderedSources);
            return true;
        }

        private static bool TryAdd(GameAssetComponent component, GameAssetAuthoringSource source, List<GameAssetComponent> components, List<GameAssetAuthoringSource> sources, Dictionary<Type, GameAssetAuthoringSource> sourceByType, out string error)
        {
            error = null;
            if (component == null)
            {
                error = $"{source.Description} contains a null GameAsset component.";
                return false;
            }
            var type = component.GetType();
            if (sourceByType.TryGetValue(type, out var previous))
            {
                error = $"GameAsset component '{type.FullName}' is authored more than once: {previous.Description}; {source.Description}.";
                return false;
            }
            sourceByType.Add(type, source);
            components.Add(component);
            sources.Add(source);
            return true;
        }

        private static int Compare(GameAssetComponent left, GameAssetComponent right)
        {
            var comparison = GameAssetSetupOrderUtils.GetOrder(left).CompareTo(GameAssetSetupOrderUtils.GetOrder(right));
            if (comparison != 0)
                return comparison;
            comparison = string.Compare(left.GetType().FullName, right.GetType().FullName, StringComparison.Ordinal);
            return comparison != 0 ? comparison : string.Compare(left.GetType().AssemblyQualifiedName, right.GetType().AssemblyQualifiedName, StringComparison.Ordinal);
        }
    }
}
