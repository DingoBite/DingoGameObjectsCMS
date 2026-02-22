using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects
{
    [Serializable, Preserve, HideInTypeMenu]
    public class GameAssetComponent
    {
        public virtual void SetupRuntimeComponent(GameRuntimeObject g) {}
    }
}