using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.RuntimeObjects.Objects
{
    internal static class RuntimeComponentCollectionValidator
    {
        public static void Validate(
            IReadOnlyList<GameRuntimeComponent> components,
            string owner,
            bool requireRegisteredTypeIds)
        {
            if (components == null)
                throw new InvalidOperationException($"{RequireOwner(owner)} has a null runtime component collection.");

            var concreteTypes = new HashSet<Type>();
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                if (component == null)
                    throw new InvalidOperationException($"{RequireOwner(owner)} contains a null runtime component at index {i}.");

                var type = component.GetType();
                if (type.IsAbstract || !typeof(GameRuntimeComponent).IsAssignableFrom(type))
                    throw new InvalidOperationException($"{RequireOwner(owner)} contains invalid runtime component type '{type.FullName}'.");
                if (!concreteTypes.Add(type))
                    throw new InvalidOperationException($"{RequireOwner(owner)} contains duplicate runtime component type '{type.FullName}'.");
            }

            if (!RuntimeComponentTypeRegistry.IsInitialized)
                return;

            var runtimeTypeIds = new HashSet<uint>();
            foreach (var type in concreteTypes)
            {
                if (!RuntimeComponentTypeRegistry.TryGetId(type, out var typeId))
                {
                    if (requireRegisteredTypeIds)
                        throw new InvalidOperationException($"{RequireOwner(owner)} contains unregistered runtime component type '{type.FullName}'.");
                    continue;
                }
                if (!runtimeTypeIds.Add(typeId))
                    throw new InvalidOperationException($"{RequireOwner(owner)} contains duplicate runtime component type id {typeId}.");
            }
        }

        private static string RequireOwner(string owner)
        {
            return string.IsNullOrWhiteSpace(owner) ? "Runtime component collection" : owner;
        }
    }
}
