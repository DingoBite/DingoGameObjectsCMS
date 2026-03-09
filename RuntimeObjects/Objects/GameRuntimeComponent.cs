using System;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Objects
{
    [Serializable, Preserve, HideInTypeMenu]
    public class GameRuntimeComponent
    {
        public virtual void OnCreate(GameRuntimeObject owner) { }
        public virtual void OnDestroy(GameRuntimeObject owner) { }
        public virtual void SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e) {}
    }
}
