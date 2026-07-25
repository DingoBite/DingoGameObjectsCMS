using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public class RuntimeReplayCheckpointFingerprintSection
    {
        private readonly byte[] _fingerprintHash;

        public readonly uint SectionId;

        public byte[] FingerprintHash =>
            (byte[])_fingerprintHash.Clone();

        public RuntimeReplayCheckpointFingerprintSection(
            uint sectionId,
            byte[] fingerprintHash)
        {
            RuntimeReplayHash.RequireSha256(
                fingerprintHash,
                nameof(fingerprintHash));
            SectionId = sectionId;
            _fingerprintHash =
                (byte[])fingerprintHash.Clone();
        }
    }

    public class RuntimeReplayCheckpointFingerprintEnvelope
    {
        private readonly byte[] _overallHash;

        public readonly long CompletedTick;
        public readonly string SchemaHash;
        public readonly IReadOnlyList<
            RuntimeReplayCheckpointFingerprintSection> Sections;

        public byte[] OverallHash => (byte[])_overallHash.Clone();

        public RuntimeReplayCheckpointFingerprintEnvelope(
            long completedTick,
            string schemaHash,
            IReadOnlyList<
                RuntimeReplayCheckpointFingerprintSection> sections,
            byte[] overallHash)
        {
            if (completedTick < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completedTick));
            }
            if (!RuntimeReplayHash.IsSha256Hex(schemaHash))
            {
                throw new ArgumentException(
                    "Checkpoint fingerprint schema hash must be a SHA-256 hex string.",
                    nameof(schemaHash));
            }
            if (sections == null)
            {
                throw new ArgumentNullException(nameof(sections));
            }
            RuntimeReplayHash.RequireSha256(
                overallHash,
                nameof(overallHash));

            var copy =
                new RuntimeReplayCheckpointFingerprintSection[
                    sections.Count];
            for (var i = 0; i < sections.Count; i++)
            {
                copy[i] = sections[i]
                          ?? throw new ArgumentException(
                              $"Checkpoint fingerprint section {i} is null.",
                              nameof(sections));
            }

            CompletedTick = completedTick;
            SchemaHash = schemaHash.ToLowerInvariant();
            Sections = Array.AsReadOnly(copy);
            _overallHash = (byte[])overallHash.Clone();
        }
    }

    public static class RuntimeReplayCheckpointFingerprintCodec
    {
        public const uint FORMAT_MAGIC = 0x31465243;
        public const uint FORMAT_VERSION = 1;
        public const int MAX_ENVELOPE_BYTES = 1024 * 1024;

        public static RuntimeReplayCheckpointFingerprintEnvelope Create(
            long completedTick,
            string schemaHash,
            IReadOnlyList<
                RuntimeReplayCheckpointFingerprintSection> sections)
        {
            var body = EncodeBody(
                completedTick,
                schemaHash,
                sections);
            var result =
                new RuntimeReplayCheckpointFingerprintEnvelope(
                    completedTick,
                    schemaHash,
                    sections,
                    RuntimeReplayHash.CalculateSha256(body));
            Validate(result);
            return result;
        }

        public static byte[] Encode(
            RuntimeReplayCheckpointFingerprintEnvelope value)
        {
            Validate(value);
            var body = EncodeBody(
                value.CompletedTick,
                value.SchemaHash,
                value.Sections);
            using var writer =
                new RuntimeReplayCheckpointWriter(
                    initialCapacity:
                    body.Length
                    + RuntimeReplayHash.SHA256_BYTES,
                    maxBytes: MAX_ENVELOPE_BYTES);
            writer.WriteRawBytes(body);
            writer.WriteRawBytes(value.OverallHash);
            return writer.ToArray();
        }

        public static RuntimeReplayCheckpointFingerprintEnvelope Decode(
            byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            using var reader =
                new RuntimeReplayCheckpointReader(
                    payload,
                    MAX_ENVELOPE_BYTES);
            var magic = reader.ReadUInt32();
            if (magic != FORMAT_MAGIC)
            {
                throw new FormatException(
                    $"Checkpoint fingerprint magic 0x{magic:x8} does not match 0x{FORMAT_MAGIC:x8}.");
            }
            var version = reader.ReadUInt32();
            if (version != FORMAT_VERSION)
            {
                throw new FormatException(
                    $"Checkpoint fingerprint format version {version} is not supported.");
            }
            var completedTick = reader.ReadInt64();
            var schemaHash = reader.ReadString(
                RuntimeReplayHash.SHA256_HEX_CHARS);
            var sectionCount = reader.ReadInt32();
            if (sectionCount < 0
                || sectionCount
                > RuntimeReplayCheckpointCodec.MAX_SECTIONS)
            {
                throw new FormatException(
                    $"Checkpoint fingerprint section count {sectionCount} is invalid.");
            }

            var sections =
                new RuntimeReplayCheckpointFingerprintSection[
                    sectionCount];
            for (var i = 0; i < sectionCount; i++)
            {
                sections[i] =
                    new RuntimeReplayCheckpointFingerprintSection(
                        reader.ReadUInt32(),
                        reader.ReadRawBytes(
                            RuntimeReplayHash.SHA256_BYTES));
            }
            var overallHash = reader.ReadRawBytes(
                RuntimeReplayHash.SHA256_BYTES);
            reader.RequireEnd();

            var result =
                new RuntimeReplayCheckpointFingerprintEnvelope(
                    completedTick,
                    schemaHash,
                    sections,
                    overallHash);
            Validate(result);
            return result;
        }

        public static void Validate(
            RuntimeReplayCheckpointFingerprintEnvelope value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            if (value.CompletedTick < -1)
            {
                throw new InvalidOperationException(
                    $"Checkpoint fingerprint completed tick {value.CompletedTick} is invalid.");
            }
            if (!RuntimeReplayHash.IsSha256Hex(value.SchemaHash))
            {
                throw new InvalidOperationException(
                    "Checkpoint fingerprint schema hash is invalid.");
            }
            if (value.Sections == null
                || value.Sections.Count
                > RuntimeReplayCheckpointCodec.MAX_SECTIONS)
            {
                throw new InvalidOperationException(
                    "Checkpoint fingerprint section collection is invalid.");
            }

            uint previousSectionId = 0;
            for (var i = 0; i < value.Sections.Count; i++)
            {
                var section = value.Sections[i]
                              ?? throw new InvalidOperationException(
                                  $"Checkpoint fingerprint section {i} is null.");
                if (i > 0
                    && previousSectionId >= section.SectionId)
                {
                    throw new InvalidOperationException(
                        "Checkpoint fingerprint sections must be unique and ordered by SectionId.");
                }
                RuntimeReplayHash.RequireSha256(
                    section.FingerprintHash,
                    nameof(section.FingerprintHash));
                previousSectionId = section.SectionId;
            }

            RuntimeReplayHash.RequireSha256(
                value.OverallHash,
                nameof(value.OverallHash));
            var body = EncodeBody(
                value.CompletedTick,
                value.SchemaHash,
                value.Sections);
            var expectedOverallHash =
                RuntimeReplayHash.CalculateSha256(body);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedOverallHash,
                    value.OverallHash))
            {
                throw new InvalidOperationException(
                    "Checkpoint fingerprint overall hash mismatch.");
            }
        }

        private static byte[] EncodeBody(
            long completedTick,
            string schemaHash,
            IReadOnlyList<
                RuntimeReplayCheckpointFingerprintSection> sections)
        {
            if (completedTick < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(completedTick));
            }
            if (!RuntimeReplayHash.IsSha256Hex(schemaHash))
            {
                throw new ArgumentException(
                    "Checkpoint fingerprint schema hash must be a SHA-256 hex string.",
                    nameof(schemaHash));
            }
            if (sections == null
                || sections.Count
                > RuntimeReplayCheckpointCodec.MAX_SECTIONS)
            {
                throw new ArgumentException(
                    "Checkpoint fingerprint section collection is invalid.",
                    nameof(sections));
            }

            using var writer =
                new RuntimeReplayCheckpointWriter(
                    maxBytes: MAX_ENVELOPE_BYTES
                              - RuntimeReplayHash.SHA256_BYTES);
            writer.WriteUInt32(FORMAT_MAGIC);
            writer.WriteUInt32(FORMAT_VERSION);
            writer.WriteInt64(completedTick);
            writer.WriteString(schemaHash.ToLowerInvariant());
            writer.WriteInt32(sections.Count);
            for (var i = 0; i < sections.Count; i++)
            {
                var section = sections[i]
                              ?? throw new ArgumentException(
                                  $"Checkpoint fingerprint section {i} is null.",
                                  nameof(sections));
                writer.WriteUInt32(section.SectionId);
                writer.WriteRawBytes(section.FingerprintHash);
            }
            return writer.ToArray();
        }
    }
}
