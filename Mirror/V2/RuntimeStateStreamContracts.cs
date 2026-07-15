using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public static class RuntimeStateStreamProtocol
    {
        public const uint FORMAT_MAGIC = 0x32535452;
        public const uint FORMAT_VERSION = 2;
        public const int SEND_RATE_HZ = 20;
        public const double SEND_INTERVAL_SECONDS = 1d / SEND_RATE_HZ;
        public const double INTERPOLATION_DELAY_SECONDS = 0.1d;
        public const double STALE_TIMEOUT_SECONDS = 0.5d;
        public const double HEARTBEAT_INTERVAL_SECONDS = 0.1d;
        public const double KEYFRAME_INTERVAL_SECONDS = 1d;
        public const int STOP_FRAME_REPETITIONS = 3;
        public const int DESPAWN_FRAME_REPETITIONS = 3;
        public const int MAX_SAMPLES_PER_FRAME = 512;
        public const int MAX_COMPONENTS_PER_SAMPLE = 64;
        public const int MAX_PACKED_SAMPLE_BYTES = 1000;
        public const int MAX_UNRELIABLE_PAYLOAD_BYTES = 1000;
        public const int MAX_PAYLOAD_BYTES = MAX_UNRELIABLE_PAYLOAD_BYTES;
        public const int MAX_RECONCILIATION_SAMPLES = 65_536;
        public const int MAX_RECONCILIATION_PACKED_BYTES = 16 * 1024 * 1024;
        public const int MAX_RECONCILIATION_SEGMENTS = 10;
    }

    [Flags]
    public enum RuntimeStateStreamSampleFlags : byte
    {
        None = 0,
        Stop = 1 << 0,
        Despawn = 1 << 1,
    }

    [Flags]
    public enum RuntimeStateStreamFrameFlags : byte
    {
        None = 0,
        ReconciliationBegin = 1 << 0,
        ReconciliationEnd = 1 << 1,
    }

    public readonly struct RuntimeStateStreamKey : IEquatable<RuntimeStateStreamKey>
    {
        public readonly long Value;

        public bool IsValid => Value > 0;

        public RuntimeStateStreamKey(long value)
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "State stream key must be positive.");
            Value = value;
        }

        public bool Equals(RuntimeStateStreamKey other) => Value == other.Value;
        public override bool Equals(object obj) => obj is RuntimeStateStreamKey other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => Value.ToString();
        public static bool operator ==(RuntimeStateStreamKey left, RuntimeStateStreamKey right) => left.Equals(right);
        public static bool operator !=(RuntimeStateStreamKey left, RuntimeStateStreamKey right) => !left.Equals(right);
    }

    public readonly struct RuntimePackedStateStreamSample
    {
        private const RuntimeStateStreamSampleFlags ALL_FLAGS =
            RuntimeStateStreamSampleFlags.Stop | RuntimeStateStreamSampleFlags.Despawn;

        public readonly RuntimeStateStreamKey Key;
        public readonly RuntimeStateStreamSampleFlags Flags;
        public readonly byte[] PackedState;

        public bool IsStop => (Flags & RuntimeStateStreamSampleFlags.Stop) != 0;
        public bool IsDespawn => (Flags & RuntimeStateStreamSampleFlags.Despawn) != 0;

        public RuntimePackedStateStreamSample(
            RuntimeStateStreamKey key,
            RuntimeStateStreamSampleFlags flags,
            byte[] packedState)
        {
            if (!key.IsValid)
                throw new ArgumentException("Packed state sample requires a valid key.", nameof(key));
            if (((byte)flags & ~(byte)ALL_FLAGS) != 0)
                throw new ArgumentOutOfRangeException(nameof(flags), flags, "Packed state sample contains unsupported flags.");
            if (packedState == null)
                throw new ArgumentNullException(nameof(packedState));
            var isDespawn = (flags & RuntimeStateStreamSampleFlags.Despawn) != 0;
            if ((isDespawn && packedState.Length != 0)
                || (!isDespawn && packedState.Length == 0)
                || packedState.Length > RuntimeStateStreamProtocol.MAX_PACKED_SAMPLE_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(packedState),
                    $"Packed state sample size {packedState.Length} is invalid for flags {flags}.");
            }
            if (isDespawn && (flags & RuntimeStateStreamSampleFlags.Stop) != 0)
                throw new ArgumentException("Despawn state stream sample cannot also be Stop.", nameof(flags));

            Key = key;
            Flags = flags;
            PackedState = (byte[])packedState.Clone();
        }
    }

    public class RuntimeStateStreamFrame
    {
        public readonly NetStoreRef Store;
        public readonly uint StreamTypeId;
        public readonly uint Sequence;
        public readonly uint SimulationTick;
        public readonly uint ReconciliationId;
        public readonly RuntimeStateStreamFrameFlags Flags;
        public readonly IReadOnlyList<RuntimePackedStateStreamSample> Samples;

        public bool IsReconciliation => ReconciliationId != 0;
        public bool StartsReconciliation => (Flags & RuntimeStateStreamFrameFlags.ReconciliationBegin) != 0;
        public bool CompletesReconciliation => (Flags & RuntimeStateStreamFrameFlags.ReconciliationEnd) != 0;
        public bool IsKeyframe => StartsReconciliation && CompletesReconciliation;

        public RuntimeStateStreamFrame(
            NetStoreRef store,
            uint streamTypeId,
            uint sequence,
            uint simulationTick,
            bool isKeyframe,
            IReadOnlyList<RuntimePackedStateStreamSample> samples)
            : this(
                store,
                streamTypeId,
                sequence,
                simulationTick,
                isKeyframe ? sequence : 0,
                isKeyframe
                    ? RuntimeStateStreamFrameFlags.ReconciliationBegin | RuntimeStateStreamFrameFlags.ReconciliationEnd
                    : RuntimeStateStreamFrameFlags.None,
                samples) { }

        public RuntimeStateStreamFrame(
            NetStoreRef store,
            uint streamTypeId,
            uint sequence,
            uint simulationTick,
            uint reconciliationId,
            RuntimeStateStreamFrameFlags flags,
            IReadOnlyList<RuntimePackedStateStreamSample> samples)
        {
            if (!store.IsValid)
                throw new ArgumentException("State stream frame requires a valid store reference.", nameof(store));
            if (streamTypeId == 0)
                throw new ArgumentOutOfRangeException(nameof(streamTypeId), "State stream type id must be non-zero.");
            if (sequence == 0)
                throw new ArgumentOutOfRangeException(nameof(sequence), "State stream sequence must be non-zero.");
            const RuntimeStateStreamFrameFlags allFlags =
                RuntimeStateStreamFrameFlags.ReconciliationBegin | RuntimeStateStreamFrameFlags.ReconciliationEnd;
            if (((byte)flags & ~(byte)allFlags) != 0)
                throw new ArgumentOutOfRangeException(nameof(flags), flags, "State stream frame contains unsupported flags.");
            if (reconciliationId == 0 && flags != RuntimeStateStreamFrameFlags.None)
            {
                throw new ArgumentException(
                    "An ordinary state stream frame cannot contain reconciliation flags.");
            }
            if ((flags & RuntimeStateStreamFrameFlags.ReconciliationBegin) != 0
                && reconciliationId != sequence)
            {
                throw new ArgumentException(
                    "A reconciliation Begin must use its first delivery sequence as ReconciliationId.",
                    nameof(reconciliationId));
            }
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            var isCompleteEmptyReconciliation = reconciliationId != 0
                                                && flags == (RuntimeStateStreamFrameFlags.ReconciliationBegin
                                                             | RuntimeStateStreamFrameFlags.ReconciliationEnd);
            if ((samples.Count == 0 && !isCompleteEmptyReconciliation)
                || samples.Count > RuntimeStateStreamProtocol.MAX_SAMPLES_PER_FRAME)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samples),
                    $"State stream frame sample count {samples.Count} is invalid; only an empty Begin|End reconciliation may contain zero samples.");
            }

            var copy = new RuntimePackedStateStreamSample[samples.Count];
            for (var i = 0; i < samples.Count; i++)
            {
                copy[i] = new RuntimePackedStateStreamSample(samples[i].Key, samples[i].Flags, samples[i].PackedState);
            }
            Array.Sort(copy, (first, second) => first.Key.Value.CompareTo(second.Key.Value));
            var exactPayloadBytes = RuntimeStateStreamFrameCodec.CalculateHeaderSize(store);
            for (var i = 0; i < copy.Length; i++)
            {
                if (i > 0 && copy[i - 1].Key == copy[i].Key)
                    throw new InvalidOperationException($"State stream frame contains duplicate key '{copy[i].Key}'.");
                exactPayloadBytes = checked(
                    exactPayloadBytes + RuntimeStateStreamFrameCodec.CalculateSampleSize(copy[i]));
            }
            if (exactPayloadBytes > RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samples),
                    $"State stream frame payload is {exactPayloadBytes} bytes; maximum is " +
                    $"{RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES}.");
            }

            Store = store;
            StreamTypeId = streamTypeId;
            Sequence = sequence;
            SimulationTick = simulationTick;
            ReconciliationId = reconciliationId;
            Flags = flags;
            Samples = Array.AsReadOnly(copy);
        }
    }

    public static class RuntimeStateStreamSequence
    {
        public static bool IsNewer(uint candidate, uint baseline)
        {
            return unchecked((int)(candidate - baseline)) > 0;
        }

        public static bool IsOlder(uint candidate, uint baseline)
        {
            return unchecked((int)(candidate - baseline)) < 0;
        }

        public static uint Next(uint value)
        {
            value = unchecked(value + 1u);
            return value == 0 ? 1u : value;
        }
    }

    /// <summary>
    /// Owns the delivery sequence for one connection/store/profile stream.
    /// Rebaseline replaces projection state, but keeps this cursor while the
    /// same connection and NetStoreRef remain alive.
    /// </summary>
    public sealed class RuntimeStateStreamSequenceCursor
    {
        public readonly NetStoreRef Store;
        public readonly uint StreamTypeId;

        public uint LastSequence { get; private set; }

        public RuntimeStateStreamSequenceCursor(NetStoreRef store, uint streamTypeId)
        {
            if (!store.IsValid)
                throw new ArgumentException("State stream sequence cursor requires a valid store reference.", nameof(store));
            if (streamTypeId == 0)
                throw new ArgumentOutOfRangeException(nameof(streamTypeId));
            Store = store;
            StreamTypeId = streamTypeId;
        }

        public uint TakeNext()
        {
            LastSequence = RuntimeStateStreamSequence.Next(LastSequence);
            return LastSequence;
        }
    }

    public enum RuntimeStateStreamFrameAcceptResult : byte
    {
        Accepted = 1,
        StaleSequence = 2,
        StaleSimulationTick = 3,
        WrongStore = 4,
        WrongStream = 5,
        InvalidSample = 6,
    }

    public class RuntimeStateStreamFrameSequenceGate
    {
        private bool _hasAccepted;

        public readonly NetStoreRef Store;
        public readonly uint StreamTypeId;

        public uint LastAcceptedSequence { get; private set; }
        public uint LastAcceptedSimulationTick { get; private set; }

        public RuntimeStateStreamFrameSequenceGate(NetStoreRef store, uint streamTypeId)
        {
            if (!store.IsValid)
                throw new ArgumentException("State stream sequence gate requires a valid store reference.", nameof(store));
            if (streamTypeId == 0)
                throw new ArgumentOutOfRangeException(nameof(streamTypeId));
            Store = store;
            StreamTypeId = streamTypeId;
        }

        public RuntimeStateStreamFrameAcceptResult CanAccept(RuntimeStateStreamFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));
            if (frame.Store != Store)
                return RuntimeStateStreamFrameAcceptResult.WrongStore;
            if (frame.StreamTypeId != StreamTypeId)
                return RuntimeStateStreamFrameAcceptResult.WrongStream;
            if (_hasAccepted && !RuntimeStateStreamSequence.IsNewer(frame.Sequence, LastAcceptedSequence))
                return RuntimeStateStreamFrameAcceptResult.StaleSequence;
            if (_hasAccepted && RuntimeStateStreamSequence.IsOlder(frame.SimulationTick, LastAcceptedSimulationTick))
                return RuntimeStateStreamFrameAcceptResult.StaleSimulationTick;
            return RuntimeStateStreamFrameAcceptResult.Accepted;
        }

        public void Commit(RuntimeStateStreamFrame frame)
        {
            var result = CanAccept(frame);
            if (result != RuntimeStateStreamFrameAcceptResult.Accepted)
                throw new InvalidOperationException($"Cannot commit state stream frame: {result}.");
            _hasAccepted = true;
            LastAcceptedSequence = frame.Sequence;
            LastAcceptedSimulationTick = frame.SimulationTick;
        }
    }

    public class RuntimeStateStreamFrameCodec
    {
        private const int FRAME_FIXED_BYTES = sizeof(uint) + sizeof(uint) + sizeof(int) + sizeof(uint)
                                              + sizeof(uint) + sizeof(uint) + sizeof(uint) + sizeof(uint)
                                              + sizeof(byte) + sizeof(int);
        private const int SAMPLE_FIXED_BYTES = sizeof(long) + sizeof(byte) + sizeof(int);

        public static int CalculateHeaderSize(NetStoreRef store)
        {
            if (!store.IsValid)
                throw new ArgumentException("State stream payload size requires a valid store reference.", nameof(store));
            return FRAME_FIXED_BYTES + Encoding.UTF8.GetByteCount(store.StoreId.ToString());
        }

        public static int CalculateSampleSize(in RuntimePackedStateStreamSample sample)
        {
            ValidateSample(sample);
            return checked(SAMPLE_FIXED_BYTES + sample.PackedState.Length);
        }

        public byte[] Encode(RuntimeStateStreamFrame frame)
        {
            if (frame == null)
                throw new ArgumentNullException(nameof(frame));

            var samples = new List<RuntimePackedStateStreamSample>(frame.Samples.Count);
            for (var i = 0; i < frame.Samples.Count; i++)
            {
                samples.Add(frame.Samples[i]);
            }
            samples.Sort((first, second) => first.Key.Value.CompareTo(second.Key.Value));
            for (var i = 1; i < samples.Count; i++)
            {
                if (samples[i - 1].Key == samples[i].Key)
                    throw new InvalidOperationException($"State stream frame contains duplicate key '{samples[i].Key}'.");
            }

            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(RuntimeStateStreamProtocol.FORMAT_MAGIC);
            writer.Write(RuntimeStateStreamProtocol.FORMAT_VERSION);
            WriteString(writer, frame.Store.StoreId.ToString());
            writer.Write(frame.Store.StoreGeneration);
            writer.Write(frame.StreamTypeId);
            writer.Write(frame.Sequence);
            writer.Write(frame.SimulationTick);
            writer.Write(frame.ReconciliationId);
            writer.Write((byte)frame.Flags);
            writer.Write(samples.Count);
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                ValidateSample(sample);
                writer.Write(sample.Key.Value);
                writer.Write((byte)sample.Flags);
                writer.Write(sample.PackedState.Length);
                writer.Write(sample.PackedState);
            }
            writer.Flush();
            var payload = stream.ToArray();
            if (payload.Length > RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES)
            {
                throw new InvalidOperationException(
                    $"State stream frame payload is {payload.Length} bytes; maximum is {RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES}.");
            }
            return payload;
        }

        public RuntimeStateStreamFrame Decode(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length == 0 || payload.Length > RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES)
            {
                throw new FormatException(
                    $"State stream frame payload size {payload.Length} is outside 1..{RuntimeStateStreamProtocol.MAX_PAYLOAD_BYTES} bytes.");
            }

            try
            {
                using var stream = new MemoryStream(payload, false);
                using var reader = new BinaryReader(stream, Encoding.UTF8, true);
                var magic = reader.ReadUInt32();
                if (magic != RuntimeStateStreamProtocol.FORMAT_MAGIC)
                    throw new FormatException($"State stream magic 0x{magic:x8} is invalid.");
                var version = reader.ReadUInt32();
                if (version != RuntimeStateStreamProtocol.FORMAT_VERSION)
                    throw new FormatException($"State stream version {version} is not supported.");
                var storeId = ReadString(reader);
                var store = new NetStoreRef(storeId, reader.ReadUInt32());
                if (!store.IsValid)
                    throw new FormatException("State stream store reference is invalid.");
                var streamTypeId = reader.ReadUInt32();
                var sequence = reader.ReadUInt32();
                var simulationTick = reader.ReadUInt32();
                var reconciliationId = reader.ReadUInt32();
                var frameFlags = (RuntimeStateStreamFrameFlags)reader.ReadByte();
                var count = reader.ReadInt32();
                if (streamTypeId == 0 || sequence == 0)
                    throw new FormatException("State stream type id and sequence must be non-zero.");
                if (count < 0
                    || count > RuntimeStateStreamProtocol.MAX_SAMPLES_PER_FRAME
                    || (count == 0
                        && (reconciliationId == 0
                            || frameFlags != (RuntimeStateStreamFrameFlags.ReconciliationBegin
                                              | RuntimeStateStreamFrameFlags.ReconciliationEnd))))
                    throw new FormatException($"State stream sample count {count} is invalid.");

                var samples = new RuntimePackedStateStreamSample[count];
                long previousKey = 0;
                for (var i = 0; i < count; i++)
                {
                    var key = new RuntimeStateStreamKey(reader.ReadInt64());
                    if (i > 0 && key.Value <= previousKey)
                        throw new FormatException("State stream samples are not in canonical key order.");
                    var flags = (RuntimeStateStreamSampleFlags)reader.ReadByte();
                    var packedLength = reader.ReadInt32();
                    var isDespawn = (flags & RuntimeStateStreamSampleFlags.Despawn) != 0;
                    if (packedLength < 0
                        || packedLength > RuntimeStateStreamProtocol.MAX_PACKED_SAMPLE_BYTES
                        || (isDespawn ? packedLength != 0 : packedLength == 0))
                        throw new FormatException($"State stream sample '{key}' packed size {packedLength} is invalid.");
                    var packed = reader.ReadBytes(packedLength);
                    if (packed.Length != packedLength)
                        throw new EndOfStreamException("State stream sample payload is truncated.");
                    samples[i] = new RuntimePackedStateStreamSample(key, flags, packed);
                    previousKey = key.Value;
                }
                if (stream.Position != stream.Length)
                    throw new FormatException("State stream frame has trailing bytes.");
                return new RuntimeStateStreamFrame(
                    store,
                    streamTypeId,
                    sequence,
                    simulationTick,
                    reconciliationId,
                    frameFlags,
                    samples);
            }
            catch (Exception exception) when (exception is EndOfStreamException
                                              || exception is IOException
                                              || exception is ArgumentException
                                              || exception is OverflowException)
            {
                throw new FormatException("State stream frame is malformed.", exception);
            }
        }

        private static void ValidateSample(in RuntimePackedStateStreamSample sample)
        {
            if (!sample.Key.IsValid)
                throw new InvalidOperationException("State stream sample key is invalid.");
            const RuntimeStateStreamSampleFlags allFlags =
                RuntimeStateStreamSampleFlags.Stop | RuntimeStateStreamSampleFlags.Despawn;
            if (((byte)sample.Flags & ~(byte)allFlags) != 0)
                throw new InvalidOperationException($"State stream sample '{sample.Key}' contains unsupported flags.");
            if (sample.IsDespawn && sample.IsStop)
                throw new InvalidOperationException($"State stream sample '{sample.Key}' cannot be both Stop and Despawn.");
            if (sample.PackedState == null
                || (sample.IsDespawn ? sample.PackedState.Length != 0 : sample.PackedState.Length == 0)
                || sample.PackedState.Length > RuntimeStateStreamProtocol.MAX_PACKED_SAMPLE_BYTES)
            {
                throw new InvalidOperationException($"State stream sample '{sample.Key}' packed state size is invalid.");
            }
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length <= 0 || length > 1024)
                throw new FormatException($"State stream store id byte length {length} is invalid.");
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
                throw new EndOfStreamException("State stream store id is truncated.");
            return Encoding.UTF8.GetString(bytes);
        }
    }
}
