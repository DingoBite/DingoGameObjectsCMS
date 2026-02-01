using System;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    [Serializable, Preserve, HideInTypeMenu]
    public class GameRuntimeComponent
    {
        public virtual void SetupForEntity(EntityCommandBuffer ecb, GameRuntimeObject g, Entity e) {}
    }
}