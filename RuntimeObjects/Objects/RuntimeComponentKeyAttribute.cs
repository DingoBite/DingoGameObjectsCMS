using System;

namespace DingoGameObjectsCMS.RuntimeObjects.Objects
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    public sealed class RuntimeComponentKeyAttribute : Attribute
    {
        public readonly string Key;

        public RuntimeComponentKeyAttribute(string key)
        {
            Key = key;
        }
    }
}
