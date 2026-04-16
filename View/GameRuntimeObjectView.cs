using Cysharp.Threading.Tasks;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoUnityExtensions.UnityViewProviders.Core;
using DingoUnityExtensions.UnityViewProviders.Pools;

namespace DingoGameObjectsCMS.View
{
    public abstract class GameRuntimeObjectView : ValueContainer<GameRuntimeObject>
    {
        private GameRuntimeObject _previousValue;

        public virtual UniTask SpawnAsync(CollectionViewSpawnOptions spawnOptions) => UniTask.CompletedTask;
        public virtual UniTask DespawnAsync(CollectionViewSpawnOptions spawnOptions) => UniTask.CompletedTask;

        protected sealed override void PreviousValueFree(GameRuntimeObject previousData)
        {
            _previousValue = previousData;
            base.PreviousValueFree(previousData);
        }

        protected sealed override void SetValueWithoutNotify(GameRuntimeObject value)
        {
            var previousValue = _previousValue;
            var sameRuntimeObject = IsSameRuntimeObject(previousValue, value);

            if (!sameRuntimeObject && previousValue != null)
                OnGRODestroy(previousValue);

            if (!sameRuntimeObject && value != null)
                OnGROCreated(value);

            UpdateGRO(previousValue, value);
            _previousValue = null;
        }

        protected virtual void OnGROCreated(GameRuntimeObject value) { }
        protected virtual void OnGRODestroy(GameRuntimeObject value) { }
        protected virtual void UpdateGRO(GameRuntimeObject previousValue, GameRuntimeObject value) { }

        private static bool IsSameRuntimeObject(GameRuntimeObject a, GameRuntimeObject b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null)
                return false;

            return a.InstanceId == b.InstanceId && a.StoreId.Equals(b.StoreId);
        }
    }
}