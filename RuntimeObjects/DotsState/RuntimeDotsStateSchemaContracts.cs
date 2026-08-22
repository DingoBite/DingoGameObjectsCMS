using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using Unity.Collections;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.DotsState
{
    public static class RuntimeDotsStateSerializationLimits
    {
        public const int MAX_BUFFER_ELEMENTS = 16 * 1024 * 1024;
    }

    public enum RuntimeDotsStateClassification : byte
    {
        Persisted = 1,
        Derived = 2,
        Transient = 3,
        Presentation = 4,
    }

    public enum RuntimeDotsStateComponentKind : byte
    {
        Component = 1,
        Buffer = 2,
    }

    [Serializable, Preserve]
    public struct RuntimeDotsStateEntityKey :
        IEquatable<RuntimeDotsStateEntityKey>
    {
        public FixedString32Bytes StoreId;
        public long FactoryId;
        public ulong ProductId;

        public RuntimeDotsStateEntityKey(
            FixedString32Bytes storeId,
            long factoryId,
            ulong productId)
        {
            StoreId = storeId;
            FactoryId = factoryId;
            ProductId = productId;
        }

        public bool Equals(RuntimeDotsStateEntityKey other)
        {
            return StoreId.Equals(other.StoreId)
                   && FactoryId == other.FactoryId
                   && ProductId == other.ProductId;
        }

        public override bool Equals(object obj)
        {
            return obj is RuntimeDotsStateEntityKey other
                   && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                var hashCode = StoreId.GetHashCode();
                hashCode = (hashCode * 397) ^ FactoryId.GetHashCode();
                hashCode = (hashCode * 397) ^ ProductId.GetHashCode();
                return hashCode;
            }
        }

        public static bool operator ==(
            RuntimeDotsStateEntityKey left,
            RuntimeDotsStateEntityKey right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(
            RuntimeDotsStateEntityKey left,
            RuntimeDotsStateEntityKey right)
        {
            return !left.Equals(right);
        }
    }

    [Serializable, Preserve]
    public class RuntimeDotsStateSchemaManifest
    {
        public int FormatVersion;
        public int CodecVersion;
        public string SchemaHash;
        public List<RuntimeDotsStateComponentSchema> Components = new();
        public List<int> ReservedComponentTypeIds = new();
    }

    [Serializable, Preserve]
    public class RuntimeDotsStateComponentSchema
    {
        public int ComponentTypeId;
        [NonSerialized] public Type RuntimeType;
        public RuntimeDotsStateClassification Classification;
        public RuntimeDotsStateComponentKind Kind;
        public bool Enableable;
        public ulong LayoutHash;
    }

    public readonly struct RuntimeDotsStateComponentDescriptor
    {
        public readonly int ComponentTypeId;
        public readonly Type RuntimeType;
        public readonly RuntimeDotsStateClassification Classification;
        public readonly RuntimeDotsStateComponentKind Kind;
        public readonly bool Enableable;

        private RuntimeDotsStateComponentDescriptor(
            int componentTypeId,
            Type runtimeType,
            RuntimeDotsStateClassification classification,
            RuntimeDotsStateComponentKind kind,
            bool enableable)
        {
            if (componentTypeId < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(componentTypeId));
            }
            if (runtimeType == null)
            {
                throw new ArgumentNullException(nameof(runtimeType));
            }

            ComponentTypeId = componentTypeId;
            RuntimeType = runtimeType;
            Classification = classification;
            Kind = kind;
            Enableable = enableable;
        }

        public static RuntimeDotsStateComponentDescriptor Component<T>(
            int componentTypeId,
            RuntimeDotsStateClassification classification,
            bool enableable)
            where T : unmanaged, IComponentData
        {
            return new RuntimeDotsStateComponentDescriptor(
                componentTypeId,
                typeof(T),
                classification,
                RuntimeDotsStateComponentKind.Component,
                enableable);
        }

        public static RuntimeDotsStateComponentDescriptor Buffer<T>(
            int componentTypeId,
            RuntimeDotsStateClassification classification,
            bool enableable)
            where T : unmanaged, IBufferElementData
        {
            return new RuntimeDotsStateComponentDescriptor(
                componentTypeId,
                typeof(T),
                classification,
                RuntimeDotsStateComponentKind.Buffer,
                enableable);
        }
    }

    public delegate void RuntimeDotsStateWriteValue<T>(
        RuntimeReplayCheckpointWriter writer,
        in T value)
        where T : unmanaged;

    public delegate T RuntimeDotsStateReadValue<T>(
        RuntimeReplayCheckpointReader reader)
        where T : unmanaged;

    public sealed class RuntimeDotsStateComponentCodec<T>
        where T : unmanaged
    {
        public readonly RuntimeDotsStateWriteValue<T> Write;
        public readonly RuntimeDotsStateReadValue<T> Read;

        public RuntimeDotsStateComponentCodec(
            RuntimeDotsStateWriteValue<T> write,
            RuntimeDotsStateReadValue<T> read)
        {
            Write = write ?? throw new ArgumentNullException(nameof(write));
            Read = read ?? throw new ArgumentNullException(nameof(read));
        }
    }

    public class RuntimeDotsStateSchemaRegistry
    {
        private readonly Dictionary<int, RuntimeDotsStateComponentDescriptor>
            _byId = new();
        private readonly Dictionary<Type, RuntimeDotsStateComponentDescriptor>
            _byType = new();
        private readonly Dictionary<Type, object> _codecs = new();

        public string SchemaHash { get; }
        public int Count => _byId.Count;

        public RuntimeDotsStateSchemaRegistry(string schemaHash)
        {
            if (!IsSha256Hex(schemaHash))
            {
                throw new ArgumentException(
                    "A DOTS state schema hash must be lowercase SHA-256 hex.",
                    nameof(schemaHash));
            }

            SchemaHash = schemaHash;
        }

        public void Register(
            in RuntimeDotsStateComponentDescriptor descriptor)
        {
            if (descriptor.Classification ==
                RuntimeDotsStateClassification.Persisted)
            {
                throw new InvalidOperationException(
                    $"Persisted DOTS state component type '{descriptor.RuntimeType.FullName}' requires a canonical typed codec.");
            }

            RegisterDescriptor(descriptor);
        }

        private void RegisterDescriptor(
            in RuntimeDotsStateComponentDescriptor descriptor)
        {
            if (_byId.ContainsKey(descriptor.ComponentTypeId))
            {
                throw new InvalidOperationException(
                    $"DOTS state component id {descriptor.ComponentTypeId} is already registered.");
            }
            if (_byType.ContainsKey(descriptor.RuntimeType))
            {
                throw new InvalidOperationException(
                    $"DOTS state component type '{descriptor.RuntimeType.FullName}' is already registered.");
            }

            _byId.Add(descriptor.ComponentTypeId, descriptor);
            _byType.Add(descriptor.RuntimeType, descriptor);
        }

        public void Register<T>(
            in RuntimeDotsStateComponentDescriptor descriptor,
            RuntimeDotsStateComponentCodec<T> codec)
            where T : unmanaged
        {
            if (descriptor.RuntimeType != typeof(T))
            {
                throw new ArgumentException(
                    $"DOTS state codec type '{typeof(T).FullName}' does not match descriptor type '{descriptor.RuntimeType?.FullName}'.",
                    nameof(codec));
            }
            if (codec == null)
            {
                throw new ArgumentNullException(nameof(codec));
            }

            RegisterDescriptor(descriptor);
            _codecs.Add(typeof(T), codec);
        }

        public bool TryTakeCodec<T>(
            out RuntimeDotsStateComponentCodec<T> codec)
            where T : unmanaged
        {
            if (_codecs.TryGetValue(typeof(T), out var value))
            {
                codec = (RuntimeDotsStateComponentCodec<T>)value;
                return true;
            }

            codec = null;
            return false;
        }

        public RuntimeDotsStateComponentCodec<T> TakeCodec<T>()
            where T : unmanaged
        {
            if (!TryTakeCodec<T>(out var codec))
            {
                throw new KeyNotFoundException(
                    $"DOTS state component type '{typeof(T).FullName}' has no registered canonical codec.");
            }

            return codec;
        }

        public RuntimeDotsStateComponentDescriptor TakeById(int id)
        {
            if (!_byId.TryGetValue(id, out var descriptor))
            {
                throw new KeyNotFoundException(
                    $"DOTS state component id {id} is not registered.");
            }

            return descriptor;
        }

        public RuntimeDotsStateComponentDescriptor TakeByType(Type type)
        {
            if (type == null
                || !_byType.TryGetValue(type, out var descriptor))
            {
                throw new KeyNotFoundException(
                    $"DOTS state component type '{type?.FullName}' is not registered.");
            }

            return descriptor;
        }

        private static bool IsSha256Hex(string value)
        {
            if (value == null || value.Length != 64)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (!(c >= '0' && c <= '9'
                      || c >= 'a' && c <= 'f'))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
