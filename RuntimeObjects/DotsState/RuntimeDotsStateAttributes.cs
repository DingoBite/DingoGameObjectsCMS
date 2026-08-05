using System;

namespace DingoGameObjectsCMS.RuntimeObjects.DotsState
{
    [AttributeUsage(
        AttributeTargets.Struct,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class RuntimeDotsPersistedAttribute : Attribute
    {
    }

    [AttributeUsage(
        AttributeTargets.Struct,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class RuntimeDotsDerivedAttribute : Attribute
    {
    }

    [AttributeUsage(
        AttributeTargets.Struct,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class RuntimeDotsTransientAttribute : Attribute
    {
    }

    [AttributeUsage(
        AttributeTargets.Struct,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class RuntimeDotsPresentationAttribute : Attribute
    {
    }
}
