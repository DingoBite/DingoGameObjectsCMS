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
    public sealed class RuntimeDotsBufferTailValidatorAttribute : Attribute
    {
        public readonly Type ValidatorType;
        public readonly string MethodName;

        public RuntimeDotsBufferTailValidatorAttribute(
            Type validatorType,
            string methodName)
        {
            ValidatorType = validatorType ?? throw new ArgumentNullException(
                nameof(validatorType));
            if (string.IsNullOrWhiteSpace(methodName))
            {
                throw new ArgumentException(
                    "A persisted DOTS buffer tail validator method is required.",
                    nameof(methodName));
            }

            MethodName = methodName.Trim();
        }
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
