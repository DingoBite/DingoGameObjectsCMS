using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoUnityExtensions.Pools.Core;
using DingoUnityExtensions.UnityViewProviders.Core;
using DingoUnityExtensions.UnityViewProviders.Pools;
using UnityEngine;

namespace DingoGameObjectsCMS.View
{
    public class GameRuntimeObjectsCollection : ValueContainerDictPoolDepend<IReadOnlyDictionary<long, GameRuntimeObject>, long, GameRuntimeObject, ValueContainer<GameRuntimeObject>>
    {
        protected override Pool<ValueContainer<GameRuntimeObject>> Factory(ValueContainer<GameRuntimeObject> prefab, GameObject parent) => new(prefab, parent);
        protected override int GetCount(IReadOnlyDictionary<long, GameRuntimeObject> value) => value.Count;
        protected override GameRuntimeObject GetValue(IReadOnlyDictionary<long, GameRuntimeObject> value, long key) => value[key];
        protected override IOrderedEnumerable<long> GetOrderedKeys(IReadOnlyDictionary<long, GameRuntimeObject> value) => value.Keys.OrderBy(k => k);
    }
}