using System;
using System.Collections.Generic;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public static class RuntimeMotionProtocol
    {
        public const uint FORMAT_MAGIC = 0x324f4d52;
        public const uint FORMAT_VERSION = 2;
        public const int SEND_RATE_HZ = 20;
        public const double SEND_INTERVAL_SECONDS = 1d / SEND_RATE_HZ;
        public const double INTERPOLATION_DELAY_SECONDS = 0.1d;
        public const double STALE_TIMEOUT_SECONDS = 0.5d;
        public const double KEYFRAME_INTERVAL_SECONDS = 1d;
        public const int STOP_FRAME_REPETITIONS = 3;
        public const int MAX_SAMPLES_PER_FRAME = 512;
        public const int MAX_COMPONENTS_PER_SAMPLE = 64;
        // KCP unreliable messages must fit one conservative transport packet.
        // The 200-byte gap to the default 1200-byte MTU is reserved for the
        // Mirror message, batching and KCP envelope. Fragmented unreliable
        // state is indistinguishable from loss and must instead spill
        // deterministically across later 20 Hz frames.
        public const int MAX_UNRELIABLE_PAYLOAD_BYTES = 1000;
        public const int MAX_COMPONENT_STATE_BYTES = MAX_UNRELIABLE_PAYLOAD_BYTES;
        public const int MAX_PAYLOAD_BYTES = MAX_UNRELIABLE_PAYLOAD_BYTES;
    }

    [Flags]
    public enum RuntimeMotionSampleFlags : byte
    {
        None = 0,
        Stop = 1 << 0,
    }

    public readonly struct RuntimeMotionComponentState
    {
        public readonly uint ComponentTypeId;
        public readonly byte[] CanonicalState;

        public RuntimeMotionComponentState(uint componentTypeId, byte[] canonicalState)
        {
            if (canonicalState == null)
                throw new ArgumentNullException(nameof(canonicalState));
            if (canonicalState.Length > RuntimeMotionProtocol.MAX_COMPONENT_STATE_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(canonicalState),
                    $"Motion component state is {canonicalState.Length} bytes; maximum is {RuntimeMotionProtocol.MAX_COMPONENT_STATE_BYTES}.");
            }

            ComponentTypeId = componentTypeId;
            CanonicalState = (byte[])canonicalState.Clone();
        }
    }

    public readonly struct RuntimeMotionSample
    {
        private const RuntimeMotionSampleFlags ALL_FLAGS = RuntimeMotionSampleFlags.Stop;

        public readonly NetObjectRef Object;
        public readonly RuntimeMotionSampleFlags Flags;
        public readonly IReadOnlyList<RuntimeMotionComponentState> Components;

        public bool IsStop => (Flags & RuntimeMotionSampleFlags.Stop) != 0;

        public RuntimeMotionSample(
            NetObjectRef value,
            RuntimeMotionSampleFlags flags,
            IReadOnlyList<RuntimeMotionComponentState> components)
        {
            if (!value.IsValid)
                throw new ArgumentException("Motion sample requires a valid object reference.", nameof(value));
            if (((byte)flags & ~(byte)ALL_FLAGS) != 0)
                throw new ArgumentOutOfRangeException(nameof(flags), flags, "Motion sample contains unsupported flags.");
            if (components == null)
                throw new ArgumentNullException(nameof(components));
            if (components.Count == 0 || components.Count > RuntimeMotionProtocol.MAX_COMPONENTS_PER_SAMPLE)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(components),
                    $"Motion sample component count {components.Count} is outside 1..{RuntimeMotionProtocol.MAX_COMPONENTS_PER_SAMPLE}.");
            }

            var copy = new RuntimeMotionComponentState[components.Count];
            for (var i = 0; i < components.Count; i++)
            {
                var component = components[i];
                copy[i] = new RuntimeMotionComponentState(component.ComponentTypeId, component.CanonicalState);
            }
            Array.Sort(copy, CompareComponents);
            for (var i = 1; i < copy.Length; i++)
            {
                if (copy[i - 1].ComponentTypeId == copy[i].ComponentTypeId)
                {
                    throw new ArgumentException(
                        $"Motion sample contains duplicate component type id {copy[i].ComponentTypeId}.",
                        nameof(components));
                }
            }

            Object = value;
            Flags = flags;
            Components = Array.AsReadOnly(copy);
        }

        private static int CompareComponents(RuntimeMotionComponentState left, RuntimeMotionComponentState right)
        {
            return left.ComponentTypeId.CompareTo(right.ComponentTypeId);
        }
    }

    public class RuntimeMotionFrame
    {
        public readonly NetStoreRef Store;
        public readonly uint SimulationTick;
        public readonly bool IsKeyframe;
        public readonly IReadOnlyList<RuntimeMotionSample> Samples;

        public RuntimeMotionFrame(
            NetStoreRef store,
            uint simulationTick,
            bool isKeyframe,
            IReadOnlyList<RuntimeMotionSample> samples)
        {
            if (!store.IsValid)
                throw new ArgumentException("Motion frame requires a valid store reference.", nameof(store));
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            if (samples.Count == 0 || samples.Count > RuntimeMotionProtocol.MAX_SAMPLES_PER_FRAME)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samples),
                    $"Motion frame sample count {samples.Count} is outside 1..{RuntimeMotionProtocol.MAX_SAMPLES_PER_FRAME}.");
            }

            var copy = new RuntimeMotionSample[samples.Count];
            for (var i = 0; i < samples.Count; i++)
            {
                if (samples[i].Object.Store != store)
                    throw new ArgumentException($"Motion sample '{samples[i].Object}' does not belong to frame store '{store}'.", nameof(samples));
                copy[i] = samples[i];
            }

            Store = store;
            SimulationTick = simulationTick;
            IsKeyframe = isKeyframe;
            Samples = Array.AsReadOnly(copy);
        }
    }

    public static class RuntimeSimulationTickSequence
    {
        public static bool IsNewer(uint candidate, uint baseline)
        {
            return unchecked((int)(candidate - baseline)) > 0;
        }
    }

    public enum RuntimeMotionFrameAcceptResult : byte
    {
        Accepted = 1,
        StaleSimulationTick = 2,
        WrongStore = 3,
    }

    public class RuntimeMotionFrameSequenceGate
    {
        private bool _hasAcceptedTick;

        public readonly NetStoreRef Store;

        public uint LastAcceptedSimulationTick { get; private set; }

        public RuntimeMotionFrameSequenceGate(NetStoreRef store)
        {
            if (!store.IsValid)
                throw new ArgumentException("Motion sequence gate requires a valid store reference.", nameof(store));
            Store = store;
        }

        public RuntimeMotionFrameAcceptResult Accept(RuntimeMotionFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (frame.Store != Store)
                return RuntimeMotionFrameAcceptResult.WrongStore;
            if (_hasAcceptedTick && !RuntimeSimulationTickSequence.IsNewer(frame.SimulationTick, LastAcceptedSimulationTick))
                return RuntimeMotionFrameAcceptResult.StaleSimulationTick;

            _hasAcceptedTick = true;
            LastAcceptedSimulationTick = frame.SimulationTick;
            return RuntimeMotionFrameAcceptResult.Accepted;
        }
    }

    public class RuntimeMotionFrameCodec
    {
        private const int FRAME_FIXED_BYTES = sizeof(uint) // magic
                                              + sizeof(uint) // version
                                              + sizeof(int) // store string byte length
                                              + sizeof(uint) // store generation
                                              + sizeof(uint) // simulation tick
                                              + sizeof(byte) // keyframe
                                              + sizeof(int); // sample count
        private const int SAMPLE_FIXED_BYTES = sizeof(long) // object id
                                               + sizeof(byte) // flags
                                               + sizeof(int); // component count
        private const int COMPONENT_FIXED_BYTES = sizeof(uint) // component type id
                                                  + sizeof(int); // payload byte length

        public static int CalculateHeaderSize(NetStoreRef store)
        {
            if (!store.IsValid)
                throw new ArgumentException("Motion payload size requires a valid store reference.", nameof(store));
            return FRAME_FIXED_BYTES + Encoding.UTF8.GetByteCount(store.StoreId.ToString());
        }

        public static int CalculateSampleSize(in RuntimeMotionSample sample)
        {
            ValidateSample(sample);
            var result = SAMPLE_FIXED_BYTES;
            for (var i = 0; i < sample.Components.Count; i++)
            {
                var payload = sample.Components[i].CanonicalState
                              ?? throw new InvalidOperationException(
                                  $"Motion component {sample.Components[i].ComponentTypeId} has a null canonical state.");
                result = checked(result + COMPONENT_FIXED_BYTES + payload.Length);
            }
            return result;
        }

        public byte[] Encode(RuntimeMotionFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            var samples = new List<RuntimeMotionSample>(frame.Samples.Count);
            for (var i = 0; i < frame.Samples.Count; i++)
            {
                samples.Add(frame.Samples[i]);
            }
            samples.Sort(CompareSamples);
            for (var i = 1; i < samples.Count; i++)
            {
                if (samples[i - 1].Object == samples[i].Object)
                    throw new InvalidOperationException($"Motion frame contains duplicate object '{samples[i].Object}'.");
            }

            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteUInt32(RuntimeMotionProtocol.FORMAT_MAGIC);
            writer.WriteUInt32(RuntimeMotionProtocol.FORMAT_VERSION);
            writer.WriteString(frame.Store.StoreId.ToString());
            writer.WriteUInt32(frame.Store.StoreGeneration);
            writer.WriteUInt32(frame.SimulationTick);
            writer.WriteBoolean(frame.IsKeyframe);
            writer.WriteInt32(samples.Count);
            for (var i = 0; i < samples.Count; i++)
            {
                WriteSample(writer, samples[i]);
            }

            var payload = writer.ToArray();
            if (payload.Length > RuntimeMotionProtocol.MAX_PAYLOAD_BYTES)
            {
                throw new InvalidOperationException(
                    $"Motion frame payload is {payload.Length} bytes; maximum is {RuntimeMotionProtocol.MAX_PAYLOAD_BYTES}.");
            }

            return payload;
        }

        public RuntimeMotionFrame Decode(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0 || payload.Length > RuntimeMotionProtocol.MAX_PAYLOAD_BYTES)
            {
                throw new FormatException(
                    $"Motion frame payload size {payload.Length} is outside 1..{RuntimeMotionProtocol.MAX_PAYLOAD_BYTES} bytes.");
            }

            var reader = new CanonicalPatchBinaryReader(payload);
            var magic = reader.ReadUInt32();
            if (magic != RuntimeMotionProtocol.FORMAT_MAGIC)
                throw new FormatException($"Motion frame magic 0x{magic:x8} does not match 0x{RuntimeMotionProtocol.FORMAT_MAGIC:x8}.");
            var version = reader.ReadUInt32();
            if (version != RuntimeMotionProtocol.FORMAT_VERSION)
                throw new FormatException($"Motion frame version {version} is not supported.");

            var storeId = reader.ReadString();
            var storeGeneration = reader.ReadUInt32();
            if (string.IsNullOrWhiteSpace(storeId))
                throw new FormatException("Motion frame store id is empty.");
            NetStoreRef store;
            try
            {
                store = new NetStoreRef(storeId, storeGeneration);
            }
            catch (ArgumentException exception)
            {
                throw new FormatException("Motion frame store id is invalid.", exception);
            }
            if (!store.IsValid)
                throw new FormatException($"Motion frame has invalid store reference '{store}'.");

            var simulationTick = reader.ReadUInt32();
            var isKeyframe = reader.ReadBoolean();
            var sampleCount = reader.ReadInt32();
            if (sampleCount <= 0 || sampleCount > RuntimeMotionProtocol.MAX_SAMPLES_PER_FRAME)
            {
                throw new FormatException(
                    $"Motion frame sample count {sampleCount} is outside 1..{RuntimeMotionProtocol.MAX_SAMPLES_PER_FRAME}.");
            }

            var samples = new RuntimeMotionSample[sampleCount];
            var previousObjectId = 0L;
            for (var i = 0; i < sampleCount; i++)
            {
                samples[i] = ReadSample(reader, store);
                if (i > 0 && samples[i].Object.ObjectId <= previousObjectId)
                    throw new FormatException("Motion frame samples are not in canonical object id order.");
                previousObjectId = samples[i].Object.ObjectId;
            }

            reader.RequireEnd();
            return new RuntimeMotionFrame(store, simulationTick, isKeyframe, samples);
        }

        private static void WriteSample(CanonicalPatchBinaryWriter writer, RuntimeMotionSample sample)
        {
            ValidateSample(sample);
            writer.WriteInt64(sample.Object.ObjectId);
            writer.WriteByte((byte)sample.Flags);
            writer.WriteInt32(sample.Components.Count);
            for (var i = 0; i < sample.Components.Count; i++)
            {
                var component = sample.Components[i];
                writer.WriteUInt32(component.ComponentTypeId);
                writer.WriteBytes(component.CanonicalState);
            }
        }

        private static RuntimeMotionSample ReadSample(CanonicalPatchBinaryReader reader, NetStoreRef store)
        {
            NetObjectRef value;
            try
            {
                value = new NetObjectRef(store, reader.ReadInt64());
            }
            catch (ArgumentException exception)
            {
                throw new FormatException("Motion sample has an invalid object id.", exception);
            }

            var flags = (RuntimeMotionSampleFlags)reader.ReadByte();
            if (((byte)flags & ~(byte)RuntimeMotionSampleFlags.Stop) != 0)
                throw new FormatException($"Motion sample for object '{value}' contains unsupported flags {flags}.");

            var componentCount = reader.ReadInt32();
            if (componentCount <= 0 || componentCount > RuntimeMotionProtocol.MAX_COMPONENTS_PER_SAMPLE)
            {
                throw new FormatException(
                    $"Motion sample for object '{value}' component count {componentCount} is outside 1..{RuntimeMotionProtocol.MAX_COMPONENTS_PER_SAMPLE}.");
            }

            var components = new RuntimeMotionComponentState[componentCount];
            var previousComponentTypeId = 0u;
            for (var i = 0; i < componentCount; i++)
            {
                var componentTypeId = reader.ReadUInt32();
                if (i > 0 && componentTypeId <= previousComponentTypeId)
                {
                    throw new FormatException(
                        $"Motion sample for object '{value}' components are not in canonical component type id order.");
                }

                var canonicalState = reader.ReadBytes();
                if (canonicalState == null)
                    throw new FormatException($"Motion component {componentTypeId} for object '{value}' has a null canonical state.");
                if (canonicalState.Length > RuntimeMotionProtocol.MAX_COMPONENT_STATE_BYTES)
                {
                    throw new FormatException(
                        $"Motion component {componentTypeId} for object '{value}' is {canonicalState.Length} bytes; "
                        + $"maximum is {RuntimeMotionProtocol.MAX_COMPONENT_STATE_BYTES}.");
                }

                components[i] = new RuntimeMotionComponentState(componentTypeId, canonicalState);
                previousComponentTypeId = componentTypeId;
            }

            try
            {
                return new RuntimeMotionSample(value, flags, components);
            }
            catch (ArgumentException exception)
            {
                throw new FormatException($"Motion sample for object '{value}' is invalid.", exception);
            }
        }

        private static void ValidateSample(in RuntimeMotionSample sample)
        {
            if (!sample.Object.IsValid)
                throw new InvalidOperationException("Motion sample requires a valid object reference.");
            if (((byte)sample.Flags & ~(byte)RuntimeMotionSampleFlags.Stop) != 0)
                throw new InvalidOperationException($"Motion sample for object '{sample.Object}' contains unsupported flags {sample.Flags}.");
            if (sample.Components == null
                || sample.Components.Count == 0
                || sample.Components.Count > RuntimeMotionProtocol.MAX_COMPONENTS_PER_SAMPLE)
            {
                throw new InvalidOperationException(
                    $"Motion sample for object '{sample.Object}' component count is outside 1..{RuntimeMotionProtocol.MAX_COMPONENTS_PER_SAMPLE}.");
            }

            var previousComponentTypeId = 0u;
            for (var i = 0; i < sample.Components.Count; i++)
            {
                var component = sample.Components[i];
                if (component.CanonicalState == null)
                    throw new InvalidOperationException($"Motion component {component.ComponentTypeId} has a null canonical state.");
                if (component.CanonicalState.Length > RuntimeMotionProtocol.MAX_COMPONENT_STATE_BYTES)
                {
                    throw new InvalidOperationException(
                        $"Motion component {component.ComponentTypeId} is {component.CanonicalState.Length} bytes; "
                        + $"maximum is {RuntimeMotionProtocol.MAX_COMPONENT_STATE_BYTES}.");
                }
                if (i > 0 && component.ComponentTypeId <= previousComponentTypeId)
                    throw new InvalidOperationException($"Motion sample for object '{sample.Object}' components are not canonical.");
                previousComponentTypeId = component.ComponentTypeId;
            }
        }

        private static int CompareSamples(RuntimeMotionSample left, RuntimeMotionSample right)
        {
            return left.Object.ObjectId.CompareTo(right.Object.ObjectId);
        }
    }
}
