using System;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Examples.Profiles.Components
{
    [Serializable, Preserve, RuntimeComponentKey("dingo.examples.profiles.user-profile")]
    public class UserProfile_GRC : GameRuntimeComponent<UserProfile_GRC>
    {
    }
}
