using System;
using System.Collections.Generic;
using Unity.Entities;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    /// <summary>
    /// Explicit structural-change barrier shared by durable and network
    /// checkpoint restore. Factory projection and store retirement both use
    /// the EndSimulation ECB, so a restore coordinator must cross this
    /// barrier before observing projected topology or completing publication.
    /// </summary>
    public static class RuntimeCheckpointProjectionBarrier
    {
        public static void Playback(World world)
        {
            if (world == null || !world.IsCreated)
            {
                throw new ArgumentException(
                    "Checkpoint projection playback requires an active ECS World.",
                    nameof(world));
            }

            world.EntityManager.CompleteAllTrackedJobs();
            var playback = world.GetExistingSystemManaged<
                EndSimulationEntityCommandBufferSystem>()
                           ?? throw new InvalidOperationException(
                               "Checkpoint restore requires "
                               + "EndSimulationEntityCommandBufferSystem.");
            playback.Update();
            world.EntityManager.CompleteAllTrackedJobs();
        }
    }

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

    /// <summary>
    /// Adds participant-specific state-schema identity to the checkpoint
    /// section schema. Implement this when the binary section layout is
    /// driven by a generated component/codec schema in addition to the
    /// participant CLR type and section version.
    /// </summary>
    public interface IRuntimeReplayCheckpointSchemaFingerprintContributor
    {
        void AppendCheckpointSchemaFingerprint(
            RuntimeReplayCheckpointWriter writer);
    }

    public delegate void RuntimeReplayBinaryMigration(
        RuntimeReplayCheckpointReader source,
        RuntimeReplayCheckpointWriter destination);

    public class RuntimeReplayCheckpointPage
    {
        private readonly byte[] _payload;
        private readonly byte[] _payloadHash;

        public readonly int PageIndex;
        public readonly int PayloadLength;

        public byte[] Payload => (byte[])_payload.Clone();
        public byte[] PayloadHash => (byte[])_payloadHash.Clone();

        internal byte[] UnsafePayload => _payload;
        internal byte[] UnsafePayloadHash => _payloadHash;

        public RuntimeReplayCheckpointPage(
            int pageIndex,
            byte[] payload,
            byte[] payloadHash = null)
        {
            if (pageIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            }
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            if (payload.Length > RuntimeReplayCheckpointCodec.PAGE_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payload),
                    $"Checkpoint page exceeds {RuntimeReplayCheckpointCodec.PAGE_BYTES} bytes.");
            }

            var expectedHash =
                RuntimeReplayHash.CalculateSha256(payload);
            var hash = payloadHash ?? expectedHash;
            RuntimeReplayHash.RequireSha256(hash, nameof(payloadHash));
            if (!RuntimeReplayHash.FixedTimeEquals(expectedHash, hash))
            {
                throw new ArgumentException(
                    "Checkpoint page hash does not match its payload.",
                    nameof(payloadHash));
            }

            PageIndex = pageIndex;
            PayloadLength = payload.Length;
            _payload = (byte[])payload.Clone();
            _payloadHash = (byte[])hash.Clone();
        }
    }

    public class RuntimeReplayCheckpointSection
    {
        private readonly RuntimeReplayCheckpointPage[] _pages;
        private readonly IReadOnlyList<RuntimeReplayCheckpointPage>
            _readOnlyPages;
        private readonly byte[] _payloadHash;

        public readonly uint SectionId;
        public readonly uint SectionVersion;
        public readonly int PayloadLength;

        public IReadOnlyList<RuntimeReplayCheckpointPage> Pages =>
            _readOnlyPages;
        public byte[] Payload => CopyPayload();
        public byte[] PayloadHash => (byte[])_payloadHash.Clone();

        public RuntimeReplayCheckpointSection(
            uint sectionId,
            uint sectionVersion,
            byte[] payload,
            byte[] payloadHash)
            : this(
                sectionId,
                sectionVersion,
                RuntimeReplayCheckpointPageUtils.Split(payload),
                payload?.Length ?? 0,
                payloadHash)
        {
        }

        public RuntimeReplayCheckpointSection(
            uint sectionId,
            uint sectionVersion,
            IReadOnlyList<RuntimeReplayCheckpointPage> pages,
            int payloadLength,
            byte[] payloadHash)
        {
            if (sectionVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sectionVersion));
            }
            if (pages == null)
            {
                throw new ArgumentNullException(nameof(pages));
            }
            if (pages.Count == 0
                || pages.Count > RuntimeReplayCheckpointCodec.MAX_PAGES_PER_SECTION)
            {
                throw new ArgumentOutOfRangeException(nameof(pages));
            }
            if (payloadLength < 0
                || payloadLength > RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength));
            }
            RuntimeReplayHash.RequireSha256(payloadHash, nameof(payloadHash));

            _pages = new RuntimeReplayCheckpointPage[pages.Count];
            var actualLength = 0;
            for (var i = 0; i < pages.Count; i++)
            {
                var page = pages[i]
                           ?? throw new ArgumentException(
                               $"Checkpoint page {i} is null.",
                               nameof(pages));
                if (page.PageIndex != i)
                {
                    throw new ArgumentException(
                        $"Checkpoint page {i} reports index {page.PageIndex}.",
                        nameof(pages));
                }
                if (i + 1 < pages.Count
                    && page.PayloadLength
                    != RuntimeReplayCheckpointCodec.PAGE_BYTES)
                {
                    throw new ArgumentException(
                        $"Checkpoint page {i} is not a full intermediate page.",
                        nameof(pages));
                }

                actualLength = checked(actualLength + page.PayloadLength);
                _pages[i] = page;
            }
            if (actualLength != payloadLength)
            {
                throw new ArgumentException(
                    $"Checkpoint pages contain {actualLength} bytes, expected {payloadLength}.",
                    nameof(payloadLength));
            }
            var expectedPageCount = Math.Max(
                1,
                (payloadLength + RuntimeReplayCheckpointCodec.PAGE_BYTES - 1)
                / RuntimeReplayCheckpointCodec.PAGE_BYTES);
            if (pages.Count != expectedPageCount)
            {
                throw new ArgumentException(
                    $"Checkpoint payload requires {expectedPageCount} pages, received {pages.Count}.",
                    nameof(pages));
            }

            var expectedSectionHash =
                RuntimeReplayHash.CalculateSha256(_pages, payloadLength);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedSectionHash,
                    payloadHash))
            {
                throw new ArgumentException(
                    "Checkpoint section hash does not match its pages.",
                    nameof(payloadHash));
            }

            SectionId = sectionId;
            SectionVersion = sectionVersion;
            PayloadLength = payloadLength;
            _payloadHash = (byte[])payloadHash.Clone();
            _readOnlyPages = Array.AsReadOnly(_pages);
        }

        public byte[] CopyPayload()
        {
            var result = new byte[PayloadLength];
            var offset = 0;
            for (var i = 0; i < _pages.Length; i++)
            {
                var source = _pages[i].UnsafePayload;
                if (source.Length > 0)
                {
                    Buffer.BlockCopy(
                        source,
                        0,
                        result,
                        offset,
                        source.Length);
                }
                offset += source.Length;
            }
            return result;
        }
    }

    public static class RuntimeReplayCheckpointPageUtils
    {
        public static IReadOnlyList<RuntimeReplayCheckpointPage> Split(
            byte[] payload,
            int maxBytes = RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }
            if (payload.Length > maxBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(payload));
            }

            var pageCount = Math.Max(
                1,
                (payload.Length + RuntimeReplayCheckpointCodec.PAGE_BYTES - 1)
                / RuntimeReplayCheckpointCodec.PAGE_BYTES);
            var result = new RuntimeReplayCheckpointPage[pageCount];
            for (var i = 0; i < pageCount; i++)
            {
                var offset = i * RuntimeReplayCheckpointCodec.PAGE_BYTES;
                var length = Math.Min(
                    RuntimeReplayCheckpointCodec.PAGE_BYTES,
                    payload.Length - offset);
                if (length < 0)
                {
                    length = 0;
                }

                var pagePayload = new byte[length];
                if (length > 0)
                {
                    Buffer.BlockCopy(
                        payload,
                        offset,
                        pagePayload,
                        0,
                        length);
                }
                result[i] = new RuntimeReplayCheckpointPage(
                    i,
                    pagePayload);
            }
            return Array.AsReadOnly(result);
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
