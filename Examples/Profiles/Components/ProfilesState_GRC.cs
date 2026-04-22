using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Profiles.Components
{
    [Serializable, Preserve]
    public class ProfilesState_GRC : GameRuntimeComponent<ProfilesState_GRC>
    {
        public Hash128 ActiveProfileId;
    }
}