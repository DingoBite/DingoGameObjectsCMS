using System;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Objects
{
    public abstract class GameRuntimeComponent<TSelf> : GameRuntimeComponent where TSelf : GameRuntimeComponent<TSelf>
    {
        public override void SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            ecb.AddComponent(e, (TSelf)this);
        }

        public override void AddForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            ecb.AddComponent(e, (TSelf)this);
        }

        public override void RemoveFromEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            ecb.RemoveComponent<TSelf>(e);
        }
    }

    [Serializable, Preserve, HideInTypeMenu]
    public class GameRuntimeComponent : IComponentData
    {
        public virtual void SetupForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e) { }
        public virtual void AddForEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e) { }
        public virtual void RemoveFromEntity(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e) { }
        public virtual void DestroyForRuntime(RuntimeStore store, EntityCommandBuffer ecb, GameRuntimeObject g, Entity e)
        {
            if (e != Entity.Null)
                RemoveFromEntity(store, ecb, g, e);
        }
    }
}