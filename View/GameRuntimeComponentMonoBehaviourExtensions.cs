using System;
using System.Collections.Generic;
using System.Reflection;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine;

namespace DingoGameObjectsCMS.View
{
    public static class GameRuntimeComponentMonoBehaviourExtensions
    {
        private static readonly Dictionary<Type, Type> ComponentViewTypesByComponentType = new();

        static GameRuntimeComponentMonoBehaviourExtensions()
        {
            BuildComponentViewTypeCache();
        }

        public static bool TryGetComponentViewType(Type componentType, out Type componentViewType)
        {
            if (componentType == null)
            {
                componentViewType = null;
                return false;
            }

            return ComponentViewTypesByComponentType.TryGetValue(componentType, out componentViewType);
        }

        private static void BuildComponentViewTypeCache()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var assembly = assemblies[i];
                if (assembly.IsDynamic)
                {
                    continue;
                }

                foreach (var type in GetLoadableTypes(assembly))
                {
                    if (type == null || type.IsAbstract || type.ContainsGenericParameters || !typeof(GameRuntimeComponentMonoBehaviour).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (!TryGetComponentType(type, out var componentType))
                    {
                        continue;
                    }

                    ComponentViewTypesByComponentType.TryAdd(componentType, type);
                }
            }
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types;
            }
        }

        private static bool TryGetComponentType(Type viewType, out Type componentType)
        {
            var type = viewType;
            while (type != null)
            {
                if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(GameRuntimeComponentMonoBehaviour<>))
                {
                    componentType = type.GetGenericArguments()[0];
                    return true;
                }

                type = type.BaseType;
            }

            componentType = null;
            return false;
        }

        public static GameRuntimeComponentMonoBehaviour GetComponentFor(this MonoBehaviour monoBehaviour, Type componentType, bool useFirstComponentOnMissingType = false, bool warnOnMissingType = false)
        {
            if (monoBehaviour == null || componentType == null)
                return null;

            var components = monoBehaviour.GetComponents<GameRuntimeComponentMonoBehaviour>();
            GameRuntimeComponentMonoBehaviour firstComponent = null;
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                if (component == null)
                    continue;

                firstComponent ??= component;
                if (component.ComponentType == componentType)
                    return component;
            }

            if (!useFirstComponentOnMissingType || firstComponent == null)
            {
                if (warnOnMissingType)
                    Debug.LogWarning($"Component palette '{monoBehaviour.name}' has no MonoBehaviour view for runtime component type '{componentType.FullName}'.", monoBehaviour);
                return null;
            }

            if (warnOnMissingType)
                Debug.LogWarning($"Component palette '{monoBehaviour.name}' has no MonoBehaviour view for runtime component type '{componentType.FullName}'. Using first palette view '{firstComponent.GetType().FullName}' for '{firstComponent.ComponentType.FullName}'.", monoBehaviour);

            return firstComponent;
        }

        public static GameRuntimeComponentMonoBehaviour<TComponent> GetComponentFor<TComponent>(this MonoBehaviour monoBehaviour) where TComponent : GameRuntimeComponent =>
            monoBehaviour.GetComponentFor(typeof(TComponent)) as GameRuntimeComponentMonoBehaviour<TComponent>;
    }
}