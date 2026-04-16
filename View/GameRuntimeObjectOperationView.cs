using Cysharp.Threading.Tasks;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoUnityExtensions.UnityViewProviders.Core;
using DingoUnityExtensions.UnityViewProviders.Pools;
using UnityEngine;

namespace DingoGameObjectsCMS.View
{
    public abstract class GameRuntimeObjectOperationView : ValueContainer<GameRuntimeObjectOperation>
    {
        private GameRuntimeObject _runtimeObject;

        public GameRuntimeObject RuntimeObject => _runtimeObject;
        public long RuntimeObjectId => _runtimeObject?.InstanceId ?? -1;
        public Hash128 RuntimeObjectGuid => _runtimeObject != null ? _runtimeObject.GUID : default;

        public virtual UniTask SpawnAsync(CollectionViewSpawnOptions spawnOptions) => UniTask.CompletedTask;
        public virtual UniTask DespawnAsync(CollectionViewSpawnOptions spawnOptions) => UniTask.CompletedTask;

        protected sealed override void SetValueWithoutNotify(GameRuntimeObjectOperation operation)
        {
            var previousValue = _runtimeObject;
            var nextValue = operation.Value;
            var sameRuntimeObject = IsSameRuntimeObject(previousValue, nextValue);

            if (!sameRuntimeObject && previousValue != null)
                OnGRODestroy(previousValue, operation);

            _runtimeObject = nextValue;

            if (!sameRuntimeObject && nextValue != null)
                OnGROCreated(nextValue, operation);

            switch (operation.Kind)
            {
                case GameRuntimeObjectOperationKind.Snapshot:
                    OnGROSnapshot(previousValue, nextValue, operation);
                    break;
                case GameRuntimeObjectOperationKind.Structure:
                    OnGROStructure(previousValue, nextValue, operation);
                    break;
                case GameRuntimeObjectOperationKind.ComponentStructure:
                    OnGROComponentStructure(previousValue, nextValue, operation);
                    break;
                case GameRuntimeObjectOperationKind.Component:
                    OnGROComponent(previousValue, nextValue, operation);
                    break;
                case GameRuntimeObjectOperationKind.Release:
                    OnGRORelease(previousValue, operation);
                    break;
                default:
                    OnUnknownOperation(previousValue, nextValue, operation);
                    break;
            }

            OnGROOperation(previousValue, nextValue, operation);
        }

        protected virtual void OnGROCreated(GameRuntimeObject value, GameRuntimeObjectOperation operation) { }
        protected virtual void OnGRODestroy(GameRuntimeObject value, GameRuntimeObjectOperation operation) { }
        protected virtual void OnGROSnapshot(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation) { }
        protected virtual void OnGROStructure(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation) { }
        protected virtual void OnGROComponentStructure(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation) { }
        protected virtual void OnGROComponent(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation) { }
        protected virtual void OnGRORelease(GameRuntimeObject previousValue, GameRuntimeObjectOperation operation) { }
        protected virtual void OnGROOperation(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation) { }
        protected virtual void OnUnknownOperation(GameRuntimeObject previousValue, GameRuntimeObject value, GameRuntimeObjectOperation operation) { }

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
