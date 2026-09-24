using System;
using System.Collections.Generic;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    public static class GameAssetObjectAuthoringCollector
    {
        public static bool TryCollect(GameAssetObjectAuthoring root, out List<GameAssetComponent> components, out string error)
        {
            components = new List<GameAssetComponent>();
            error = null;
            if (root == null)
            {
                error = "A GameAsset authoring root is required.";
                return false;
            }

            var types = new HashSet<Type>();
            if (!TryCollect(root.transform, root.transform, components, types, out error))
            {
                components.Clear();
                return false;
            }
            if (components.Count == 0)
            {
                error = "A GameAsset authoring root requires at least one component authoring source.";
                return false;
            }

            components.Sort(CompareComponents);
            return true;
        }

        private static bool TryCollect(Transform current, Transform root, List<GameAssetComponent> components, HashSet<Type> types, out string error)
        {
            error = null;
            if (current != root && current.TryGetComponent<GameAssetObjectAuthoring>(out _))
                return true;

            var behaviours = current.GetComponents<MonoBehaviour>();
            for (var i = 0; i < behaviours.Length; i++)
            {
                if (behaviours[i] is not IGameAssetComponentAuthoring source)
                    continue;

                GameAssetComponent component;
                try
                {
                    component = source.BuildGameAssetComponent();
                }
                catch (Exception exception)
                {
                    error = $"{behaviours[i].GetType().FullName} could not build its GameAsset component: {exception.Message}";
                    return false;
                }
                if (component == null)
                {
                    error = $"{behaviours[i].GetType().FullName} returned a null GameAsset component.";
                    return false;
                }
                if (!types.Add(component.GetType()))
                {
                    error = $"GameAsset component '{component.GetType().FullName}' is authored more than once.";
                    return false;
                }

                components.Add(component);
            }

            for (var i = 0; i < current.childCount; i++)
            {
                if (!TryCollect(current.GetChild(i), root, components, types, out error))
                    return false;
            }

            return true;
        }

        private static int CompareComponents(GameAssetComponent left, GameAssetComponent right)
        {
            var comparison = GameAssetSetupOrderUtils.GetOrder(left).CompareTo(GameAssetSetupOrderUtils.GetOrder(right));
            if (comparison != 0)
                return comparison;

            comparison = string.Compare(left.GetType().FullName, right.GetType().FullName, StringComparison.Ordinal);
            return comparison != 0 ? comparison : string.Compare(left.GetType().AssemblyQualifiedName, right.GetType().AssemblyQualifiedName, StringComparison.Ordinal);
        }
    }
}
