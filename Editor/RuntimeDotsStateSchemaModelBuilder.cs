using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;
using Unity.Collections;
using Unity.Entities;

namespace DingoGameObjectsCMS.Editor
{
    public enum RuntimeDotsStateGeneratedValueKind
    {
        Boolean,
        Byte,
        SByte,
        Char,
        Int16,
        UInt16,
        Int32,
        UInt32,
        Int64,
        UInt64,
        Single,
        Double,
        FixedString32,
        Enum,
        Struct,
    }

    public class RuntimeDotsStateGeneratedFieldDescriptor
    {
        public FieldInfo Field;
        public RuntimeDotsStateGeneratedValueDescriptor ValueType;
    }

    public class RuntimeDotsStateGeneratedValueDescriptor
    {
        public Type RuntimeType;
        public Type EnumUnderlyingType;
        public RuntimeDotsStateGeneratedValueKind Kind;
        public List<RuntimeDotsStateGeneratedFieldDescriptor> Fields = new();
    }

    public class RuntimeDotsStateGeneratedComponentDescriptor
    {
        public Type RuntimeType;
        public RuntimeDotsStateComponentSchema Schema;
        public RuntimeDotsStateGeneratedValueDescriptor PersistedValue;
        public MethodInfo PersistedBufferTailValidator;
        public bool IsZeroSized;
    }

    public class RuntimeDotsStateSchemaDiscoveryResult
    {
        public List<RuntimeDotsStateGeneratedComponentDescriptor> Components =
            new();
    }

    public static class RuntimeDotsStateSchemaDiscovery
    {
        public static RuntimeDotsStateSchemaDiscoveryResult Discover(
            IEnumerable<Type> runtimeTypes,
            Func<Type, bool> requiresClassification = null)
        {
            if (runtimeTypes == null)
            {
                throw new ArgumentNullException(nameof(runtimeTypes));
            }

            var result = new RuntimeDotsStateSchemaDiscoveryResult();
            var seenTypes = new HashSet<Type>();
            foreach (var runtimeType in runtimeTypes
                         .Where(type => type != null)
                         .OrderBy(TypeSortKey, StringComparer.Ordinal))
            {
                if (!seenTypes.Add(runtimeType)
                    || !IsDotsStateComponentType(runtimeType))
                {
                    continue;
                }

                var classification = TakeClassification(runtimeType);
                if (classification == null)
                {
                    if (requiresClassification?.Invoke(runtimeType) == true)
                    {
                        throw new InvalidOperationException(
                            $"DOTS component '{runtimeType.FullName}' requires an explicit runtime state classification.");
                    }

                    continue;
                }

                var descriptor = DescribeComponent(
                    runtimeType,
                    classification.Value);

                result.Components.Add(descriptor);
            }

            result.Components.Sort((first, second) => string.CompareOrdinal(
                TypeSortKey(first.RuntimeType),
                TypeSortKey(second.RuntimeType)));
            return result;
        }

        public static RuntimeDotsStateGeneratedComponentDescriptor
            DescribeComponent(Type runtimeType)
        {
            if (runtimeType == null)
            {
                throw new ArgumentNullException(nameof(runtimeType));
            }

            var classification = TakeClassification(runtimeType);
            if (classification == null)
            {
                throw new InvalidOperationException(
                    $"DOTS component '{runtimeType.FullName}' has no runtime state classification.");
            }

            return DescribeComponent(
                runtimeType,
                classification.Value);
        }

        public static bool IsDotsStateComponentType(Type type)
        {
            if (type == null
                || !type.IsValueType
                || type.IsEnum
                || type.IsGenericTypeDefinition
                || type.ContainsGenericParameters)
            {
                return false;
            }

            return typeof(IBufferElementData).IsAssignableFrom(type)
                   || typeof(IComponentData).IsAssignableFrom(type);
        }

        private static RuntimeDotsStateGeneratedComponentDescriptor
            DescribeComponent(
                Type runtimeType,
                RuntimeDotsStateClassification classification)
        {
            if (!IsDotsStateComponentType(runtimeType))
            {
                throw new InvalidOperationException(
                    $"Type '{runtimeType.FullName}' is not a concrete unmanaged IComponentData or IBufferElementData struct.");
            }
            if (!IsPubliclyAccessible(runtimeType))
            {
                throw new InvalidOperationException(
                    $"DOTS state component '{runtimeType.FullName}' must be public so generated code can reference it.");
            }

            ValidateUnmanaged(runtimeType, new HashSet<Type>());
            if (classification == RuntimeDotsStateClassification.Persisted
                && ContainsUnstableRuntimeReference(
                    runtimeType,
                    new HashSet<Type>()))
            {
                throw new InvalidOperationException(
                    $"Persisted DOTS component '{runtimeType.FullName}' contains an Entity or RuntimeInstance handle. Persist stable RuntimeDotsStateEntityKey values instead.");
            }

            var kind = typeof(IBufferElementData).IsAssignableFrom(runtimeType)
                ? RuntimeDotsStateComponentKind.Buffer
                : RuntimeDotsStateComponentKind.Component;
            var persistedValue = classification ==
                                 RuntimeDotsStateClassification.Persisted
                ? DescribePersistedValue(
                    runtimeType,
                    new HashSet<Type>())
                : null;
            var persistedBufferTailValidator =
                DescribePersistedBufferTailValidator(
                    runtimeType,
                    classification,
                    kind);
            return new RuntimeDotsStateGeneratedComponentDescriptor
            {
                RuntimeType = runtimeType,
                Schema = new RuntimeDotsStateComponentSchema
                {
                    ComponentTypeId = -1,
                    RuntimeType = runtimeType,
                    Classification = classification,
                    Kind = kind,
                    Enableable = typeof(IEnableableComponent)
                        .IsAssignableFrom(runtimeType),
                },
                PersistedValue = persistedValue,
                PersistedBufferTailValidator =
                    persistedBufferTailValidator,
                IsZeroSized = kind == RuntimeDotsStateComponentKind.Component
                              && ComponentType.ReadOnly(runtimeType)
                                  .IsZeroSized,
            };
        }

