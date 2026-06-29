using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Profiles.Components
{
    [Serializable, Preserve, RuntimeComponentKey("dingo.examples.profiles.profiles-state")]
    public class ProfilesState_GRC : GameRuntimeComponent<ProfilesState_GRC>
    {
        public Hash128 ActiveProfileId;
    }
}
