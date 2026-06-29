using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoUnityExtensions.UnityViewProviders.Core;

namespace DingoGameObjectsCMS.View
{
    public abstract class GameRuntimeComponentMonoBehaviour : ValueContainer<GameRuntimeComponent>
    {
        public abstract Type ComponentType { get; }
        public GameRuntimeObjectByComponentsView View { get; private set; }
        public GameRuntimeObject RuntimeObject => View != null ? View.RuntimeObject : null;

        public void Attach(GameRuntimeObjectByComponentsView view)
        {
            View = view;
            OnAttach(view);
        }

        public void Detach()
        {
            OnDetach();
            UpdateValueWithoutNotify(null);
            View = null;
        }

        public virtual void AddTo(GameRuntimeObject gro) { }

        protected virtual void OnAttach(GameRuntimeObjectByComponentsView view) { }
        protected virtual void OnDetach() { }
    }

    public abstract class GameRuntimeComponentMonoBehaviour<TComponent> : GameRuntimeComponentMonoBehaviour where TComponent : GameRuntimeComponent
    {
        public override Type ComponentType => typeof(TComponent);
        public TComponent TypedValue => Value as TComponent;

        public sealed override void AddTo(GameRuntimeObject gro) => AddToGRO(gro);

        protected sealed override void SetValueWithoutNotify(GameRuntimeComponent value) => SetValueWithoutNotify(value as TComponent);

        protected virtual void AddToGRO(GameRuntimeObject gro) { }
        protected abstract void SetValueWithoutNotify(TComponent value);
    }
}