        private static MethodInfo DescribePersistedBufferTailValidator(
            Type runtimeType,
            RuntimeDotsStateClassification classification,
            RuntimeDotsStateComponentKind kind)
        {
            var attribute = runtimeType.GetCustomAttribute<
                RuntimeDotsBufferTailValidatorAttribute>(inherit: false);
            if (attribute == null)
            {
                return null;
            }
            if (classification != RuntimeDotsStateClassification.Persisted
                || kind != RuntimeDotsStateComponentKind.Buffer)
            {
                throw new InvalidOperationException(
                    $"DOTS buffer tail validator on '{runtimeType.FullName}' "
                    + "requires a persisted IBufferElementData type.");
            }
            if (!IsPubliclyAccessible(attribute.ValidatorType)
                || attribute.ValidatorType.ContainsGenericParameters)
            {
                throw new InvalidOperationException(
                    $"DOTS buffer tail validator type "
                    + $"'{attribute.ValidatorType.FullName}' for "
                    + $"'{runtimeType.FullName}' must be publicly accessible "
                    + "and closed so generated code can call it directly.");
            }

            var parameterType = runtimeType.MakeByRefType();
            var methods = attribute.ValidatorType.GetMethods(
                    BindingFlags.Public
                    | BindingFlags.Static
                    | BindingFlags.DeclaredOnly)
                .Where(method => string.Equals(
                    method.Name,
                    attribute.MethodName,
                    StringComparison.Ordinal))
                .Where(method => !method.IsGenericMethodDefinition
                                 && !method.ContainsGenericParameters
                                 && method.ReturnType == typeof(bool))
                .Where(method =>
                {
                    var parameters = method.GetParameters();
                    return parameters.Length == 1
                           && parameters[0].ParameterType == parameterType
                           && parameters[0].IsIn
                           && !parameters[0].IsOut;
                })
                .ToArray();
            if (methods.Length != 1)
            {
                throw new InvalidOperationException(
                    $"DOTS buffer tail validator "
                    + $"'{attribute.ValidatorType.FullName}."
                    + $"{attribute.MethodName}' for '{runtimeType.FullName}' "
                    + $"must expose exactly one public static bool "
                    + $"{attribute.MethodName}(in {runtimeType.FullName}) "
                    + "overload.");
            }

            return methods[0];
        }

        private static RuntimeDotsStateGeneratedValueDescriptor
            DescribePersistedValue(
                Type type,
                HashSet<Type> traversal)
        {
            var primitive = DescribePrimitive(type);
            if (primitive != null)
            {
                return primitive;
            }
            if (type.IsEnum)
            {
                return new RuntimeDotsStateGeneratedValueDescriptor
                {
                    RuntimeType = type,
                    EnumUnderlyingType = Enum.GetUnderlyingType(type),
                    Kind = RuntimeDotsStateGeneratedValueKind.Enum,
                };
            }
            if (type == typeof(Entity))
            {
                throw new InvalidOperationException(
                    $"Persisted DOTS value '{type.FullName}' cannot contain Entity.");
            }
            if (!type.IsValueType
                || type.IsPointer
                || type == typeof(IntPtr)
                || type == typeof(UIntPtr)
                || type == typeof(decimal))
            {
                throw new InvalidOperationException(
                    $"Persisted DOTS value '{type.FullName}' has no supported canonical field codec.");
            }
            if (!traversal.Add(type))
            {
                throw new InvalidOperationException(
                    $"Persisted DOTS value '{type.FullName}' has a recursive layout.");
            }

            var result = new RuntimeDotsStateGeneratedValueDescriptor
            {
                RuntimeType = type,
                Kind = RuntimeDotsStateGeneratedValueKind.Struct,
            };
            var fields = TakeInstanceFields(type);
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                if (!field.IsPublic || field.IsInitOnly || field.IsLiteral)
                {
                    throw new InvalidOperationException(
                        $"Persisted DOTS field '{type.FullName}.{field.Name}' must be a mutable public field for generated canonical codecs.");
                }

                result.Fields.Add(
                    new RuntimeDotsStateGeneratedFieldDescriptor
                    {
                        Field = field,
                        ValueType = DescribePersistedValue(
                            field.FieldType,
                            traversal),
                    });
            }
            traversal.Remove(type);
            return result;
        }

