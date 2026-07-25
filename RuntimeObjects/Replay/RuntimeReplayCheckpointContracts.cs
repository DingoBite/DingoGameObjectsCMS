using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public interface IRuntimeReplayCheckpointParticipant
    {
        uint SectionId { get; }
        uint CurrentVersion { get; }

        void Capture(RuntimeReplayCheckpointWriter writer);
        void Restore(RuntimeReplayCheckpointReader reader);
        void AppendFingerprint(RuntimeReplayCheckpointWriter writer);
    }

    public interface IRuntimeReplayCheckpointPrevalidator
    {
        void Prevalidate(RuntimeReplayCheckpointReader reader);
    }

    public delegate void RuntimeReplayBinaryMigration(
        RuntimeReplayCheckpointReader source,
        RuntimeReplayCheckpointWriter destination);

    public class RuntimeReplayCheckpointSection
    {
        public readonly uint SectionId;
        public readonly uint SectionVersion;
        public readonly byte[] Payload;
        public readonly byte[] PayloadHash;

        public RuntimeReplayCheckpointSection(
            uint sectionId,
            uint sectionVersion,
            byte[] payload,
            byte[] payloadHash)
        {
            if (sectionVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sectionVersion));
            }
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            RuntimeReplayHash.RequireSha256(payloadHash, nameof(payloadHash));

            SectionId = sectionId;
            SectionVersion = sectionVersion;
            Payload = (byte[])payload.Clone();
            PayloadHash = (byte[])payloadHash.Clone();
        }
    }

    public class RuntimeReplayCheckpointEnvelope
    {
        public readonly long CompletedTick;
        public readonly ulong Cursor;
        public readonly string SchemaHash;
        public readonly IReadOnlyList<RuntimeReplayCheckpointSection> Sections;
        public readonly byte[] OverallHash;

        public RuntimeReplayCheckpointEnvelope(
            long completedTick,
            ulong cursor,
            string schemaHash,
            IReadOnlyList<RuntimeReplayCheckpointSection> sections,
            byte[] overallHash)
        {
            if (completedTick < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(completedTick));
            }
            if (!RuntimeReplayHash.IsSha256Hex(schemaHash))
            {
                throw new ArgumentException(
                    "Checkpoint schema hash must be a SHA-256 hex string.",
                    nameof(schemaHash));
            }
            if (sections == null)
            {
                throw new ArgumentNullException(nameof(sections));
            }
            RuntimeReplayHash.RequireSha256(overallHash, nameof(overallHash));

            var sectionCopy = new RuntimeReplayCheckpointSection[sections.Count];
            for (var i = 0; i < sections.Count; i++)
            {
                sectionCopy[i] = sections[i]
                                 ?? throw new ArgumentException(
                                     $"Checkpoint section {i} is null.",
                                     nameof(sections));
            }

            CompletedTick = completedTick;
            Cursor = cursor;
            SchemaHash = schemaHash.ToLowerInvariant();
            Sections = Array.AsReadOnly(sectionCopy);
            OverallHash = (byte[])overallHash.Clone();
        }
    }

    public static class RuntimeReplayId
    {
        public const int MAX_ID_CHARS = 128;

        public static void Validate(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value)
                || value.Length > MAX_ID_CHARS
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    $"Replay id must contain 1..{MAX_ID_CHARS} non-whitespace characters without outer whitespace.",
                    parameterName);
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var valid = c >= 'a' && c <= 'z'
                            || c >= 'A' && c <= 'Z'
                            || c >= '0' && c <= '9'
                            || c == '.'
                            || c == '_'
                            || c == '-'
                            || c == '/';
                if (!valid)
                {
                    throw new ArgumentException(
                        $"Replay id '{value}' contains unsupported character '{c}'.",
                        parameterName);
                }
            }
        }
    }
}
