using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Objects;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public enum RuntimeComponentPatchProjectionMode : byte
    {
        // Materializes the baseline and delegates value comparison/encoding to
        // the generated component codec.
        SemanticDiff = 1,

        // Compares component type-id membership only. Codec value methods are
        // deliberately outside this path.
        StructuralPresence = 2,

        // Removes an inherited baseline component and omits runtime-only data.
        Excluded = 3,
    }

    public abstract class RuntimeComponentPatchCodec
    {
        public readonly uint ComponentTypeId;
        public readonly string ComponentTypeKey;
        public abstract Type ComponentRuntimeType { get; }

        protected RuntimeComponentPatchCodec(uint componentTypeId, string componentTypeKey)
        {
            if (string.IsNullOrWhiteSpace(componentTypeKey))
                throw new ArgumentException("Runtime component patch key is required.", nameof(componentTypeKey));
            ComponentTypeId = componentTypeId;
            ComponentTypeKey = componentTypeKey;
        }

        public ComponentPatch BuildPatch(
            GameRuntimeComponent baseline,
            GameRuntimeComponent current,
            RuntimePatchCodecContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (baseline == null && current == null)
                return null;

            if (baseline == null)
                return new ComponentPatch(ComponentTypeId, ComponentTypeKey, ComponentPatchKind.Add, EncodeCanonical(current, context));

            if (current == null)
                return new ComponentPatch(ComponentTypeId, ComponentTypeKey, ComponentPatchKind.Remove);

            if (TryBuildCustomPatch(baseline, current, context, out var customPayload))
            {
                if (customPayload == null)
                    throw new InvalidOperationException($"Component {ComponentTypeKey} custom codec returned a null payload.");
                return new ComponentPatch(ComponentTypeId, ComponentTypeKey, ComponentPatchKind.Custom, customPayload);
            }

            var fields = new List<FieldPatch>();
            CollectFieldPatches(baseline, current, fields, context);
            NormalizeFields(fields);
            if (fields.Count == 0)
                return null;

            return new ComponentPatch(ComponentTypeId, ComponentTypeKey, ComponentPatchKind.Fields)
            {
                Fields = fields
            };
        }

        public GameRuntimeComponent ApplyPatch(
            GameRuntimeComponent baseline,
            ComponentPatch patch,
            RuntimePatchCodecContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (patch == null)
                return baseline == null ? null : Clone(baseline);
            if (patch.ComponentTypeId != ComponentTypeId)
                throw new InvalidOperationException($"Codec {ComponentTypeId} cannot apply component patch {patch.ComponentTypeId}.");
            if (!string.Equals(patch.ComponentTypeKey, ComponentTypeKey, StringComparison.Ordinal))
                throw new InvalidOperationException($"Codec '{ComponentTypeKey}' cannot apply component patch '{patch.ComponentTypeKey}'.");
            ValidatePatchShape(patch);

            switch (patch.Kind)
            {
                case ComponentPatchKind.Add:
                    if (baseline != null)
                        throw new InvalidOperationException($"Component {ComponentTypeId} add patch requires an absent baseline component.");
                    return DecodeCanonical(patch.Payload, context);

                case ComponentPatchKind.AddPresence:
                    if (baseline != null)
                        throw new InvalidOperationException($"Component {ComponentTypeId} presence add requires an absent baseline component.");
                    return CreateDefault();

                case ComponentPatchKind.Remove:
                    if (baseline == null)
                        throw new InvalidOperationException($"Component {ComponentTypeId} remove patch requires a baseline component.");
                    return null;

                case ComponentPatchKind.Fields:
                    if (baseline == null)
                        throw new InvalidOperationException($"Component {ComponentTypeId} field patch requires a baseline component.");
                    var target = Clone(baseline);
                    var fields = patch.Fields == null ? new List<FieldPatch>() : new List<FieldPatch>(patch.Fields);
                    NormalizeFields(fields);
                    for (var i = 0; i < fields.Count; i++)
                    {
                        ValidateFieldKind(fields[i].Kind);
                        ApplyFieldPatch(target, fields[i], context);
                    }
                    return target;

                case ComponentPatchKind.Custom:
                    if (baseline == null)
                        throw new InvalidOperationException($"Component {ComponentTypeKey} custom patch requires a baseline component.");
                    return ApplyCustomPatch(baseline, patch.Payload, context);

                default:
                    throw new InvalidOperationException($"Component {ComponentTypeId} patch has unsupported kind {patch.Kind}.");
            }
        }

        public byte[] EncodeCanonical(GameRuntimeComponent value, RuntimePatchCodecContext context)
        {
            if (value == null)
                throw new ArgumentNullException(nameof(value));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            var writer = new CanonicalPatchBinaryWriter();
            WriteCanonical(writer, value, context);
            return writer.ToArray();
        }

        public GameRuntimeComponent DecodeCanonical(byte[] payload, RuntimePatchCodecContext context)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            var reader = new CanonicalPatchBinaryReader(payload);
            var value = ReadCanonical(reader, context);
            reader.RequireEnd();
            return value;
        }

        public virtual bool TryGetFieldKey(uint fieldId, out string fieldKey)
        {
            fieldKey = null;
            return false;
        }

        public virtual bool TryGetFieldId(string fieldKey, out uint fieldId)
        {
            fieldId = 0;
            return false;
        }

        public abstract GameRuntimeComponent Clone(GameRuntimeComponent value);
        public abstract GameRuntimeComponent CreateDefault();
        protected abstract void WriteCanonical(
            CanonicalPatchBinaryWriter writer,
            GameRuntimeComponent value,
            RuntimePatchCodecContext context);
        protected abstract GameRuntimeComponent ReadCanonical(
            CanonicalPatchBinaryReader reader,
            RuntimePatchCodecContext context);
        protected abstract void CollectFieldPatches(
            GameRuntimeComponent baseline,
            GameRuntimeComponent current,
            List<FieldPatch> fields,
            RuntimePatchCodecContext context);
        protected abstract void ApplyFieldPatch(
            GameRuntimeComponent target,
            FieldPatch fieldPatch,
            RuntimePatchCodecContext context);
        protected virtual bool TryBuildCustomPatch(
            GameRuntimeComponent baseline,
            GameRuntimeComponent current,
            RuntimePatchCodecContext context,
            out byte[] payload)
        {
            payload = null;
            return false;
        }
        protected virtual GameRuntimeComponent ApplyCustomPatch(
            GameRuntimeComponent baseline,
            byte[] payload,
            RuntimePatchCodecContext context)
        {
            throw new InvalidOperationException($"Component {ComponentTypeKey} does not support custom patches.");
        }

        private static void NormalizeFields(List<FieldPatch> fields)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i] == null)
                    throw new InvalidOperationException("Component field patches cannot contain null entries.");
                if (string.IsNullOrWhiteSpace(fields[i].FieldKey))
                    throw new InvalidOperationException($"Component field patch {fields[i].FieldId} has no stable field key.");
                if (!keys.Add(fields[i].FieldKey))
                    throw new InvalidOperationException($"Component patch contains duplicate field key '{fields[i].FieldKey}'.");
            }
            fields.Sort(CompareFields);

            for (var i = 1; i < fields.Count; i++)
            {
                if (fields[i - 1].FieldId == fields[i].FieldId)
                    throw new InvalidOperationException($"Component patch contains duplicate field id {fields[i].FieldId}.");
            }
        }

        private static void ValidateFieldKind(FieldPatchKind kind)
        {
            if (kind != FieldPatchKind.Set && kind != FieldPatchKind.Remove)
                throw new InvalidOperationException($"Unsupported field patch kind {kind}.");
        }

        private static void ValidatePatchShape(ComponentPatch patch)
        {
            if (patch.CanonicalJson != null)
                throw new InvalidOperationException($"Runtime-binary component {patch.ComponentTypeId} cannot contain authoring JSON.");
            var fieldCount = patch.Fields?.Count ?? 0;
            switch (patch.Kind)
            {
                case ComponentPatchKind.Add:
                case ComponentPatchKind.Custom:
                    if (patch.Payload == null || fieldCount != 0)
                        throw new InvalidOperationException($"Component {patch.ComponentTypeId} {patch.Kind} patch requires one canonical payload and no field patches.");
                    return;

                case ComponentPatchKind.AddPresence:
                    if (patch.Payload != null || fieldCount != 0)
                        throw new InvalidOperationException($"Component {patch.ComponentTypeId} presence add cannot contain payload data.");
                    return;

                case ComponentPatchKind.Remove:
                    if (patch.Payload != null || fieldCount != 0)
                        throw new InvalidOperationException($"Component {patch.ComponentTypeId} remove patch cannot contain payload data.");
                    return;

                case ComponentPatchKind.Fields:
                    if (patch.Payload != null || fieldCount == 0)
                        throw new InvalidOperationException($"Component {patch.ComponentTypeId} field patch requires at least one field and no component payload.");
                    for (var i = 0; i < patch.Fields.Count; i++)
                    {
                        var field = patch.Fields[i];
                        if (field == null)
                            throw new InvalidOperationException($"Component {patch.ComponentTypeId} field patch cannot contain null entries.");
                        if (field.CanonicalJson != null)
                            throw new InvalidOperationException($"Runtime-binary field {field.FieldId} cannot contain authoring JSON.");
                        ValidateFieldKind(field.Kind);
                        if (field.Kind == FieldPatchKind.Set && field.Payload == null)
                            throw new InvalidOperationException($"Field {field.FieldId} set patch requires a canonical payload.");
                        if (field.Kind == FieldPatchKind.Remove && field.Payload != null)
                            throw new InvalidOperationException($"Field {field.FieldId} remove patch cannot contain a payload.");
                    }
                    return;

                default:
                    throw new InvalidOperationException($"Unsupported component patch kind {patch.Kind}.");
            }
        }

        private static int CompareFields(FieldPatch first, FieldPatch second)
        {
            var byId = first.FieldId.CompareTo(second.FieldId);
            return byId != 0 ? byId : string.CompareOrdinal(first.FieldKey, second.FieldKey);
        }
    }

    public abstract class RuntimeComponentPatchCodec<T> : RuntimeComponentPatchCodec where T : GameRuntimeComponent, new()
    {
        public override Type ComponentRuntimeType => typeof(T);

        protected RuntimeComponentPatchCodec(uint componentTypeId, string componentTypeKey) : base(componentTypeId, componentTypeKey) { }

        public override GameRuntimeComponent Clone(GameRuntimeComponent value)
        {
            return CloneTyped(RequireTyped(value));
        }

        public override GameRuntimeComponent CreateDefault()
        {
            return new T();
        }

        protected override void WriteCanonical(
            CanonicalPatchBinaryWriter writer,
            GameRuntimeComponent value,
            RuntimePatchCodecContext context)
        {
            WriteCanonicalTyped(writer, RequireTyped(value), context);
        }

        protected override GameRuntimeComponent ReadCanonical(
            CanonicalPatchBinaryReader reader,
            RuntimePatchCodecContext context)
        {
            return ReadCanonicalTyped(reader, context);
        }

        protected override void CollectFieldPatches(
            GameRuntimeComponent baseline,
            GameRuntimeComponent current,
            List<FieldPatch> fields,
            RuntimePatchCodecContext context)
        {
            CollectFieldPatchesTyped(RequireTyped(baseline), RequireTyped(current), fields, context);
        }

        protected override void ApplyFieldPatch(
            GameRuntimeComponent target,
            FieldPatch fieldPatch,
            RuntimePatchCodecContext context)
        {
            ApplyFieldPatchTyped(RequireTyped(target), fieldPatch, context);
        }

        protected override bool TryBuildCustomPatch(
            GameRuntimeComponent baseline,
            GameRuntimeComponent current,
            RuntimePatchCodecContext context,
            out byte[] payload)
        {
            return TryBuildCustomPatchTyped(RequireTyped(baseline), RequireTyped(current), context, out payload);
        }

        protected override GameRuntimeComponent ApplyCustomPatch(
            GameRuntimeComponent baseline,
            byte[] payload,
            RuntimePatchCodecContext context)
        {
            return ApplyCustomPatchTyped(RequireTyped(baseline), payload, context);
        }

        protected abstract T CloneTyped(T value);
        protected abstract void WriteCanonicalTyped(
            CanonicalPatchBinaryWriter writer,
            T value,
            RuntimePatchCodecContext context);
        protected abstract T ReadCanonicalTyped(
            CanonicalPatchBinaryReader reader,
            RuntimePatchCodecContext context);
        protected abstract void CollectFieldPatchesTyped(
            T baseline,
            T current,
            List<FieldPatch> fields,
            RuntimePatchCodecContext context);
        protected abstract void ApplyFieldPatchTyped(
            T target,
            FieldPatch fieldPatch,
            RuntimePatchCodecContext context);
        protected virtual bool TryBuildCustomPatchTyped(
            T baseline,
            T current,
            RuntimePatchCodecContext context,
            out byte[] payload)
        {
            payload = null;
            return false;
        }
        protected virtual T ApplyCustomPatchTyped(
            T baseline,
            byte[] payload,
            RuntimePatchCodecContext context)
        {
            throw new InvalidOperationException($"Component {ComponentTypeKey} does not support custom patches.");
        }

        private static T RequireTyped(GameRuntimeComponent value)
        {
            if (value is T typed)
                return typed;
            throw new InvalidOperationException($"Patch codec expected {typeof(T).FullName}, received {value?.GetType().FullName ?? "null"}.");
        }
    }

    public class RuntimePatchCodecRegistry
    {
        private readonly Dictionary<uint, RuntimeComponentPatchCodec> _codecs = new();
        private readonly Dictionary<string, RuntimeComponentPatchCodec> _codecsByKey = new(StringComparer.Ordinal);

        public readonly string SchemaHash;

        public int Count => _codecs.Count;

        public RuntimePatchCodecRegistry(string schemaHash)
        {
            if (string.IsNullOrWhiteSpace(schemaHash))
                throw new ArgumentException("Runtime patch schema hash is required.", nameof(schemaHash));
            SchemaHash = schemaHash;
        }

        public void Register(RuntimeComponentPatchCodec codec)
        {
            if (codec == null)
                throw new ArgumentNullException(nameof(codec));
            if (_codecs.ContainsKey(codec.ComponentTypeId))
                throw new InvalidOperationException($"Runtime component patch codec {codec.ComponentTypeId} is already registered.");
            if (_codecsByKey.ContainsKey(codec.ComponentTypeKey))
                throw new InvalidOperationException($"Runtime component patch codec '{codec.ComponentTypeKey}' is already registered.");
            _codecs.Add(codec.ComponentTypeId, codec);
            _codecsByKey.Add(codec.ComponentTypeKey, codec);
        }

        public bool TryGet(uint componentTypeId, out RuntimeComponentPatchCodec codec)
        {
            return _codecs.TryGetValue(componentTypeId, out codec);
        }

        public RuntimeComponentPatchCodec Get(uint componentTypeId)
        {
            if (_codecs.TryGetValue(componentTypeId, out var codec))
                return codec;
            throw new KeyNotFoundException($"Runtime component patch codec {componentTypeId} is not registered.");
        }

        public bool TryGet(string componentTypeKey, out RuntimeComponentPatchCodec codec)
        {
            return _codecsByKey.TryGetValue(componentTypeKey, out codec);
        }

        public RuntimeComponentPatchCodec Get(string componentTypeKey)
        {
            if (_codecsByKey.TryGetValue(componentTypeKey, out var codec))
                return codec;
            throw new KeyNotFoundException($"Runtime component patch codec '{componentTypeKey}' is not registered.");
        }
    }

    public class RuntimeObjectPatchEngine
    {
        private readonly RuntimePatchCodecRegistry _registry;
        private readonly RuntimePatchCodecContext _context;

        public RuntimeObjectPatchEngine(RuntimePatchCodecRegistry registry, RuntimePatchCodecContext context)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public RuntimeObjectPatch BuildPatch(
            IReadOnlyDictionary<uint, GameRuntimeComponent> baseline,
            IReadOnlyDictionary<uint, GameRuntimeComponent> current)
        {
            var typeIds = CollectTypeIds(baseline, current);
            var result = new RuntimeObjectPatch(_registry.SchemaHash);
            for (var i = 0; i < typeIds.Count; i++)
            {
                var typeId = typeIds[i];
                TryGetComponent(baseline, typeId, out var baselineComponent);
                TryGetComponent(current, typeId, out var currentComponent);
                var componentPatch = _registry.Get(typeId).BuildPatch(baselineComponent, currentComponent, _context);
                if (componentPatch != null)
                    result.Components.Add(componentPatch);
            }
            return result;
        }

        public RuntimeObjectPatch BuildProjectedPatch(
            IReadOnlyCollection<uint> baselineComponentTypeIds,
            IReadOnlyDictionary<uint, GameRuntimeComponent> current,
            Func<uint, GameRuntimeComponent> materializeBaseline,
            Func<uint, RuntimeComponentPatchProjectionMode> selectMode)
        {
            if (baselineComponentTypeIds == null)
                throw new ArgumentNullException(nameof(baselineComponentTypeIds));
            if (materializeBaseline == null)
                throw new ArgumentNullException(nameof(materializeBaseline));
            if (selectMode == null)
                throw new ArgumentNullException(nameof(selectMode));

            var baselinePresence = new HashSet<uint>();
            foreach (var componentTypeId in baselineComponentTypeIds)
            {
                if (!baselinePresence.Add(componentTypeId))
                    throw new InvalidOperationException($"Projected baseline contains duplicate component type id {componentTypeId}.");
            }

            var currentPresence = new HashSet<uint>();
            if (current != null)
            {
                foreach (var pair in current)
                {
                    currentPresence.Add(pair.Key);
                }
            }

            var componentTypeIds = new HashSet<uint>(baselinePresence);
            componentTypeIds.UnionWith(currentPresence);
            var orderedComponentTypeIds = new List<uint>(componentTypeIds);
            orderedComponentTypeIds.Sort();

            var result = new RuntimeObjectPatch(_registry.SchemaHash);
            for (var i = 0; i < orderedComponentTypeIds.Count; i++)
            {
                var componentTypeId = orderedComponentTypeIds[i];
                var hasBaseline = baselinePresence.Contains(componentTypeId);
                var hasCurrent = currentPresence.Contains(componentTypeId);
                var codec = _registry.Get(componentTypeId);
                var mode = selectMode(componentTypeId);
                switch (mode)
                {
                    case RuntimeComponentPatchProjectionMode.SemanticDiff:
                        var baselineComponent = hasBaseline
                            ? materializeBaseline(componentTypeId)
                            : null;
                        if (hasBaseline && baselineComponent == null)
                        {
                            throw new InvalidOperationException(
                                $"Projected baseline materializer returned null for component {componentTypeId}.");
                        }
                        TryGetComponent(current, componentTypeId, out var currentComponent);
                        var componentPatch = codec.BuildPatch(baselineComponent, currentComponent, _context);
                        if (componentPatch != null)
                            result.Components.Add(componentPatch);
                        break;

                    case RuntimeComponentPatchProjectionMode.StructuralPresence:
                        AppendStructuralPresenceChange(
                            result,
                            componentTypeId,
                            codec.ComponentTypeKey,
                            hasBaseline,
                            hasCurrent);
                        break;

                    case RuntimeComponentPatchProjectionMode.Excluded:
                        if (hasBaseline)
                        {
                            result.Components.Add(new ComponentPatch(
                                componentTypeId,
                                codec.ComponentTypeKey,
                                ComponentPatchKind.Remove));
                        }
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(
                            nameof(selectMode),
                            mode,
                            $"Component {componentTypeId} has no supported patch projection mode.");
                }
            }

            return result;
        }

        public void AppendStructuralPresenceChanges(
            RuntimeObjectPatch target,
            IReadOnlyCollection<uint> previous,
            IReadOnlyCollection<uint> current)
        {
            if (target == null)
                throw new ArgumentNullException(nameof(target));
            if (!string.Equals(target.SchemaHash, _registry.SchemaHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runtime object patch schema hash '{target.SchemaHash}' does not match registry '{_registry.SchemaHash}'.");
            }
            if (target.Representation != RuntimeObjectPatchRepresentation.RuntimeBinary)
            {
                throw new InvalidOperationException(
                    $"Structural presence projection requires {RuntimeObjectPatchRepresentation.RuntimeBinary}, received {target.Representation}.");
            }
            if (target.Components == null)
                throw new InvalidOperationException("Runtime object patch has no component collection.");

            var previousSet = previous == null ? new HashSet<uint>() : new HashSet<uint>(previous);
            var currentSet = current == null ? new HashSet<uint>() : new HashSet<uint>(current);
            var componentTypeIds = new HashSet<uint>(previousSet);
            componentTypeIds.UnionWith(currentSet);
            var orderedComponentTypeIds = new List<uint>(componentTypeIds);
            orderedComponentTypeIds.Sort();
            for (var i = 0; i < orderedComponentTypeIds.Count; i++)
            {
                var componentTypeId = orderedComponentTypeIds[i];
                AppendStructuralPresenceChange(
                    target,
                    componentTypeId,
                    _registry.Get(componentTypeId).ComponentTypeKey,
                    previousSet.Contains(componentTypeId),
                    currentSet.Contains(componentTypeId));
            }

            NormalizeComponents(target.Components);
        }

        public Dictionary<uint, GameRuntimeComponent> ApplyPatch(
            IReadOnlyDictionary<uint, GameRuntimeComponent> baseline,
            RuntimeObjectPatch patch)
        {
            if (patch == null)
                throw new ArgumentNullException(nameof(patch));
            if (!string.Equals(patch.SchemaHash, _registry.SchemaHash, StringComparison.Ordinal))
                throw new InvalidOperationException($"Runtime object patch schema hash '{patch.SchemaHash}' does not match registry '{_registry.SchemaHash}'.");
            if (patch.Representation != RuntimeObjectPatchRepresentation.RuntimeBinary)
                throw new InvalidOperationException($"Runtime patch engine requires {RuntimeObjectPatchRepresentation.RuntimeBinary}, received {patch.Representation}.");

            var result = new Dictionary<uint, GameRuntimeComponent>();
            if (baseline != null)
            {
                foreach (var pair in baseline)
                {
                    result[pair.Key] = _registry.Get(pair.Key).Clone(pair.Value);
                }
            }

            var componentPatches = patch.Components == null
                ? new List<ComponentPatch>()
                : new List<ComponentPatch>(patch.Components);
            NormalizeComponents(componentPatches);
            for (var i = 0; i < componentPatches.Count; i++)
            {
                var componentPatch = componentPatches[i];
                result.TryGetValue(componentPatch.ComponentTypeId, out var baselineComponent);
                var value = _registry.Get(componentPatch.ComponentTypeId).ApplyPatch(baselineComponent, componentPatch, _context);
                if (value == null)
                    result.Remove(componentPatch.ComponentTypeId);
                else
                    result[componentPatch.ComponentTypeId] = value;
            }
            return result;
        }

        /// <summary>
        /// Applies a lane-projected runtime patch without cloning components
        /// that the patch does not touch. The caller therefore owns every
        /// baseline value passed here: fresh GA materialization owns decoded
        /// instances, while replica delta staging commits only patched ids.
        /// Structural-presence lanes never enter semantic codec operations.
        /// </summary>
        public Dictionary<uint, GameRuntimeComponent> ApplyProjectedPatch(
            IReadOnlyDictionary<uint, GameRuntimeComponent> baseline,
            RuntimeObjectPatch patch,
            Func<uint, RuntimeComponentPatchProjectionMode> selectMode)
        {
            if (patch == null)
                throw new ArgumentNullException(nameof(patch));
            if (selectMode == null)
                throw new ArgumentNullException(nameof(selectMode));
            if (!string.Equals(patch.SchemaHash, _registry.SchemaHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runtime object patch schema hash '{patch.SchemaHash}' does not match registry '{_registry.SchemaHash}'.");
            }
            if (patch.Representation != RuntimeObjectPatchRepresentation.RuntimeBinary)
            {
                throw new InvalidOperationException(
                    $"Projected patch apply requires {RuntimeObjectPatchRepresentation.RuntimeBinary}, received {patch.Representation}.");
            }

            var result = new Dictionary<uint, GameRuntimeComponent>();
            if (baseline != null)
            {
                foreach (var pair in baseline)
                {
                    ValidateComponentValue(_registry.Get(pair.Key), pair.Value, "projected baseline");
                    result.Add(pair.Key, pair.Value);
                }
            }

            var componentPatches = patch.Components == null
                ? new List<ComponentPatch>()
                : new List<ComponentPatch>(patch.Components);
            NormalizeComponents(componentPatches);
            for (var i = 0; i < componentPatches.Count; i++)
            {
                var componentPatch = componentPatches[i];
                var codec = _registry.Get(componentPatch.ComponentTypeId);
                ValidatePatchIdentity(codec, componentPatch);
                result.TryGetValue(componentPatch.ComponentTypeId, out var baselineComponent);

                switch (selectMode(componentPatch.ComponentTypeId))
                {
                    case RuntimeComponentPatchProjectionMode.SemanticDiff:
                        if (componentPatch.Kind == ComponentPatchKind.AddPresence)
                        {
                            throw new InvalidOperationException(
                                $"Semantic component {componentPatch.ComponentTypeId} cannot use payloadless AddPresence.");
                        }

                        // Custom codecs may mutate their input. Clone only this
                        // affected reliable component so detached validation
                        // cannot mutate the live replica.
                        var semanticBaseline = componentPatch.Kind == ComponentPatchKind.Custom
                            && baselineComponent != null
                                ? codec.Clone(baselineComponent)
                                : baselineComponent;
                        var semanticValue = codec.ApplyPatch(semanticBaseline, componentPatch, _context);
                        SetProjectedValue(result, codec, semanticValue);
                        break;

                    case RuntimeComponentPatchProjectionMode.StructuralPresence:
                        ApplyStructuralPresence(result, codec, baselineComponent, componentPatch);
                        break;

                    case RuntimeComponentPatchProjectionMode.Excluded:
                        RequirePayloadlessKind(componentPatch, ComponentPatchKind.Remove, "Excluded");
                        if (baselineComponent == null)
                        {
                            throw new InvalidOperationException(
                                $"Excluded component {componentPatch.ComponentTypeId} remove requires a present baseline component.");
                        }
                        result.Remove(componentPatch.ComponentTypeId);
                        break;

                    default:
                        throw new InvalidOperationException(
                            $"Component {componentPatch.ComponentTypeId} has no supported projected apply mode.");
                }
            }

            return result;
        }

        private static List<uint> CollectTypeIds(
            IReadOnlyDictionary<uint, GameRuntimeComponent> baseline,
            IReadOnlyDictionary<uint, GameRuntimeComponent> current)
        {
            var unique = new HashSet<uint>();
            if (baseline != null)
            {
                foreach (var pair in baseline)
                {
                    unique.Add(pair.Key);
                }
            }
            if (current != null)
            {
                foreach (var pair in current)
                {
                    unique.Add(pair.Key);
                }
            }

            var result = new List<uint>(unique);
            result.Sort();
            return result;
        }

        private static bool TryGetComponent(
            IReadOnlyDictionary<uint, GameRuntimeComponent> components,
            uint componentTypeId,
            out GameRuntimeComponent component)
        {
            if (components != null && components.TryGetValue(componentTypeId, out component))
                return true;
            component = null;
            return false;
        }

        private static void ApplyStructuralPresence(
            Dictionary<uint, GameRuntimeComponent> target,
            RuntimeComponentPatchCodec codec,
            GameRuntimeComponent baseline,
            ComponentPatch patch)
        {
            switch (patch.Kind)
            {
                case ComponentPatchKind.AddPresence:
                    RequirePayloadlessKind(patch, ComponentPatchKind.AddPresence, "Structural-presence");
                    if (baseline != null)
                    {
                        throw new InvalidOperationException(
                            $"Structural-presence component {patch.ComponentTypeId} add requires an absent baseline component.");
                    }
                    SetProjectedValue(target, codec, codec.CreateDefault());
                    return;

                case ComponentPatchKind.Remove:
                    RequirePayloadlessKind(patch, ComponentPatchKind.Remove, "Structural-presence");
                    if (baseline == null)
                    {
                        throw new InvalidOperationException(
                            $"Structural-presence component {patch.ComponentTypeId} remove requires a present baseline component.");
                    }
                    target.Remove(patch.ComponentTypeId);
                    return;

                default:
                    throw new InvalidOperationException(
                        $"Structural-presence component {patch.ComponentTypeId} cannot apply {patch.Kind}; only AddPresence and Remove are allowed.");
            }
        }

        private static void SetProjectedValue(
            Dictionary<uint, GameRuntimeComponent> target,
            RuntimeComponentPatchCodec codec,
            GameRuntimeComponent value)
        {
            if (value == null)
            {
                target.Remove(codec.ComponentTypeId);
                return;
            }

            ValidateComponentValue(codec, value, "projected result");
            target[codec.ComponentTypeId] = value;
        }

        private static void ValidateComponentValue(
            RuntimeComponentPatchCodec codec,
            GameRuntimeComponent value,
            string source)
        {
            if (value == null)
                throw new InvalidOperationException($"Component {codec.ComponentTypeId} has a null {source} value.");
            if (!codec.ComponentRuntimeType.IsInstanceOfType(value))
            {
                throw new InvalidOperationException(
                    $"Component {codec.ComponentTypeId} expected {codec.ComponentRuntimeType.FullName}, " +
                    $"but {source} contains {value.GetType().FullName}.");
            }
        }

        private static void ValidatePatchIdentity(
            RuntimeComponentPatchCodec codec,
            ComponentPatch patch)
        {
            if (patch.ComponentTypeId != codec.ComponentTypeId
                || !string.Equals(patch.ComponentTypeKey, codec.ComponentTypeKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Component patch {patch.ComponentTypeId}/'{patch.ComponentTypeKey}' does not match codec " +
                    $"{codec.ComponentTypeId}/'{codec.ComponentTypeKey}'.");
            }
        }

        private static void RequirePayloadlessKind(
            ComponentPatch patch,
            ComponentPatchKind requiredKind,
            string lane)
        {
            if (patch.Kind != requiredKind
                || patch.Payload != null
                || patch.CanonicalJson != null
                || (patch.Fields?.Count ?? 0) != 0)
            {
                throw new InvalidOperationException(
                    $"{lane} component {patch.ComponentTypeId} requires payloadless {requiredKind}.");
            }
        }

        private static void AppendStructuralPresenceChange(
            RuntimeObjectPatch target,
            uint componentTypeId,
            string componentTypeKey,
            bool hadPrevious,
            bool hasCurrent)
        {
            if (hadPrevious == hasCurrent)
                return;

            target.Components.Add(new ComponentPatch(
                componentTypeId,
                componentTypeKey,
                hasCurrent ? ComponentPatchKind.AddPresence : ComponentPatchKind.Remove));
        }

        private static void NormalizeComponents(List<ComponentPatch> components)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < components.Count; i++)
            {
                if (components[i] == null)
                    throw new InvalidOperationException("Runtime object patch cannot contain null component patches.");
                if (string.IsNullOrWhiteSpace(components[i].ComponentTypeKey))
                    throw new InvalidOperationException($"Runtime object component patch {components[i].ComponentTypeId} has no stable component key.");
                if (!keys.Add(components[i].ComponentTypeKey))
                    throw new InvalidOperationException($"Runtime object patch contains duplicate component type key '{components[i].ComponentTypeKey}'.");
            }
            components.Sort(CompareComponents);

            for (var i = 1; i < components.Count; i++)
            {
                if (components[i - 1].ComponentTypeId == components[i].ComponentTypeId)
                    throw new InvalidOperationException($"Runtime object patch contains duplicate component type id {components[i].ComponentTypeId}.");
            }
        }

        private static int CompareComponents(ComponentPatch first, ComponentPatch second)
        {
            var byId = first.ComponentTypeId.CompareTo(second.ComponentTypeId);
            return byId != 0 ? byId : string.CompareOrdinal(first.ComponentTypeKey, second.ComponentTypeKey);
        }
    }
}
