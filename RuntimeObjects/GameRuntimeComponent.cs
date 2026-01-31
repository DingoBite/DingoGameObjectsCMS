using System;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    [Serializable, Preserve, HideInTypeMenu]
    public class GameRuntimeComponent : GameGUIDObject
    {
        public virtual void SetupForEntity(EntityCommandBuffer ecb, GameRuntimeObject g, Entity e) {}
    }
}