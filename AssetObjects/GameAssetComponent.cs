using System;
using DingoGameObjectsCMS.RuntimeObjects;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects
{
    [Serializable, Preserve]
    public abstract class GameAssetComponent : GameGUIDObject
    {
        public abstract void SetupRuntimeComponent(GameRuntimeObject g);
    }
}