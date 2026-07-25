using System;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects.Commands
{
    public enum RuntimeCommandsBusMode : byte
    {
        AutomaticLateUpdate = 0,
        ExternalTickBarrier = 1,
    }

    public enum RuntimeCommandExecutionStatus : byte
    {
        Succeeded = 0,
        Failed = 1,
        Unsupported = 2,
    }

    [Serializable, Preserve]
    public struct RuntimeReplayObjectRef : IEquatable<RuntimeReplayObjectRef>
    {
        public FixedString32Bytes StoreId;
        public Hash128 InstanceGuid;

        public bool IsDefault => StoreId.Length == 0 && !InstanceGuid.isValid;
        public bool IsValid => StoreId.Length > 0 && InstanceGuid.isValid;

        public RuntimeReplayObjectRef(FixedString32Bytes storeId, Hash128 instanceGuid)
        {
            if ((storeId.Length == 0) != !instanceGuid.isValid)
                throw new ArgumentException("A replay object reference requires both StoreId and object GUID.");

            StoreId = storeId;
            InstanceGuid = instanceGuid;
        }

        public static RuntimeReplayObjectRef FromRuntimeInstance(
            in RuntimeInstance instance,
            RuntimePersistentPatchCodecContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            var persistent = context.EncodePersistentReference(instance);
            return new RuntimeReplayObjectRef(persistent.StoreId, persistent.ObjectGuid);
        }

        public RuntimeInstance Resolve(RuntimePersistentPatchCodecContext context)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));
            if (IsDefault)
                return default;
            if (!IsValid)
                throw new InvalidOperationException("Replay object reference is incomplete.");

            return context.DecodePersistentReference(
                new RuntimePatchObjectReference(StoreId, InstanceGuid));
        }

        public void Write(CanonicalPatchBinaryWriter writer)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (IsDefault)
            {
                writer.WriteBoolean(false);
                return;
            }
            if (!IsValid)
                throw new InvalidOperationException("Replay object reference is incomplete.");

            writer.WriteBoolean(true);
            writer.WriteString(StoreId.ToString());
            writer.WriteHash128(InstanceGuid);
        }

        public static RuntimeReplayObjectRef Read(CanonicalPatchBinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (!reader.ReadBoolean())
                return default;

            var storeId = reader.ReadString();
            if (string.IsNullOrWhiteSpace(storeId))
                throw new FormatException("Replay object reference has an empty StoreId.");

            return new RuntimeReplayObjectRef(new FixedString32Bytes(storeId), reader.ReadHash128());
        }

        public bool Equals(RuntimeReplayObjectRef other)
        {
            return StoreId.Equals(other.StoreId) && InstanceGuid == other.InstanceGuid;
        }

        public override bool Equals(object obj)
        {
            return obj is RuntimeReplayObjectRef other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(StoreId, InstanceGuid);
        }

        public static bool operator ==(RuntimeReplayObjectRef left, RuntimeReplayObjectRef right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RuntimeReplayObjectRef left, RuntimeReplayObjectRef right)
        {
            return !left.Equals(right);
        }
    }

    [Serializable, Preserve]
    public struct RuntimeEncodedCommand
    {
        public uint TypeId;
        public ushort CodecVersion;
        public byte[] Payload;

        public bool IsValid => TypeId > 0 && CodecVersion > 0 && Payload != null;
        public int PayloadBytes => Payload?.Length ?? 0;

        public RuntimeEncodedCommand(uint typeId, ushort codecVersion, byte[] payload)
        {
            if (typeId == 0)
                throw new ArgumentOutOfRangeException(nameof(typeId));
            if (codecVersion == 0)
                throw new ArgumentOutOfRangeException(nameof(codecVersion));

            TypeId = typeId;
            CodecVersion = codecVersion;
            Payload = CopyPayload(payload);
        }

        public RuntimeEncodedCommand Copy()
        {
            return new RuntimeEncodedCommand(TypeId, CodecVersion, Payload);
        }

        public static byte[] CopyPayload(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return Array.Empty<byte>();

            var copy = new byte[payload.Length];
            Buffer.BlockCopy(payload, 0, copy, 0, payload.Length);
            return copy;
        }
    }

    [Serializable, Preserve]
    public struct RuntimeCommandJournalEntry
    {
        public long ApplyBeforeTick;
        public ulong Sequence;
        public RuntimeEncodedCommand EncodedCommand;

        public RuntimeCommandJournalEntry(
            long applyBeforeTick,
            ulong sequence,
            in RuntimeEncodedCommand encodedCommand)
        {
            if (applyBeforeTick < 0)
                throw new ArgumentOutOfRangeException(nameof(applyBeforeTick));
            if (sequence == 0)
                throw new ArgumentOutOfRangeException(nameof(sequence));
            if (!encodedCommand.IsValid)
                throw new ArgumentException(
                    "Journal command snapshot is invalid.",
                    nameof(encodedCommand));

            ApplyBeforeTick = applyBeforeTick;
            Sequence = sequence;
            EncodedCommand = encodedCommand.Copy();
        }
    }

    public readonly struct RuntimeCommandExecutionResult
    {
        public readonly ulong ExecutionId;
        public readonly long ApplyBeforeTick;
        public readonly GameRuntimeCommand Command;
        public readonly RuntimeCommandExecutionStatus Status;
        public readonly RuntimeEncodedCommand EncodedCommand;
        public readonly RuntimeCommandJournalEntry JournalEntry;
        public readonly Exception Exception;
        public readonly bool ReplayJournalExcluded;

        public bool Succeeded => Status == RuntimeCommandExecutionStatus.Succeeded;
        public bool Failed => Status == RuntimeCommandExecutionStatus.Failed;
        public bool Unsupported => Status == RuntimeCommandExecutionStatus.Unsupported;
        public bool HasEncodedCommand => EncodedCommand.IsValid;
        public bool HasJournalEntry => JournalEntry.Sequence > 0;

        public RuntimeCommandExecutionResult(
            ulong executionId,
            long applyBeforeTick,
            GameRuntimeCommand command,
            RuntimeCommandExecutionStatus status,
            in RuntimeEncodedCommand encodedCommand,
            in RuntimeCommandJournalEntry journalEntry,
            Exception exception,
            bool replayJournalExcluded = false)
        {
            if (executionId == 0)
                throw new ArgumentOutOfRangeException(nameof(executionId));
            if (applyBeforeTick < 0)
                throw new ArgumentOutOfRangeException(nameof(applyBeforeTick));
            if (command == null)
                throw new ArgumentNullException(nameof(command));
            if (status != RuntimeCommandExecutionStatus.Succeeded
                && status != RuntimeCommandExecutionStatus.Failed
                && status != RuntimeCommandExecutionStatus.Unsupported)
            {
                throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown runtime command execution status.");
            }
            if (status == RuntimeCommandExecutionStatus.Succeeded && exception != null)
                throw new ArgumentException("A successful command result cannot contain an exception.", nameof(exception));
            if (status != RuntimeCommandExecutionStatus.Succeeded && exception == null)
                throw new ArgumentException("A non-successful command result requires an exception.", nameof(exception));
            if (replayJournalExcluded
                && (encodedCommand.IsValid || journalEntry.Sequence > 0))
            {
                throw new ArgumentException(
                    "A command excluded from the replay journal cannot carry encoded replay data.");
            }

            ExecutionId = executionId;
            ApplyBeforeTick = applyBeforeTick;
            Command = command;
            Status = status;
            EncodedCommand = encodedCommand.IsValid ? encodedCommand.Copy() : default;
            JournalEntry = journalEntry.Sequence > 0
                ? new RuntimeCommandJournalEntry(
                    journalEntry.ApplyBeforeTick,
                    journalEntry.Sequence,
                    journalEntry.EncodedCommand)
                : default;
            Exception = exception;
            ReplayJournalExcluded = replayJournalExcluded;
        }
    }
}
