using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects
{
    /// <summary>
    /// Materialization order for a <see cref="GameAssetComponent"/>, lowest
    /// first. Setup is applied in this order instead of the authored list order,
    /// so the position of a component inside the document carries no meaning and
    /// an override may append one anywhere. Ties keep the authored order.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = true)]
    public class GameAssetSetupOrderAttribute : Attribute
    {
        public readonly int Order;

        public GameAssetSetupOrderAttribute(int order)
        {
            Order = order;
        }
    }

    public static class GameAssetSetupOrderUtils
    {
        public const int DEFAULT_ORDER = 0;

        private static readonly object CACHE_LOCK = new();
        private static readonly Dictionary<Type, int> ORDER_BY_TYPE = new();

        public static int GetOrder(GameAssetComponent component)
        {
            return GetOrder(component.GetType());
        }

        public static int GetOrder(Type componentType)
        {
            lock (CACHE_LOCK)
            {
                if (ORDER_BY_TYPE.TryGetValue(componentType, out var cached))
                    return cached;

                var attribute = (GameAssetSetupOrderAttribute)Attribute.GetCustomAttribute(
                    componentType,
                    typeof(GameAssetSetupOrderAttribute),
                    inherit: true);
                var order = attribute?.Order ?? DEFAULT_ORDER;
                ORDER_BY_TYPE.Add(componentType, order);
                return order;
            }
        }
    }

    [Serializable, Preserve, HideInTypeMenu]
    public class GameAssetComponent
    {
        public virtual void SetupRuntimeComponent(GameRuntimeObject g) {}
        public virtual void SetupRuntimeCommand(GameRuntimeCommand g) {}
    }
}
