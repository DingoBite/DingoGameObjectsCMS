using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public static class RuntimeReplayCheckpointCodec
    {
        public const uint FORMAT_MAGIC = 0x31504352;
        public const uint FORMAT_VERSION = 1;
        public const int MAX_SECTIONS = 4096;
        public const int MAX_SECTION_BYTES = 16 * 1024 * 1024;
        public const int MAX_ENVELOPE_BYTES = 16 * 1024 * 1024;

        public static RuntimeReplayCheckpointEnvelope Create(
            long completedTick,
            ulong cursor,
            string schemaHash,
            IReadOnlyList<RuntimeReplayCheckpointSection> sections)
        {
            var body = EncodeBody(completedTick, cursor, schemaHash, sections);
            var overallHash = RuntimeReplayHash.CalculateSha256(body);
            var result = new RuntimeReplayCheckpointEnvelope(
                completedTick,
                cursor,
                schemaHash,
                sections,
                overallHash);
            Validate(result);
            return result;
        }

        public static byte[] Encode(RuntimeReplayCheckpointEnvelope value)
        {
            Validate(value);
            var body = EncodeBody(
                value.CompletedTick,
                value.Cursor,
                value.SchemaHash,
                value.Sections);
            using var writer = new RuntimeReplayCheckpointWriter(
                body.Length + RuntimeReplayHash.SHA256_BYTES,
                MAX_ENVELOPE_BYTES);
            writer.WriteRawBytes(body);
            writer.WriteRawBytes(value.OverallHash);
            return writer.ToArray();
        }

        public static RuntimeReplayCheckpointEnvelope Decode(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            if (payload.Length > MAX_ENVELOPE_BYTES)
            {
                throw new FormatException(
                    $"Checkpoint envelope is {payload.Length} bytes; maximum is {MAX_ENVELOPE_BYTES}.");
            }

            using var reader = new RuntimeReplayCheckpointReader(
                payload,
                MAX_ENVELOPE_BYTES);
            var magic = reader.ReadUInt32();
            if (magic != FORMAT_MAGIC)
            {
                throw new FormatException(
                    $"Checkpoint magic 0x{magic:x8} does not match 0x{FORMAT_MAGIC:x8}.");
            }
            var version = reader.ReadUInt32();
            if (version != FORMAT_VERSION)
            {
                throw new FormatException(
                    $"Checkpoint format version {version} is not supported.");
            }

            var completedTick = reader.ReadInt64();
            var cursor = reader.ReadUInt64();
            var schemaHash = reader.ReadString(RuntimeReplayHash.SHA256_HEX_CHARS);
            var sectionCount = reader.ReadInt32();
            if (sectionCount < 0 || sectionCount > MAX_SECTIONS)
            {
                throw new FormatException(
                    $"Checkpoint section count {sectionCount} is outside 0..{MAX_SECTIONS}.");
            }

            var sections = new RuntimeReplayCheckpointSection[sectionCount];
            for (var i = 0; i < sectionCount; i++)
            {
                var sectionId = reader.ReadUInt32();
                var sectionVersion = reader.ReadUInt32();
                var sectionPayload = reader.ReadBytes(MAX_SECTION_BYTES);
                var sectionHash = reader.ReadRawBytes(RuntimeReplayHash.SHA256_BYTES);
                sections[i] = new RuntimeReplayCheckpointSection(
                    sectionId,
                    sectionVersion,
                    sectionPayload,
                    sectionHash);
            }

            var overallHash = reader.ReadRawBytes(RuntimeReplayHash.SHA256_BYTES);
            reader.RequireEnd();
            var result = new RuntimeReplayCheckpointEnvelope(
                completedTick,
                cursor,
                schemaHash,
                sections,
                overallHash);
            Validate(result);
            return result;
        }

        public static void Validate(RuntimeReplayCheckpointEnvelope value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            if (value.CompletedTick < -1)
            {
                throw new InvalidOperationException(
                    $"Checkpoint completed tick {value.CompletedTick} is invalid.");
            }
            if (!RuntimeReplayHash.IsSha256Hex(value.SchemaHash))
            {
                throw new InvalidOperationException(
                    "Checkpoint schema hash is not a SHA-256 hex string.");
            }
            if (value.Sections == null || value.Sections.Count > MAX_SECTIONS)
            {
                throw new InvalidOperationException(
                    $"Checkpoint section collection must contain 0..{MAX_SECTIONS} entries.");
            }
            RuntimeReplayHash.RequireSha256(value.OverallHash, nameof(value.OverallHash));

            uint previousId = 0;
            for (var i = 0; i < value.Sections.Count; i++)
            {
                var section = value.Sections[i]
                              ?? throw new InvalidOperationException(
                                  $"Checkpoint section {i} is null.");
                if (section.SectionVersion == 0)
                {
                    throw new InvalidOperationException(
                        $"Checkpoint section '{section.SectionId}' has version zero.");
                }
                if (section.Payload == null
                    || section.Payload.Length > MAX_SECTION_BYTES)
                {
                    throw new InvalidOperationException(
                        $"Checkpoint section '{section.SectionId}' payload exceeds {MAX_SECTION_BYTES} bytes.");
                }
                if (i > 0
                    && previousId >= section.SectionId)
                {
                    throw new InvalidOperationException(
                        "Checkpoint sections must be unique and ordered by SectionId.");
                }

                RuntimeReplayHash.RequireSha256(
                    section.PayloadHash,
                    nameof(section.PayloadHash));
                var expectedSectionHash =
                    RuntimeReplayHash.CalculateSha256(section.Payload);
                if (!RuntimeReplayHash.FixedTimeEquals(
                        expectedSectionHash,
                        section.PayloadHash))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint section {section.SectionId} hash mismatch.");
                }
                previousId = section.SectionId;
            }

            var body = EncodeBody(
                value.CompletedTick,
                value.Cursor,
                value.SchemaHash,
                value.Sections);
            var expectedOverallHash = RuntimeReplayHash.CalculateSha256(body);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedOverallHash,
                    value.OverallHash))
            {
                throw new InvalidOperationException(
                    "Checkpoint overall hash mismatch.");
            }
        }

        private static byte[] EncodeBody(
            long completedTick,
            ulong cursor,
            string schemaHash,
            IReadOnlyList<RuntimeReplayCheckpointSection> sections)
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
            if (sections == null || sections.Count > MAX_SECTIONS)
            {
                throw new ArgumentException(
                    $"Checkpoint must contain 0..{MAX_SECTIONS} sections.",
                    nameof(sections));
            }

            using var writer = new RuntimeReplayCheckpointWriter(
                initialCapacity: 1024,
                maxBytes: MAX_ENVELOPE_BYTES - RuntimeReplayHash.SHA256_BYTES);
            writer.WriteUInt32(FORMAT_MAGIC);
            writer.WriteUInt32(FORMAT_VERSION);
            writer.WriteInt64(completedTick);
            writer.WriteUInt64(cursor);
            writer.WriteString(schemaHash.ToLowerInvariant());
            writer.WriteInt32(sections.Count);
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i]
                              ?? throw new ArgumentException(
                                  $"Checkpoint section {i} is null.",
                                  nameof(sections));
                writer.WriteUInt32(section.SectionId);
                writer.WriteUInt32(section.SectionVersion);
                writer.WriteBytes(section.Payload);
                writer.WriteRawBytes(section.PayloadHash);
            }
            return writer.ToArray();
        }
    }
}
