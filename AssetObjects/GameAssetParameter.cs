using System;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects
{
    [Serializable, Preserve, HideInTypeMenu]
    public class GameAssetParameter
    {
        public virtual void SetupRuntimeCommand(GameRuntimeCommand c) {}
    }
}