using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine;

namespace DingoGameObjectsCMS.View
{
    public static class GameRuntimeComponentMonoBehaviourExtensions
    {
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