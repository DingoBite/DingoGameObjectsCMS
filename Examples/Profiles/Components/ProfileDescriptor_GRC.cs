using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Profiles.Components
{
    [Serializable, Preserve]
    public class ProfileDescriptor_GRC : GameRuntimeComponent<ProfileDescriptor_GRC>
    {
        public string Name;
        public DateTime CreationDateTime = DateTime.Now;
    }
}