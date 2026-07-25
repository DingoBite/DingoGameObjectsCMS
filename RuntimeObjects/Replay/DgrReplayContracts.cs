using System;
using System.Collections.Generic;
using System.Linq;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public enum DgrReplayCompression : byte
    {
        None = 0,
        Deflate = 1,
    }

    public class DgrReplayQueueOverflowException :
        InvalidOperationException
    {
        public DgrReplayQueueOverflowException(string message) :
            base(message)
        {
        }
    }

    public class DgrReplayTrackDescriptor
    {
        public readonly string TrackId;
        public readonly uint TrackVersion;
        public readonly string SchemaHash;

        public DgrReplayTrackDescriptor(
            string trackId,
            uint trackVersion,
            string schemaHash)
        {
            RuntimeReplayId.Validate(trackId, nameof(trackId));
            if (trackVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(trackVersion));
            }
            if (!RuntimeReplayHash.IsSha256Hex(schemaHash))
            {
                throw new ArgumentException(
                    "Track schema hash must be a SHA-256 hex string.",
                    nameof(schemaHash));
            }

            TrackId = trackId;
            TrackVersion = trackVersion;
            SchemaHash = schemaHash.ToLowerInvariant();
        }
    }

    public class DgrReplayFileHeader
    {
        private readonly byte[] _metadata;
        private readonly byte[] _metadataHash;

        public readonly Guid RecordingId;
        public readonly long CreatedUtcTicks;
        public readonly string RegistrySchemaHash;
        public readonly IReadOnlyList<DgrReplayTrackDescriptor> Tracks;

        public byte[] Metadata => (byte[])_metadata.Clone();
        public byte[] MetadataHash => (byte[])_metadataHash.Clone();

        public DgrReplayFileHeader(
            Guid recordingId,
            long createdUtcTicks,
            string registrySchemaHash,
            byte[] metadata,
            IEnumerable<DgrReplayTrackDescriptor> tracks)
        {
            if (recordingId == Guid.Empty)
            {
                throw new ArgumentException(
                    "Replay recording id cannot be empty.",
                    nameof(recordingId));
            }
            if (createdUtcTicks <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(createdUtcTicks));
            }
            if (!RuntimeReplayHash.IsSha256Hex(registrySchemaHash))
            {
                throw new ArgumentException(
                    "Replay registry schema hash must be a SHA-256 hex string.",
                    nameof(registrySchemaHash));
            }
            if (tracks == null)
            {
                throw new ArgumentNullException(nameof(tracks));
            }
            if (metadata == null)
            {
                throw new ArgumentNullException(nameof(metadata));
            }
            if (metadata.Length
                > DgrReplayFormat.MAX_METADATA_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(metadata),
                    $"Replay metadata cannot exceed {DgrReplayFormat.MAX_METADATA_BYTES} bytes.");
            }

            var ordered = tracks
                .Select(value =>
                    value
                    ?? throw new ArgumentException(
                        "Replay track descriptor cannot be null.",
                        nameof(tracks)))
                .OrderBy(value => value.TrackId, StringComparer.Ordinal)
                .ToArray();
            for (var i = 1; i < ordered.Length; i++)
            {
                if (string.Equals(
                        ordered[i - 1].TrackId,
                        ordered[i].TrackId,
                        StringComparison.Ordinal))
                {
                    throw new ArgumentException(
                        $"Replay track '{ordered[i].TrackId}' is duplicated.",
                        nameof(tracks));
                }
            }

            RecordingId = recordingId;
            CreatedUtcTicks = createdUtcTicks;
            RegistrySchemaHash =
                registrySchemaHash.ToLowerInvariant();
            _metadata = (byte[])metadata.Clone();
            _metadataHash =
                RuntimeReplayHash.CalculateSha256(_metadata);
            Tracks = Array.AsReadOnly(ordered);
        }

        public DgrReplayFileHeader(
            Guid recordingId,
            long createdUtcTicks,
            string registrySchemaHash,
            IEnumerable<DgrReplayTrackDescriptor> tracks) :
            this(
                recordingId,
                createdUtcTicks,
                registrySchemaHash,
                Array.Empty<byte>(),
                tracks)
        {
        }
    }

    public class DgrReplayChunkIndexEntry
    {
        public readonly ulong Sequence;
        public readonly int TrackIndex;
        public readonly long StartTick;
        public readonly long EndTick;
        public readonly ulong Cursor;
        public readonly long FileOffset;
        public readonly int RecordLength;
        public readonly DgrReplayCompression Compression;
        public readonly int UncompressedLength;
        public readonly int StoredLength;
        public readonly byte[] PayloadHash;

        public long CompletedTick => EndTick;

        public DgrReplayChunkIndexEntry(
            ulong sequence,
            int trackIndex,
            long startTick,
            long endTick,
            ulong cursor,
            long fileOffset,
            int recordLength,
            DgrReplayCompression compression,
            int uncompressedLength,
            int storedLength,
            byte[] payloadHash)
        {
            if (trackIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(trackIndex));
            }
            if (startTick < -1 || endTick < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startTick));
            }
            if ((startTick == -1) != (endTick == -1)
                || startTick > endTick)
            {
                throw new ArgumentException(
                    $"Replay tick range {startTick}..{endTick} is invalid.");
            }
            if (fileOffset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fileOffset));
            }
            if (recordLength <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(recordLength));
            }
            if (uncompressedLength < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(uncompressedLength));
            }
            if (storedLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(storedLength));
            }
            RuntimeReplayHash.RequireSha256(
                payloadHash,
                nameof(payloadHash));

            Sequence = sequence;
            TrackIndex = trackIndex;
            StartTick = startTick;
            EndTick = endTick;
            Cursor = cursor;
            FileOffset = fileOffset;
            RecordLength = recordLength;
            Compression = compression;
            UncompressedLength = uncompressedLength;
            StoredLength = storedLength;
            PayloadHash = (byte[])payloadHash.Clone();
        }

        public DgrReplayChunkIndexEntry(
            ulong sequence,
            int trackIndex,
            long completedTick,
            ulong cursor,
            long fileOffset,
            int recordLength,
            DgrReplayCompression compression,
            int uncompressedLength,
            int storedLength,
            byte[] payloadHash) :
            this(
                sequence,
                trackIndex,
                completedTick,
                completedTick,
                cursor,
                fileOffset,
                recordLength,
                compression,
                uncompressedLength,
                storedLength,
                payloadHash)
        {
        }
    }

    public class DgrReplayScanResult
    {
        public readonly DgrReplayFileHeader Header;
        public readonly IReadOnlyList<DgrReplayChunkIndexEntry> Chunks;
        public readonly long HeaderLength;
        public readonly long DataLength;
        public readonly long ValidLength;
        public readonly bool HasValidFooter;
        public readonly string Failure;

        public bool IsComplete => HasValidFooter
                                  && string.IsNullOrEmpty(Failure);

        public DgrReplayScanResult(
            DgrReplayFileHeader header,
            IReadOnlyList<DgrReplayChunkIndexEntry> chunks,
            long headerLength,
            long dataLength,
            long validLength,
            bool hasValidFooter,
            string failure)
        {
            Header = header
                     ?? throw new ArgumentNullException(nameof(header));
            if (chunks == null)
            {
                throw new ArgumentNullException(nameof(chunks));
            }
            if (headerLength <= 0
                || dataLength < headerLength
                || validLength < dataLength)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(validLength));
            }

            var copy = new DgrReplayChunkIndexEntry[chunks.Count];
            for (var i = 0; i < chunks.Count; i++)
            {
                copy[i] = chunks[i]
                          ?? throw new ArgumentException(
                              $"Replay chunk index entry {i} is null.",
                              nameof(chunks));
            }

            Chunks = Array.AsReadOnly(copy);
            HeaderLength = headerLength;
            DataLength = dataLength;
            ValidLength = validLength;
            HasValidFooter = hasValidFooter;
            Failure = failure;
        }
    }
}
