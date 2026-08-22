using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Mirror.Protocol
{
    [Serializable, Preserve]
    public struct RuntimeCheckpointChunk
    {
        public ulong SessionId;
        public string CheckpointGroupId;
        public long CompletedTick;
        public ulong JournalCursor;
        public string CheckpointHash;
        public string SchemaHash;
        public ushort SectionIndex;
        public ushort SectionCount;
        public uint SectionId;
        public uint SectionVersion;
        public int SectionPayloadLength;
        public byte[] SectionPayloadHash;
        public ushort PageIndex;
        public ushort PageCount;
        public byte[] PagePayloadHash;
        public byte[] Payload;
    }

    public enum RuntimeCheckpointChunkResult : byte
    {
        Accepted = 0,
        Completed = 1,
        Duplicate = 2,
        DuplicateCompleted = 3,
        Invalid = 4,
        ConflictingTransfer = 5,
        Corrupt = 6,
        TimedOut = 7,
    }

    public static class RuntimeCheckpointChunker
    {
        public static IReadOnlyList<RuntimeCheckpointChunk> Split(
            ulong sessionId,
            RuntimeRecoveryCheckpoint checkpoint)
        {
            if (sessionId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionId));
            }
            if (checkpoint == null)
            {
                throw new ArgumentNullException(nameof(checkpoint));
            }

            RuntimeReplayCheckpointCodec.Validate(checkpoint.Envelope);
            var envelope = checkpoint.Envelope;
            var boundary = checkpoint.Boundary;
            if (envelope.Sections.Count > ushort.MaxValue)
            {
                throw new InvalidOperationException(
                    "Checkpoint section count exceeds the wire range.");
            }

            var totalPages = 0;
            for (var i = 0; i < envelope.Sections.Count; i++)
            {
                totalPages = checked(
                    totalPages + envelope.Sections[i].Pages.Count);
            }
            if (totalPages > RuntimeReplayCheckpointCodec.MAX_ENVELOPE_PAGES)
            {
                throw new InvalidOperationException(
                    $"Checkpoint contains {totalPages} pages; limit is {RuntimeReplayCheckpointCodec.MAX_ENVELOPE_PAGES}.");
            }

            if (envelope.Sections.Count == 0)
            {
                return new[]
                {
                    CreateHeaderOnlyChunk(
                        sessionId,
                        boundary,
                        envelope.SchemaHash),
                };
            }

            var result = new RuntimeCheckpointChunk[totalPages];
            var destination = 0;
            for (var sectionIndex = 0;
                 sectionIndex < envelope.Sections.Count;
                 sectionIndex++)
            {
                var section = envelope.Sections[sectionIndex];
                if (section.Pages.Count > ushort.MaxValue)
                {
                    throw new InvalidOperationException(
                        $"Checkpoint section {section.SectionId} page count exceeds the wire range.");
                }

                for (var pageIndex = 0;
                     pageIndex < section.Pages.Count;
                     pageIndex++)
                {
                    var page = section.Pages[pageIndex];
                    result[destination++] = new RuntimeCheckpointChunk
                    {
                        SessionId = sessionId,
                        CheckpointGroupId = boundary.GroupId,
                        CompletedTick = boundary.CompletedTick,
                        JournalCursor = boundary.JournalCursor,
                        CheckpointHash = boundary.CheckpointHash,
                        SchemaHash = envelope.SchemaHash,
                        SectionIndex = (ushort)sectionIndex,
                        SectionCount = (ushort)envelope.Sections.Count,
                        SectionId = section.SectionId,
                        SectionVersion = section.SectionVersion,
                        SectionPayloadLength = section.PayloadLength,
                        SectionPayloadHash = section.PayloadHash,
                        PageIndex = (ushort)pageIndex,
                        PageCount = (ushort)section.Pages.Count,
                        PagePayloadHash = page.PayloadHash,
                        Payload = page.Payload,
                    };
                }
            }

            return result;
        }

        private static RuntimeCheckpointChunk CreateHeaderOnlyChunk(
            ulong sessionId,
            in RuntimeCheckpointBoundary boundary,
            string schemaHash)
        {
            return new RuntimeCheckpointChunk
            {
                SessionId = sessionId,
                CheckpointGroupId = boundary.GroupId,
                CompletedTick = boundary.CompletedTick,
                JournalCursor = boundary.JournalCursor,
                CheckpointHash = boundary.CheckpointHash,
                SchemaHash = schemaHash,
                SectionCount = 0,
                Payload = Array.Empty<byte>(),
            };
        }
    }

    class RuntimeCheckpointSectionAssemblyState
    {
        public readonly uint SectionId;
        public readonly uint SectionVersion;
        public readonly int PayloadLength;
        public readonly byte[] PayloadHash;
        public readonly RuntimeReplayCheckpointPage[] Pages;
        public int ReceivedPages;

        public RuntimeCheckpointSectionAssemblyState(
            in RuntimeCheckpointChunk chunk)
        {
            SectionId = chunk.SectionId;
            SectionVersion = chunk.SectionVersion;
            PayloadLength = chunk.SectionPayloadLength;
            PayloadHash = (byte[])chunk.SectionPayloadHash.Clone();
            Pages = new RuntimeReplayCheckpointPage[chunk.PageCount];
        }

        public bool Matches(in RuntimeCheckpointChunk chunk)
        {
            return SectionId == chunk.SectionId
                   && SectionVersion == chunk.SectionVersion
                   && PayloadLength == chunk.SectionPayloadLength
                   && Pages.Length == chunk.PageCount
                   && RuntimeReplayHash.FixedTimeEquals(
                       PayloadHash,
                       chunk.SectionPayloadHash);
        }
    }

    public class RuntimeCheckpointChunkAssembler
    {
        private RuntimeCheckpointChunk _header;
        private RuntimeCheckpointSectionAssemblyState[] _sections;
        private int _observedSections;
        private int _declaredPages;
        private int _receivedPages;
        private double _startedAt;
        private ulong _lastCompletedSessionId;
        private string _lastCompletedCheckpointHash;

        public bool IsActive => _sections != null;

        public RuntimeCheckpointChunkResult Tick(double nowSeconds)
        {
            if (_sections == null)
            {
                return RuntimeCheckpointChunkResult.Accepted;
            }
            if (nowSeconds - _startedAt
                <= RuntimeProtocol.BASELINE_TIMEOUT_SECONDS)
            {
                return RuntimeCheckpointChunkResult.Accepted;
            }

            ResetActive();
            return RuntimeCheckpointChunkResult.TimedOut;
        }

        public RuntimeCheckpointChunkResult Accept(
            in RuntimeCheckpointChunk chunk,
            double nowSeconds,
            out RuntimeRecoveryCheckpoint completedCheckpoint)
        {
            completedCheckpoint = null;
            if (!ValidateHeader(chunk))
            {
                return RuntimeCheckpointChunkResult.Invalid;
            }
            if (IsLastCompleted(chunk))
            {
                return RuntimeCheckpointChunkResult.DuplicateCompleted;
            }
            if (_sections != null
                && nowSeconds - _startedAt
                > RuntimeProtocol.BASELINE_TIMEOUT_SECONDS)
            {
                ResetActive();
                return RuntimeCheckpointChunkResult.TimedOut;
            }

            if (_sections == null)
            {
                Begin(chunk, nowSeconds);
            }
            else if (!SameTransfer(_header, chunk))
            {
                return RuntimeCheckpointChunkResult.ConflictingTransfer;
            }

            if (chunk.SectionCount == 0)
            {
                return CompleteEmpty(out completedCheckpoint);
            }

            var sectionIndex = chunk.SectionIndex;
            var section = _sections[sectionIndex];
            if (section == null)
            {
                if (_declaredPages + chunk.PageCount
                    > RuntimeReplayCheckpointCodec.MAX_ENVELOPE_PAGES)
                {
                    ResetActive();
                    return RuntimeCheckpointChunkResult.Invalid;
                }

                section = new RuntimeCheckpointSectionAssemblyState(chunk);
                _sections[sectionIndex] = section;
                _observedSections++;
                _declaredPages += chunk.PageCount;
            }
            else if (!section.Matches(chunk))
            {
                ResetActive();
                return RuntimeCheckpointChunkResult.ConflictingTransfer;
            }

            var existing = section.Pages[chunk.PageIndex];
            if (existing != null)
            {
                if (!BytesEqual(existing.Payload, chunk.Payload)
                    || !BytesEqual(
                        existing.PayloadHash,
                        chunk.PagePayloadHash))
                {
                    ResetActive();
                    return RuntimeCheckpointChunkResult.Corrupt;
                }

                return RuntimeCheckpointChunkResult.Duplicate;
            }

            RuntimeReplayCheckpointPage page;
            try
            {
                page = new RuntimeReplayCheckpointPage(
                    chunk.PageIndex,
                    chunk.Payload,
                    chunk.PagePayloadHash);
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is InvalidOperationException
                || exception is OverflowException)
            {
                ResetActive();
                return RuntimeCheckpointChunkResult.Corrupt;
            }

            section.Pages[chunk.PageIndex] = page;
            section.ReceivedPages++;
            _receivedPages++;
            if (_observedSections != _sections.Length
                || _receivedPages != _declaredPages)
            {
                return RuntimeCheckpointChunkResult.Accepted;
            }

            return Complete(out completedCheckpoint);
        }

        public void Reset()
        {
            ResetActive();
            _lastCompletedSessionId = 0;
            _lastCompletedCheckpointHash = null;
        }

        private RuntimeCheckpointChunkResult CompleteEmpty(
            out RuntimeRecoveryCheckpoint completedCheckpoint)
        {
            try
            {
                var envelope = RuntimeReplayCheckpointCodec.Create(
                    _header.CompletedTick,
                    _header.JournalCursor,
                    _header.SchemaHash,
                    Array.Empty<RuntimeReplayCheckpointSection>());
                completedCheckpoint = CreateRecoveryCheckpoint(envelope);
                RememberCompleted();
                ResetActive();
                return RuntimeCheckpointChunkResult.Completed;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is InvalidOperationException
                || exception is OverflowException)
            {
                ResetActive();
                completedCheckpoint = null;
                return RuntimeCheckpointChunkResult.Corrupt;
            }
        }

        private RuntimeCheckpointChunkResult Complete(
            out RuntimeRecoveryCheckpoint completedCheckpoint)
        {
            try
            {
                var sections =
                    new RuntimeReplayCheckpointSection[_sections.Length];
                for (var i = 0; i < _sections.Length; i++)
                {
                    var section = _sections[i];
                    if (section == null
                        || section.ReceivedPages
                        != section.Pages.Length)
                    {
                        throw new InvalidOperationException(
                            $"Checkpoint section {i} is incomplete.");
                    }

                    sections[i] = new RuntimeReplayCheckpointSection(
                        section.SectionId,
                        section.SectionVersion,
                        section.Pages,
                        section.PayloadLength,
                        section.PayloadHash);
                }

                var envelope = RuntimeReplayCheckpointCodec.Create(
                    _header.CompletedTick,
                    _header.JournalCursor,
                    _header.SchemaHash,
                    sections);
                completedCheckpoint = CreateRecoveryCheckpoint(envelope);
                RememberCompleted();
                ResetActive();
                return RuntimeCheckpointChunkResult.Completed;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                || exception is InvalidOperationException
                || exception is OverflowException)
            {
                ResetActive();
                completedCheckpoint = null;
                return RuntimeCheckpointChunkResult.Corrupt;
            }
        }

        private RuntimeRecoveryCheckpoint CreateRecoveryCheckpoint(
            RuntimeReplayCheckpointEnvelope envelope)
        {
            var boundary = new RuntimeCheckpointBoundary(
                _header.CheckpointGroupId,
                _header.CompletedTick,
                _header.JournalCursor,
                _header.CheckpointHash);
            return new RuntimeRecoveryCheckpoint(boundary, envelope);
        }

        private void Begin(
            in RuntimeCheckpointChunk chunk,
            double nowSeconds)
        {
            _header = chunk;
            _header.Payload = null;
            _header.PagePayloadHash = null;
            _header.SectionPayloadHash = null;
            _sections =
                new RuntimeCheckpointSectionAssemblyState[
                    chunk.SectionCount];
            _observedSections = 0;
            _declaredPages = 0;
            _receivedPages = 0;
            _startedAt = nowSeconds;
        }

        private void RememberCompleted()
        {
            _lastCompletedSessionId = _header.SessionId;
            _lastCompletedCheckpointHash = _header.CheckpointHash;
        }

        private bool IsLastCompleted(in RuntimeCheckpointChunk chunk)
        {
            return chunk.SessionId == _lastCompletedSessionId
                   && string.Equals(
                       chunk.CheckpointHash,
                       _lastCompletedCheckpointHash,
                       StringComparison.Ordinal);
        }

        private static bool ValidateHeader(
            in RuntimeCheckpointChunk chunk)
        {
            if (chunk.SessionId == 0
                || chunk.CompletedTick < 0
                || string.IsNullOrWhiteSpace(chunk.CheckpointGroupId)
                || !RuntimeReplayHash.IsSha256Hex(chunk.CheckpointHash)
                || !RuntimeReplayHash.IsSha256Hex(chunk.SchemaHash)
                || chunk.SectionCount
                > RuntimeReplayCheckpointCodec.MAX_SECTIONS)
            {
                return false;
            }
            try
            {
                RuntimeReplayId.Validate(
                    chunk.CheckpointGroupId,
                    nameof(chunk.CheckpointGroupId));
            }
            catch (ArgumentException)
            {
                return false;
            }

            if (chunk.SectionCount == 0)
            {
                return chunk.SectionIndex == 0
                       && chunk.SectionId == 0
                       && chunk.SectionVersion == 0
                       && chunk.SectionPayloadLength == 0
                       && chunk.SectionPayloadHash == null
                       && chunk.PageIndex == 0
                       && chunk.PageCount == 0
                       && chunk.PagePayloadHash == null
                       && chunk.Payload != null
                       && chunk.Payload.Length == 0;
            }

            if (chunk.SectionIndex >= chunk.SectionCount
                || chunk.SectionVersion == 0
                || chunk.SectionPayloadLength < 0
                || chunk.SectionPayloadLength
                > RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES
                || chunk.SectionPayloadHash == null
                || chunk.SectionPayloadHash.Length
                != RuntimeReplayHash.SHA256_BYTES
                || chunk.PageCount == 0
                || chunk.PageCount
                > RuntimeReplayCheckpointCodec.MAX_PAGES_PER_SECTION
                || chunk.PageIndex >= chunk.PageCount
                || chunk.PagePayloadHash == null
                || chunk.PagePayloadHash.Length
                != RuntimeReplayHash.SHA256_BYTES
                || chunk.Payload == null
                || chunk.Payload.Length
                > RuntimeReplayCheckpointCodec.PAGE_BYTES)
            {
                return false;
            }

            var expectedPageCount = Math.Max(
                1,
                (chunk.SectionPayloadLength
                 + RuntimeReplayCheckpointCodec.PAGE_BYTES - 1)
                / RuntimeReplayCheckpointCodec.PAGE_BYTES);
            if (chunk.PageCount != expectedPageCount)
            {
                return false;
            }

            var expectedPageLength = chunk.PageIndex + 1
                < chunk.PageCount
                ? RuntimeReplayCheckpointCodec.PAGE_BYTES
                : chunk.SectionPayloadLength
                  - chunk.PageIndex
                  * RuntimeReplayCheckpointCodec.PAGE_BYTES;
            return chunk.Payload.Length == expectedPageLength;
        }

        private static bool SameTransfer(
            in RuntimeCheckpointChunk left,
            in RuntimeCheckpointChunk right)
        {
            return left.SessionId == right.SessionId
                   && left.CompletedTick == right.CompletedTick
                   && left.JournalCursor == right.JournalCursor
                   && left.SectionCount == right.SectionCount
                   && string.Equals(
                       left.CheckpointGroupId,
                       right.CheckpointGroupId,
                       StringComparison.Ordinal)
                   && string.Equals(
                       left.CheckpointHash,
                       right.CheckpointHash,
                       StringComparison.Ordinal)
                   && string.Equals(
                       left.SchemaHash,
                       right.SchemaHash,
                       StringComparison.Ordinal);
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            return RuntimeReplayHash.FixedTimeEquals(left, right);
        }

        private void ResetActive()
        {
            _header = default;
            _sections = null;
            _observedSections = 0;
            _declaredPages = 0;
            _receivedPages = 0;
            _startedAt = 0;
        }

    }
}
