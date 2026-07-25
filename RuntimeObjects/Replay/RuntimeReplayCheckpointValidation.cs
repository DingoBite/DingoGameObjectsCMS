using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    /// <summary>
    /// Optional first phase for checkpoint participants that publish immutable
    /// facts derived from one section for validation by other sections.
    /// </summary>
    public interface IRuntimeReplayCheckpointValidationContextContributor
    {
        void ContributeValidationContext(
            RuntimeReplayCheckpointReader reader,
            RuntimeReplayCheckpointValidationContext context);
    }

    /// <summary>
    /// Optional second validation phase. It runs only after every section has
    /// passed its normal prevalidation and every contributor has published its
    /// immutable validation facts.
    /// </summary>
    public interface IRuntimeReplayCheckpointContextPrevalidator
    {
        void Prevalidate(
            RuntimeReplayCheckpointReader reader,
            RuntimeReplayCheckpointValidationContext context);
    }

    public class RuntimeReplayCheckpointValidationContext
    {
        private readonly Dictionary<object, object> _values = new();

        public void Publish<T>(object key, T value)
            where T : class
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            if (!_values.TryAdd(key, value))
            {
                throw new InvalidOperationException(
                    "Checkpoint validation context key was published twice.");
            }
        }

        public T Require<T>(object key)
            where T : class
        {
            if (key == null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (!_values.TryGetValue(key, out var value)
                || value is not T typed)
            {
                throw new InvalidOperationException(
                    $"Checkpoint validation context has no '{typeof(T).FullName}' value for the requested link.");
            }

            return typed;
        }
    }

    public class RuntimeReplayReferenceClosureBinding
    {
        public void Publish(
            RuntimeReplayCheckpointValidationContext context,
            RuntimeReplayObjectReferenceClosure closure)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            context.Publish(this, closure);
        }

        public RuntimeReplayObjectReferenceClosure Require(
            RuntimeReplayCheckpointValidationContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.Require<RuntimeReplayObjectReferenceClosure>(this);
        }
    }

    public class RuntimeReplayObjectReferenceClosureEntry
    {
        private readonly HashSet<uint> _runtimeComponentTypeIds;

        public RuntimeReplayObjectRef Reference { get; }

        public RuntimeReplayObjectReferenceClosureEntry(
            RuntimeReplayObjectRef reference,
            IReadOnlyCollection<uint> runtimeComponentTypeIds)
        {
            if (!reference.IsValid)
            {
                throw new ArgumentException(
                    "Reference-closure entries require a stable object reference.",
                    nameof(reference));
            }
            if (runtimeComponentTypeIds == null)
            {
                throw new ArgumentNullException(
                    nameof(runtimeComponentTypeIds));
            }

            Reference = reference;
            _runtimeComponentTypeIds = new HashSet<uint>();
            foreach (var typeId in runtimeComponentTypeIds)
            {
                if (!_runtimeComponentTypeIds.Add(typeId))
                {
                    throw new ArgumentException(
                        $"Reference-closure entry '{reference.StoreId}/{reference.InstanceGuid}' "
                        + $"contains duplicate component type id {typeId}.",
                        nameof(runtimeComponentTypeIds));
                }
            }
        }

        public bool HasRuntimeComponent<T>()
            where T : GameRuntimeComponent
        {
            return RuntimeComponentTypeRegistry.TryGetId(
                       typeof(T),
                       out var typeId)
                   && _runtimeComponentTypeIds.Contains(typeId);
        }
    }

    public class RuntimeReplayObjectReferenceClosure
    {
        private readonly Dictionary<
            RuntimeReplayStableObjectKey,
            RuntimeReplayObjectReferenceClosureEntry> _entries = new();

        public int Count => _entries.Count;

        public RuntimeReplayObjectReferenceClosure(
            IReadOnlyList<RuntimeReplayObjectReferenceClosureEntry> entries)
        {
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i]
                            ?? throw new ArgumentException(
                                $"Reference-closure entry {i} is null.",
                                nameof(entries));
                var key = new RuntimeReplayStableObjectKey(
                    entry.Reference.StoreId,
                    entry.Reference.InstanceGuid);
                if (!_entries.TryAdd(key, entry))
                {
                    throw new ArgumentException(
                        $"Reference closure contains duplicate object "
                        + $"'{entry.Reference.StoreId}/{entry.Reference.InstanceGuid}'.",
                        nameof(entries));
                }
            }
        }

        public bool Contains(RuntimeReplayObjectRef reference)
        {
            return reference.IsValid
                   && _entries.ContainsKey(
                       new RuntimeReplayStableObjectKey(
                           reference.StoreId,
                           reference.InstanceGuid));
        }

        public RuntimeReplayObjectReferenceClosureEntry Require(
            RuntimeReplayObjectRef reference,
            string label,
            bool allowDefault = false)
        {
            if (reference.IsDefault && allowDefault)
            {
                return null;
            }
            if (!reference.IsValid)
            {
                throw new FormatException(
                    $"Checkpoint {label} requires a stable runtime reference.");
            }

            var key = new RuntimeReplayStableObjectKey(
                reference.StoreId,
                reference.InstanceGuid);
            if (!_entries.TryGetValue(key, out var entry))
            {
                throw new InvalidOperationException(
                    $"Checkpoint {label} reference "
                    + $"'{reference.StoreId}/{reference.InstanceGuid}' is outside "
                    + "the authoritative RuntimeStore closure.");
            }

            return entry;
        }

        public void RequireRuntimeComponent<T>(
            RuntimeReplayObjectRef reference,
            string label)
            where T : GameRuntimeComponent
        {
            var entry = Require(reference, label);
            if (!entry.HasRuntimeComponent<T>())
            {
                throw new InvalidOperationException(
                    $"Checkpoint {label} reference "
                    + $"'{reference.StoreId}/{reference.InstanceGuid}' does not "
                    + $"materialize required runtime component '{typeof(T).FullName}'.");
            }
        }
    }
}
