using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DingoGameObjectsCMS.AssetObjects;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class GameAssetComponentTypeCache
    {
        private static readonly Dictionary<Type, string> _menuNameByType = new();
        private static IReadOnlyList<Type> _types;

        public static IReadOnlyList<Type> Types => GetTypes();

        public static IReadOnlyList<Type> GetTypes(bool refresh = false)
        {
            if (refresh || _types == null)
                _types = BuildTypes();

            return _types;
        }

        public static string GetMenuName(Type type)
        {
            if (type == null)
                return string.Empty;

            if (_types == null)
                _types = BuildTypes();

            return _menuNameByType.TryGetValue(type, out var menuName) && !string.IsNullOrWhiteSpace(menuName)
                ? menuName
                : type.Name;
        }

        public static void Invalidate()
        {
            _types = null;
            _menuNameByType.Clear();
        }

        private static IReadOnlyList<Type> BuildTypes()
        {
            var result = new List<Type>();
            _menuNameByType.Clear();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch
                {
                    continue;
                }

                foreach (var type in types)
                {
                    if (type == null
                        || type.IsAbstract
                        || type.IsInterface
                        || type == typeof(GameAssetComponent)
                        || !typeof(GameAssetComponent).IsAssignableFrom(type)
                        || Attribute.IsDefined(type, typeof(HideInTypeMenuAttribute), inherit: false)
                        || type.GetConstructor(Type.EmptyTypes) == null)
                    {
                        continue;
                    }

                    _menuNameByType[type] = type.GetCustomAttribute<AddTypeMenuAttribute>()?.MenuName ?? type.Name;
                    result.Add(type);
                }
            }

            return result
                .OrderBy(GetTypeOrder)
                .ThenBy(type => _menuNameByType.TryGetValue(type, out var menuName) ? menuName : type.Name)
                .ToArray();
        }

        private static int GetTypeOrder(Type type)
        {
            return type.GetCustomAttribute<AddTypeMenuAttribute>()?.Order ?? 0;
        }
    }
}