        private static RuntimeDotsStateGeneratedValueDescriptor
            DescribePrimitive(Type type)
        {
            if (type == typeof(bool))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.Boolean);
            if (type == typeof(byte))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.Byte);
            if (type == typeof(sbyte))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.SByte);
            if (type == typeof(char))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.Char);
            if (type == typeof(short))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.Int16);
            if (type == typeof(ushort))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.UInt16);
            if (type == typeof(int))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.Int32);
            if (type == typeof(uint))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.UInt32);
            if (type == typeof(long))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.Int64);
            if (type == typeof(ulong))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.UInt64);
            if (type == typeof(float))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.Single);
            if (type == typeof(double))
                return Primitive(type, RuntimeDotsStateGeneratedValueKind.Double);
            if (type == typeof(FixedString32Bytes))
                return Primitive(
                    type,
                    RuntimeDotsStateGeneratedValueKind.FixedString32);
            return null;
        }

        private static RuntimeDotsStateGeneratedValueDescriptor Primitive(
            Type type,
            RuntimeDotsStateGeneratedValueKind kind)
        {
            return new RuntimeDotsStateGeneratedValueDescriptor
            {
                RuntimeType = type,
                Kind = kind,
            };
        }

        private static RuntimeDotsStateClassification? TakeClassification(
            Type type)
        {
            var classifications =
                new List<RuntimeDotsStateClassification>();
            var persisted = type.GetCustomAttribute<
                RuntimeDotsPersistedAttribute>(inherit: false);
            if (persisted != null)
            {
                classifications.Add(
                    RuntimeDotsStateClassification.Persisted);
            }
            if (type.IsDefined(
                    typeof(RuntimeDotsDerivedAttribute),
                    inherit: false))
            {
                classifications.Add(
                    RuntimeDotsStateClassification.Derived);
            }
            if (type.IsDefined(
                    typeof(RuntimeDotsTransientAttribute),
                    inherit: false))
            {
                classifications.Add(
                    RuntimeDotsStateClassification.Transient);
            }
            if (type.IsDefined(
                    typeof(RuntimeDotsPresentationAttribute),
                    inherit: false))
            {
                classifications.Add(
                    RuntimeDotsStateClassification.Presentation);
            }

            if (classifications.Count > 1)
            {
                throw new InvalidOperationException(
                    $"DOTS component '{type.FullName}' has more than one runtime state classification.");
            }

            return classifications.Count == 0
                ? null
                : classifications[0];
        }

        private static void ValidateUnmanaged(
            Type type,
            HashSet<Type> traversal)
        {
            if (type.IsPrimitive || type.IsEnum || type.IsPointer)
            {
                return;
            }
            if (!type.IsValueType || type.IsByRefLike)
            {
                throw new InvalidOperationException(
                    $"DOTS state component value '{type.FullName}' is not unmanaged.");
            }
            if (!traversal.Add(type))
            {
                throw new InvalidOperationException(
                    $"DOTS state component value '{type.FullName}' has a recursive layout.");
            }

            var fields = TakeInstanceFields(type);
            for (var i = 0; i < fields.Length; i++)
            {
                ValidateUnmanaged(fields[i].FieldType, traversal);
            }
            traversal.Remove(type);
        }

        private static bool ContainsUnstableRuntimeReference(
            Type type,
            HashSet<Type> traversal)
        {
            if (type == typeof(Entity)
                || type == typeof(global::DingoGameObjectsCMS.RuntimeObjects.RuntimeInstance))
            {
                return true;
            }
            if (type.IsPrimitive
                || type.IsEnum
                || type.IsPointer
                || !type.IsValueType)
            {
                return false;
            }
            if (!traversal.Add(type))
            {
                return false;
            }

            var fields = TakeInstanceFields(type);
            for (var i = 0; i < fields.Length; i++)
            {
                if (ContainsUnstableRuntimeReference(
                        fields[i].FieldType,
                        traversal))
                {
                    traversal.Remove(type);
                    return true;
                }
            }
            traversal.Remove(type);
            return false;
        }

        private static FieldInfo[] TakeInstanceFields(Type type)
        {
            return type.GetFields(
                    BindingFlags.Instance
                    | BindingFlags.Public
                    | BindingFlags.NonPublic)
                .OrderBy(field => field.Name, StringComparer.Ordinal)
                .ThenBy(field => field.FieldType.FullName, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsPubliclyAccessible(Type type)
        {
            if (type.IsNested)
            {
                return type.IsNestedPublic
                       && IsPubliclyAccessible(type.DeclaringType);
            }

            return type.IsPublic;
        }

        private static string TypeSortKey(Type type)
        {
            return $"{type.Assembly.GetName().Name}:{type.FullName}";
        }
    }
}
