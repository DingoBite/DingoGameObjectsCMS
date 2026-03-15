using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoUnityExtensions.Pools.Core;
using DingoUnityExtensions.UnityViewProviders.Pools;
using UnityEngine;

namespace DingoGameObjectsCMS.View
{
    public class GameRuntimeObjectsCollection : AsyncValueContainerDictPoolDepend<IReadOnlyDictionary<long, GameRuntimeObject>, long, GameRuntimeObject, GameRuntimeObjectView>
    {
        protected override Pool<GameRuntimeObjectView> Factory(GameRuntimeObjectView prefab, GameObject parent) => new(prefab, parent);
        protected override int GetCount(IReadOnlyDictionary<long, GameRuntimeObject> value) => value.Count;
        protected override GameRuntimeObject GetValue(IReadOnlyDictionary<long, GameRuntimeObject> value, long key) => value[key];
        protected override IOrderedEnumerable<long> GetOrderedKeys(IReadOnlyDictionary<long, GameRuntimeObject> value) => value.Keys.OrderBy(k => k);
        protected override UniTask OnAfterPullAsync(long key, GameRuntimeObject value, GameRuntimeObjectView valueContainer, CollectionViewSpawnOptions spawnOptions) => valueContainer != null ? valueContainer.SpawnAsync(spawnOptions) : UniTask.CompletedTask;
        protected override UniTask OnBeforePushAsync(long key, GameRuntimeObject value, GameRuntimeObjectView valueContainer, CollectionViewSpawnOptions spawnOptions) => valueContainer != null ? valueContainer.DespawnAsync(spawnOptions) : UniTask.CompletedTask;
    }
}
