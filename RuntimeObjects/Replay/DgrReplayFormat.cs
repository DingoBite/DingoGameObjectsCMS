using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public static class DgrReplayFormat
    {
        public const uint FILE_MAGIC = 0x31524744;
        public const uint CHUNK_MAGIC = 0x314b4843;
        public const uint FOOTER_MAGIC = 0x31584449;
        public const uint FOOTER_END_MAGIC = 0x31444e45;
        public const uint CONTAINER_VERSION = 1;
        public const uint CHUNK_VERSION = 1;
        public const uint FOOTER_VERSION = 1;

        public const int MAX_TRACKS = 4096;
        public const int MAX_HEADER_BYTES = 1024 * 1024;
        public const int MAX_METADATA_BYTES = 512 * 1024;
        public const int MAX_CHUNK_BYTES = 16 * 1024 * 1024;
        public const int MAX_STORED_CHUNK_BYTES =
            MAX_CHUNK_BYTES + 1024 * 1024;
        public const int MAX_CHUNKS = 500000;
        public const int MAX_FOOTER_BYTES = 64 * 1024 * 1024;

        private const int RECORD_PREFIX_BYTES = 12;
        private const int CHUNK_BODY_FIXED_BYTES =
            sizeof(ulong)
            + sizeof(int)
            + sizeof(long)
            + sizeof(long)
            + sizeof(ulong)
            + sizeof(byte)
            + sizeof(int)
            + sizeof(int)
            + RuntimeReplayHash.SHA256_BYTES;

        private static readonly UTF8Encoding UTF8 = new(false, true);

        public static uint ReadContainerVersion(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (!stream.CanSeek)
            {
                throw new ArgumentException(
                    "Container version probing requires a seekable stream.",
                    nameof(stream));
            }

            var position = stream.Position;
            try
            {
                using var reader =
                    new BinaryReader(stream, UTF8, leaveOpen: true);
                var magic = ReadUInt32(reader, "file magic");
                if (magic != FILE_MAGIC)
                {
                    throw new FormatException(
                        $"DGR file magic 0x{magic:x8} does not match "
                        + $"0x{FILE_MAGIC:x8}.");
                }
                return ReadUInt32(reader, "container version");
            }
            finally
            {
                stream.Position = position;
            }
        }

        public static long WriteHeader(
            Stream stream,
            DgrReplayFileHeader header)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }
            if (header.Tracks.Count > MAX_TRACKS)
            {
                throw new InvalidOperationException(
                    $"Replay header has {header.Tracks.Count} tracks; maximum is {MAX_TRACKS}.");
            }

            using var bodyWriter =
                new RuntimeReplayCheckpointWriter(
                    maxBytes: MAX_HEADER_BYTES);
            bodyWriter.WriteGuid(header.RecordingId);
            bodyWriter.WriteInt64(header.CreatedUtcTicks);
            bodyWriter.WriteString(header.RegistrySchemaHash);
            var metadata = header.Metadata;
            bodyWriter.WriteBytes(metadata);
            bodyWriter.WriteRawBytes(header.MetadataHash);
            bodyWriter.WriteInt32(header.Tracks.Count);
            for (var i = 0; i < header.Tracks.Count; i++)
            {
                var track = header.Tracks[i];
                bodyWriter.WriteString(track.TrackId);
                bodyWriter.WriteUInt32(track.TrackVersion);
                bodyWriter.WriteString(track.SchemaHash);
            }

            var body = bodyWriter.ToArray();
            var hash = RuntimeReplayHash.CalculateSha256(body);
            using var writer =
                new BinaryWriter(stream, UTF8, leaveOpen: true);
            writer.Write(FILE_MAGIC);
            writer.Write(CONTAINER_VERSION);
            writer.Write(body.Length);
            writer.Write(body);
            writer.Write(hash);
            writer.Flush();
            return stream.Position;
        }

        public static DgrReplayFileHeader ReadHeader(Stream stream)
        {
            using var reader =
                new BinaryReader(stream, UTF8, leaveOpen: true);
            var magic = ReadUInt32(reader, "file magic");
            if (magic != FILE_MAGIC)
            {
                throw new FormatException(
                    $"DGR file magic 0x{magic:x8} does not match 0x{FILE_MAGIC:x8}.");
            }
            var version = ReadUInt32(reader, "container version");
            if (version != CONTAINER_VERSION)
            {
                throw new FormatException(
                    $"DGR container version {version} is not supported.");
            }
            var bodyLength = ReadInt32(reader, "header length");
            if (bodyLength < 0 || bodyLength > MAX_HEADER_BYTES)
            {
                throw new FormatException(
                    $"DGR header length {bodyLength} is outside 0..{MAX_HEADER_BYTES}.");
            }

            var body = ReadExactly(reader, bodyLength, "header");
            var storedHash = ReadExactly(
                reader,
                RuntimeReplayHash.SHA256_BYTES,
                "header hash");
            var expectedHash =
                RuntimeReplayHash.CalculateSha256(body);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedHash,
                    storedHash))
            {
                throw new FormatException("DGR header hash mismatch.");
            }

            using var bodyReader =
                new RuntimeReplayCheckpointReader(
                    body,
                    MAX_HEADER_BYTES);
            var recordingId = bodyReader.ReadGuid();
            var createdUtcTicks = bodyReader.ReadInt64();
            var registrySchemaHash = bodyReader.ReadString(
                RuntimeReplayHash.SHA256_HEX_CHARS);
            var metadata =
                bodyReader.ReadBytes(MAX_METADATA_BYTES);
            var storedMetadataHash = bodyReader.ReadRawBytes(
                RuntimeReplayHash.SHA256_BYTES);
            var expectedMetadataHash =
                RuntimeReplayHash.CalculateSha256(metadata);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedMetadataHash,
                    storedMetadataHash))
            {
                throw new FormatException(
                    "DGR metadata hash mismatch.");
            }
            var trackCount = bodyReader.ReadInt32();
            if (trackCount < 0 || trackCount > MAX_TRACKS)
            {
                throw new FormatException(
                    $"DGR track count {trackCount} is outside 0..{MAX_TRACKS}.");
            }

            var tracks = new DgrReplayTrackDescriptor[trackCount];
            string previousTrackId = null;
            for (var i = 0; i < trackCount; i++)
            {
                var trackId =
                    bodyReader.ReadString(RuntimeReplayId.MAX_ID_CHARS);
                var trackVersion = bodyReader.ReadUInt32();
                var trackSchemaHash = bodyReader.ReadString(
                    RuntimeReplayHash.SHA256_HEX_CHARS);
                if (previousTrackId != null
                    && string.CompareOrdinal(
                        previousTrackId,
                        trackId) >= 0)
                {
                    throw new FormatException(
                        "DGR tracks must be unique and ordered by TrackId.");
                }

                tracks[i] = new DgrReplayTrackDescriptor(
                    trackId,
                    trackVersion,
                    trackSchemaHash);
                previousTrackId = trackId;
            }
            bodyReader.RequireEnd();

            return new DgrReplayFileHeader(
                recordingId,
                createdUtcTicks,
                registrySchemaHash,
                metadata,
                tracks);
        }

        public static DgrReplayChunkIndexEntry WriteChunk(
            Stream stream,
            ulong sequence,
            int trackIndex,
            long startTick,
            long endTick,
            ulong cursor,
            DgrReplayCompression compression,
            int uncompressedLength,
            byte[] storedPayload,
            byte[] payloadHash)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
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
            if (uncompressedLength < 0
                || uncompressedLength > MAX_CHUNK_BYTES)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(uncompressedLength));
            }
            if (storedPayload == null)
            {
                throw new ArgumentNullException(nameof(storedPayload));
            }
            if (storedPayload.Length > MAX_STORED_CHUNK_BYTES)
            {
                throw new InvalidOperationException(
                    $"Stored DGR chunk exceeds {MAX_STORED_CHUNK_BYTES} bytes.");
            }
            ValidateCompression(compression);
            RuntimeReplayHash.RequireSha256(
                payloadHash,
                nameof(payloadHash));

            using var bodyWriter =
                new RuntimeReplayCheckpointWriter(
                    initialCapacity:
                    Math.Min(
                        CHUNK_BODY_FIXED_BYTES + storedPayload.Length,
                        1024 * 1024),
                    maxBytes:
                    CHUNK_BODY_FIXED_BYTES
                    + MAX_STORED_CHUNK_BYTES);
            bodyWriter.WriteUInt64(sequence);
            bodyWriter.WriteInt32(trackIndex);
            bodyWriter.WriteInt64(startTick);
            bodyWriter.WriteInt64(endTick);
            bodyWriter.WriteUInt64(cursor);
            bodyWriter.WriteByte((byte)compression);
            bodyWriter.WriteInt32(uncompressedLength);
            bodyWriter.WriteInt32(storedPayload.Length);
            bodyWriter.WriteRawBytes(payloadHash);
            bodyWriter.WriteRawBytes(storedPayload);
            var body = bodyWriter.ToArray();
            var recordHash =
                RuntimeReplayHash.CalculateSha256(body);
            var recordLength = checked(
                RECORD_PREFIX_BYTES
                + body.Length
                + RuntimeReplayHash.SHA256_BYTES);
            var offset = stream.Position;

            using var writer =
                new BinaryWriter(stream, UTF8, leaveOpen: true);
            writer.Write(CHUNK_MAGIC);
            writer.Write(CHUNK_VERSION);
            writer.Write(recordLength);
            writer.Write(body);
            writer.Write(recordHash);
            writer.Flush();

            return new DgrReplayChunkIndexEntry(
                sequence,
                trackIndex,
                startTick,
                endTick,
                cursor,
                offset,
                recordLength,
                compression,
                uncompressedLength,
                storedPayload.Length,
                payloadHash);
        }

        public static DgrReplayChunkIndexEntry WriteChunk(
            Stream stream,
            ulong sequence,
            int trackIndex,
            long completedTick,
            ulong cursor,
            DgrReplayCompression compression,
            int uncompressedLength,
            byte[] storedPayload,
            byte[] payloadHash)
        {
            return WriteChunk(
                stream,
                sequence,
                trackIndex,
                completedTick,
                completedTick,
                cursor,
                compression,
                uncompressedLength,
                storedPayload,
                payloadHash);
        }

        public static DgrReplayChunkIndexEntry ReadChunk(
            Stream stream,
            DgrReplayFileHeader header,
            bool keepPayload,
            out byte[] payload)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (header == null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            var offset = stream.Position;
            using var reader =
                new BinaryReader(stream, UTF8, leaveOpen: true);
            var magic = ReadUInt32(reader, "chunk magic");
            if (magic != CHUNK_MAGIC)
            {
                throw new FormatException(
                    $"DGR chunk magic 0x{magic:x8} does not match 0x{CHUNK_MAGIC:x8}.");
            }
            var version = ReadUInt32(reader, "chunk version");
            if (version != CHUNK_VERSION)
            {
                throw new FormatException(
                    $"DGR chunk version {version} is not supported.");
            }
            var recordLength = ReadInt32(reader, "chunk record length");
            var maximumRecordLength = checked(
                RECORD_PREFIX_BYTES
                + CHUNK_BODY_FIXED_BYTES
                + MAX_STORED_CHUNK_BYTES
                + RuntimeReplayHash.SHA256_BYTES);
            var minimumRecordLength = checked(
                RECORD_PREFIX_BYTES
                + CHUNK_BODY_FIXED_BYTES
                + RuntimeReplayHash.SHA256_BYTES);
            if (recordLength < minimumRecordLength
                || recordLength > maximumRecordLength)
            {
                throw new FormatException(
                    $"DGR chunk record length {recordLength} is invalid.");
            }

            var bodyLength = checked(
                recordLength
                - RECORD_PREFIX_BYTES
                - RuntimeReplayHash.SHA256_BYTES);
            var body = ReadExactly(reader, bodyLength, "chunk body");
            var storedRecordHash = ReadExactly(
                reader,
                RuntimeReplayHash.SHA256_BYTES,
                "chunk record hash");
            var expectedRecordHash =
                RuntimeReplayHash.CalculateSha256(body);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedRecordHash,
                    storedRecordHash))
            {
                throw new FormatException(
                    $"DGR chunk at offset {offset} has a record hash mismatch.");
            }

            using var bodyReader =
                new RuntimeReplayCheckpointReader(
                    body,
                    body.Length);
            var sequence = bodyReader.ReadUInt64();
            var trackIndex = bodyReader.ReadInt32();
            var startTick = bodyReader.ReadInt64();
            var endTick = bodyReader.ReadInt64();
            var cursor = bodyReader.ReadUInt64();
            var compression =
                (DgrReplayCompression)bodyReader.ReadByte();
            var uncompressedLength = bodyReader.ReadInt32();
            var storedLength = bodyReader.ReadInt32();
            var payloadHash = bodyReader.ReadRawBytes(
                RuntimeReplayHash.SHA256_BYTES);

            if (trackIndex < 0
                || trackIndex >= header.Tracks.Count)
            {
                throw new FormatException(
                    $"DGR chunk track index {trackIndex} is outside the header track table.");
            }
            if (startTick < -1 || endTick < -1)
            {
                throw new FormatException(
                    $"DGR chunk tick range {startTick}..{endTick} is invalid.");
            }
            if ((startTick == -1) != (endTick == -1)
                || startTick > endTick)
            {
                throw new FormatException(
                    $"DGR chunk tick range {startTick}..{endTick} is invalid.");
            }
            ValidateCompression(compression);
            if (uncompressedLength < 0
                || uncompressedLength > MAX_CHUNK_BYTES)
            {
                throw new FormatException(
                    $"DGR chunk uncompressed length {uncompressedLength} is invalid.");
            }
            if (storedLength < 0
                || storedLength > MAX_STORED_CHUNK_BYTES)
            {
                throw new FormatException(
                    $"DGR chunk stored length {storedLength} is invalid.");
            }
            if (bodyReader.Remaining != storedLength)
            {
                throw new FormatException(
                    $"DGR chunk declares {storedLength} stored bytes, "
                    + $"but contains {bodyReader.Remaining}.");
            }
            var storedPayload =
                bodyReader.ReadRawBytes(storedLength);
            bodyReader.RequireEnd();

            var decodedPayload = DecodePayload(
                compression,
                storedPayload,
                uncompressedLength);
            var expectedPayloadHash =
                RuntimeReplayHash.CalculateSha256(decodedPayload);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedPayloadHash,
                    payloadHash))
            {
                throw new FormatException(
                    $"DGR chunk {sequence} payload hash mismatch.");
            }

            payload = keepPayload ? decodedPayload : null;
            return new DgrReplayChunkIndexEntry(
                sequence,
                trackIndex,
                startTick,
                endTick,
                cursor,
                offset,
                recordLength,
                compression,
                uncompressedLength,
                storedLength,
                payloadHash);
        }

        public static byte[] EncodeDeflate(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            if (payload.Length > MAX_CHUNK_BYTES)
            {
                throw new ArgumentOutOfRangeException(nameof(payload));
            }

            using var output = new MemoryStream();
            using (var deflate = new DeflateStream(
                       output,
                       CompressionLevel.Optimal,
                       leaveOpen: true))
            {
                deflate.Write(payload, 0, payload.Length);
            }
            var result = output.ToArray();
            if (result.Length > MAX_STORED_CHUNK_BYTES)
            {
                throw new InvalidOperationException(
                    "Deflate output exceeds the DGR stored-chunk limit.");
            }
            return result;
        }

        public static long WriteFooter(
            Stream stream,
            IReadOnlyList<DgrReplayChunkIndexEntry> entries,
            long dataLength)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }
            if (entries.Count > MAX_CHUNKS)
            {
                throw new InvalidOperationException(
                    $"DGR index has {entries.Count} chunks; maximum is {MAX_CHUNKS}.");
            }
            if (dataLength <= 0 || stream.Length < dataLength)
            {
                throw new ArgumentOutOfRangeException(nameof(dataLength));
            }

            stream.Flush();
            stream.Position = 0;
            var prefixHash =
                RuntimeReplayHash.CalculateSha256(stream, dataLength);
            stream.Position = dataLength;

            var index = EncodeIndex(entries);
            var indexHash =
                RuntimeReplayHash.CalculateSha256(index);
            using var bodyWriter =
                new RuntimeReplayCheckpointWriter(
                    maxBytes: MAX_FOOTER_BYTES);
            bodyWriter.WriteInt64(dataLength);
            bodyWriter.WriteInt32(index.Length);
            bodyWriter.WriteRawBytes(index);
            bodyWriter.WriteRawBytes(indexHash);
            bodyWriter.WriteRawBytes(prefixHash);
            var body = bodyWriter.ToArray();
            var footerHash =
                RuntimeReplayHash.CalculateSha256(body);
            var recordLength = checked(
                RECORD_PREFIX_BYTES
                + body.Length
                + RuntimeReplayHash.SHA256_BYTES
                + sizeof(uint));

            using var writer =
                new BinaryWriter(stream, UTF8, leaveOpen: true);
            writer.Write(FOOTER_MAGIC);
            writer.Write(FOOTER_VERSION);
            writer.Write(recordLength);
            writer.Write(body);
            writer.Write(footerHash);
            writer.Write(FOOTER_END_MAGIC);
            writer.Flush();
            return stream.Position;
        }

        public static IReadOnlyList<DgrReplayChunkIndexEntry>
            ReadFooter(
                Stream stream,
                long footerOffset,
                IReadOnlyList<DgrReplayChunkIndexEntry> scannedEntries)
        {
            using var reader =
                new BinaryReader(stream, UTF8, leaveOpen: true);
            var magic = ReadUInt32(reader, "footer magic");
            if (magic != FOOTER_MAGIC)
            {
                throw new FormatException(
                    $"DGR footer magic 0x{magic:x8} does not match 0x{FOOTER_MAGIC:x8}.");
            }
            var version = ReadUInt32(reader, "footer version");
            if (version != FOOTER_VERSION)
            {
                throw new FormatException(
                    $"DGR footer version {version} is not supported.");
            }
            var recordLength =
                ReadInt32(reader, "footer record length");
            var minimumLength = checked(
                RECORD_PREFIX_BYTES
                + sizeof(long)
                + sizeof(int)
                + RuntimeReplayHash.SHA256_BYTES * 3
                + sizeof(uint));
            if (recordLength < minimumLength
                || recordLength > MAX_FOOTER_BYTES)
            {
                throw new FormatException(
                    $"DGR footer record length {recordLength} is invalid.");
            }

            var bodyLength = checked(
                recordLength
                - RECORD_PREFIX_BYTES
                - RuntimeReplayHash.SHA256_BYTES
                - sizeof(uint));
            var body = ReadExactly(reader, bodyLength, "footer body");
            var storedFooterHash = ReadExactly(
                reader,
                RuntimeReplayHash.SHA256_BYTES,
                "footer hash");
            var endMagic = ReadUInt32(reader, "footer end magic");
            if (endMagic != FOOTER_END_MAGIC)
            {
                throw new FormatException(
                    "DGR footer end marker is invalid.");
            }
            var expectedFooterHash =
                RuntimeReplayHash.CalculateSha256(body);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedFooterHash,
                    storedFooterHash))
            {
                throw new FormatException(
                    "DGR footer hash mismatch.");
            }

            using var bodyReader =
                new RuntimeReplayCheckpointReader(
                    body,
                    MAX_FOOTER_BYTES);
            var dataLength = bodyReader.ReadInt64();
            var indexLength = bodyReader.ReadInt32();
            if (indexLength < 0
                || indexLength > MAX_FOOTER_BYTES)
            {
                throw new FormatException(
                    $"DGR index length {indexLength} is invalid.");
            }
            var index = bodyReader.ReadRawBytes(indexLength);
            var storedIndexHash = bodyReader.ReadRawBytes(
                RuntimeReplayHash.SHA256_BYTES);
            var storedPrefixHash = bodyReader.ReadRawBytes(
                RuntimeReplayHash.SHA256_BYTES);
            bodyReader.RequireEnd();

            if (dataLength != footerOffset)
            {
                throw new FormatException(
                    $"DGR footer points to data length {dataLength}, "
                    + $"but begins at {footerOffset}.");
            }
            var expectedIndexHash =
                RuntimeReplayHash.CalculateSha256(index);
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedIndexHash,
                    storedIndexHash))
            {
                throw new FormatException("DGR footer index hash mismatch.");
            }

            var endPosition = stream.Position;
            stream.Position = 0;
            var expectedPrefixHash =
                RuntimeReplayHash.CalculateSha256(stream, dataLength);
            stream.Position = endPosition;
            if (!RuntimeReplayHash.FixedTimeEquals(
                    expectedPrefixHash,
                    storedPrefixHash))
            {
                throw new FormatException(
                    "DGR data-prefix hash mismatch.");
            }

            var indexedEntries = DecodeIndex(index);
            RequireSameIndex(scannedEntries, indexedEntries);
            return indexedEntries;
        }

        public static bool IndexEntriesEqual(
            DgrReplayChunkIndexEntry first,
            DgrReplayChunkIndexEntry second)
        {
            return first.Sequence == second.Sequence
                   && first.TrackIndex == second.TrackIndex
                   && first.StartTick == second.StartTick
                   && first.EndTick == second.EndTick
                   && first.Cursor == second.Cursor
                   && first.FileOffset == second.FileOffset
                   && first.RecordLength == second.RecordLength
                   && first.Compression == second.Compression
                   && first.UncompressedLength
                   == second.UncompressedLength
                   && first.StoredLength == second.StoredLength
                   && RuntimeReplayHash.FixedTimeEquals(
                       first.PayloadHash,
                       second.PayloadHash);
        }

        private static byte[] EncodeIndex(
            IReadOnlyList<DgrReplayChunkIndexEntry> entries)
        {
            using var writer =
                new RuntimeReplayCheckpointWriter(
                    maxBytes: MAX_FOOTER_BYTES);
            writer.WriteInt32(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i]
                            ?? throw new InvalidOperationException(
                                $"DGR index entry {i} is null.");
                writer.WriteUInt64(entry.Sequence);
                writer.WriteInt32(entry.TrackIndex);
                writer.WriteInt64(entry.StartTick);
                writer.WriteInt64(entry.EndTick);
                writer.WriteUInt64(entry.Cursor);
                writer.WriteInt64(entry.FileOffset);
                writer.WriteInt32(entry.RecordLength);
                writer.WriteByte((byte)entry.Compression);
                writer.WriteInt32(entry.UncompressedLength);
                writer.WriteInt32(entry.StoredLength);
                writer.WriteRawBytes(entry.PayloadHash);
            }
            return writer.ToArray();
        }

        private static DgrReplayChunkIndexEntry[] DecodeIndex(
            byte[] index)
        {
            using var reader =
                new RuntimeReplayCheckpointReader(
                    index,
                    MAX_FOOTER_BYTES);
            var count = reader.ReadInt32();
            if (count < 0 || count > MAX_CHUNKS)
            {
                throw new FormatException(
                    $"DGR index count {count} is outside 0..{MAX_CHUNKS}.");
            }

            var result = new DgrReplayChunkIndexEntry[count];
            for (var i = 0; i < count; i++)
            {
                var sequence = reader.ReadUInt64();
                var trackIndex = reader.ReadInt32();
                var startTick = reader.ReadInt64();
                var endTick = reader.ReadInt64();
                var cursor = reader.ReadUInt64();
                var fileOffset = reader.ReadInt64();
                var recordLength = reader.ReadInt32();
                var compression =
                    (DgrReplayCompression)reader.ReadByte();
                ValidateCompression(compression);
                var uncompressedLength = reader.ReadInt32();
                var storedLength = reader.ReadInt32();
                var payloadHash = reader.ReadRawBytes(
                    RuntimeReplayHash.SHA256_BYTES);
                result[i] = new DgrReplayChunkIndexEntry(
                    sequence,
                    trackIndex,
                    startTick,
                    endTick,
                    cursor,
                    fileOffset,
                    recordLength,
                    compression,
                    uncompressedLength,
                    storedLength,
                    payloadHash);
            }
            reader.RequireEnd();
            return result;
        }

        private static void RequireSameIndex(
            IReadOnlyList<DgrReplayChunkIndexEntry> scanned,
            IReadOnlyList<DgrReplayChunkIndexEntry> indexed)
        {
            if (scanned.Count != indexed.Count)
            {
                throw new FormatException(
                    $"DGR footer indexes {indexed.Count} chunks, "
                    + $"but the data prefix contains {scanned.Count}.");
            }
            for (var i = 0; i < scanned.Count; i++)
            {
                if (!IndexEntriesEqual(scanned[i], indexed[i]))
                {
                    throw new FormatException(
                        $"DGR footer index entry {i} does not match its chunk.");
                }
            }
        }

        private static byte[] DecodePayload(
            DgrReplayCompression compression,
            byte[] storedPayload,
            int uncompressedLength)
        {
            if (compression == DgrReplayCompression.None)
            {
                if (storedPayload.Length != uncompressedLength)
                {
                    throw new FormatException(
                        "Uncompressed DGR chunk length does not match its stored length.");
                }
                return (byte[])storedPayload.Clone();
            }

            var result = new byte[uncompressedLength];
            using var input =
                new MemoryStream(storedPayload, writable: false);
            using var deflate =
                new DeflateStream(
                    input,
                    CompressionMode.Decompress,
                    leaveOpen: false);
            var offset = 0;
            while (offset < result.Length)
            {
                var read = deflate.Read(
                    result,
                    offset,
                    result.Length - offset);
                if (read <= 0)
                {
                    throw new FormatException(
                        $"Deflate DGR chunk ended at {offset} of {result.Length} bytes.");
                }
                offset += read;
            }
            if (deflate.ReadByte() != -1)
            {
                throw new FormatException(
                    "Deflate DGR chunk expands beyond its declared length.");
            }
            return result;
        }

        private static void ValidateCompression(
            DgrReplayCompression compression)
        {
            if (compression != DgrReplayCompression.None
                && compression != DgrReplayCompression.Deflate)
            {
                throw new FormatException(
                    $"DGR compression value {(byte)compression} is not supported.");
            }
        }

        private static uint ReadUInt32(
            BinaryReader reader,
            string label)
        {
            return BitConverter.ToUInt32(
                ReadExactly(reader, sizeof(uint), label),
                0);
        }

        private static int ReadInt32(
            BinaryReader reader,
            string label)
        {
            return BitConverter.ToInt32(
                ReadExactly(reader, sizeof(int), label),
                0);
        }

        private static byte[] ReadExactly(
            BinaryReader reader,
            int length,
            string label)
        {
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length)
            {
                throw new EndOfStreamException(
                    $"DGR {label} expected {length} bytes, received {bytes.Length}.");
            }
            return bytes;
        }
    }

    public static class DgrReplayFileScanner
    {
        public static DgrReplayScanResult Scan(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "Replay path is required.",
                    nameof(path));
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            var header = DgrReplayFormat.ReadHeader(stream);
            var headerLength = stream.Position;
            var dataLength = headerLength;
            var validLength = headerLength;
            var chunks = new List<DgrReplayChunkIndexEntry>();
            var hasValidFooter = false;
            string failure = null;

            while (stream.Position < stream.Length)
            {
                var offset = stream.Position;
                if (stream.Length - offset < sizeof(uint))
                {
                    failure =
                        $"DGR record at offset {offset} has a truncated magic.";
                    break;
                }

                uint magic;
                using (var reader = new BinaryReader(
                           stream,
                           Encoding.UTF8,
                           leaveOpen: true))
                {
                    magic = reader.ReadUInt32();
                }
                stream.Position = offset;

                if (magic == DgrReplayFormat.CHUNK_MAGIC)
                {
                    try
                    {
                        var entry = DgrReplayFormat.ReadChunk(
                            stream,
                            header,
                            keepPayload: false,
                            out _);
                        if (entry.Sequence != (ulong)chunks.Count)
                        {
                            throw new FormatException(
                                $"DGR chunk sequence {entry.Sequence} appears at index {chunks.Count}.");
                        }
                        chunks.Add(entry);
                        dataLength = stream.Position;
                        validLength = dataLength;
                    }
                    catch (Exception exception)
                        when (exception is FormatException
                              || exception is EndOfStreamException
                              || exception is InvalidDataException
                              || exception is ArgumentException
                              || exception is OverflowException)
                    {
                        stream.Position = offset;
                        failure =
                            $"DGR chunk at offset {offset} is invalid: {exception.Message}";
                        break;
                    }
                    continue;
                }

                if (magic == DgrReplayFormat.FOOTER_MAGIC)
                {
                    try
                    {
                        DgrReplayFormat.ReadFooter(
                            stream,
                            offset,
                            chunks);
                        hasValidFooter = true;
                        validLength = stream.Position;
                        if (stream.Position != stream.Length)
                        {
                            failure =
                                $"DGR file has {stream.Length - stream.Position} trailing bytes after its footer.";
                        }
                    }
                    catch (Exception exception)
                        when (exception is FormatException
                              || exception is EndOfStreamException
                              || exception is ArgumentException
                              || exception is OverflowException)
                    {
                        stream.Position = offset;
                        failure =
                            $"DGR footer at offset {offset} is invalid: {exception.Message}";
                    }
                    break;
                }

                failure =
                    $"DGR record at offset {offset} has unknown magic 0x{magic:x8}.";
                break;
            }

            return new DgrReplayScanResult(
                header,
                chunks,
                headerLength,
                dataLength,
                validLength,
                hasValidFooter,
                failure);
        }
    }
}
