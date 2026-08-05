using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public class RuntimeReplayCheckpointWriter : IDisposable
    {
        public const int DEFAULT_MAX_BYTES = 64 * 1024 * 1024;

        private static readonly UTF8Encoding UTF8 = new(false, true);

        private readonly RuntimeReplayPagedStream _stream;
        private readonly BinaryWriter _writer;
        private readonly int _maxBytes;
        private bool _disposed;

        public int Length => checked((int)_stream.Length);

        public RuntimeReplayCheckpointWriter(
            int initialCapacity = 256,
            int maxBytes = DEFAULT_MAX_BYTES)
        {
            if (initialCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(initialCapacity));
            }
            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }
            if (initialCapacity > maxBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(initialCapacity),
                    "Initial capacity cannot exceed the writer limit.");
            }

            _maxBytes = maxBytes;
            _stream = new RuntimeReplayPagedStream(
                RuntimeReplayCheckpointCodec.PAGE_BYTES,
                maxBytes,
                initialCapacity);
            _writer = new BinaryWriter(_stream, UTF8, leaveOpen: true);
        }

        public void WriteByte(byte value)
        {
            EnsureWritable(sizeof(byte));
            _writer.Write(value);
        }

        public void WriteBoolean(bool value)
        {
            EnsureWritable(sizeof(byte));
            _writer.Write(value);
        }

        public void WriteInt32(int value)
        {
            EnsureWritable(sizeof(int));
            _writer.Write(value);
        }

        public void WriteUInt32(uint value)
        {
            EnsureWritable(sizeof(uint));
            _writer.Write(value);
        }

        public void WriteInt64(long value)
        {
            EnsureWritable(sizeof(long));
            _writer.Write(value);
        }

        public void WriteUInt64(ulong value)
        {
            EnsureWritable(sizeof(ulong));
            _writer.Write(value);
        }

        public void WriteSingle(float value)
        {
            EnsureWritable(sizeof(float));
            _writer.Write(value);
        }

        public void WriteDouble(double value)
        {
            EnsureWritable(sizeof(double));
            _writer.Write(value);
        }

        public void WriteGuid(Guid value)
        {
            WriteRawBytes(value.ToByteArray());
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteInt32(-1);
                return;
            }

            var bytes = UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            WriteRawBytes(bytes);
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null)
            {
                WriteInt32(-1);
                return;
            }

            WriteInt32(value.Length);
            WriteRawBytes(value);
        }

        /// <summary>
        /// Starts a length-prefixed payload that is written directly into this
        /// writer. Complete it with <see cref="EndLengthPrefixedBlock"/>.
        /// This avoids allocating one temporary byte array per component
        /// record while preserving the existing binary wire layout.
        /// </summary>
        public int BeginLengthPrefixedBlock()
        {
            var lengthOffset = Length;
            WriteInt32(0);
            return lengthOffset;
        }

        public void EndLengthPrefixedBlock(int lengthOffset)
        {
            ThrowIfDisposed();
            var endOffset = _stream.Position;
            if (lengthOffset < 0
                || lengthOffset > endOffset - sizeof(int))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(lengthOffset));
            }

            var payloadLength = checked(
                (int)(endOffset - lengthOffset - sizeof(int)));
            _stream.Position = lengthOffset;
            _writer.Write(payloadLength);
            _stream.Position = endOffset;
        }

        public void WriteRawBytes(byte[] value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            EnsureWritable(value.Length);
            _writer.Write(value);
        }

        public byte[] ToArray()
        {
            ThrowIfDisposed();
            _writer.Flush();
            return _stream.ToArray();
        }

        public IReadOnlyList<RuntimeReplayCheckpointPage> ToPages()
        {
            ThrowIfDisposed();
            _writer.Flush();
            return _stream.ToCheckpointPages();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
            _stream.Dispose();
        }

        private void EnsureWritable(int byteCount)
        {
            ThrowIfDisposed();
            if (byteCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteCount));
            }
            if (_stream.Length + byteCount > _maxBytes)
            {
                throw new InvalidOperationException(
                    $"Replay checkpoint payload exceeds the {_maxBytes}-byte writer limit.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RuntimeReplayCheckpointWriter));
            }
        }
    }

    public class RuntimeReplayCheckpointReader : IDisposable
    {
        public const int DEFAULT_MAX_STRING_BYTES = 1024 * 1024;
        public const int DEFAULT_MAX_BLOB_BYTES = 64 * 1024 * 1024;

        private static readonly UTF8Encoding UTF8 = new(false, true);

        private readonly RuntimeReplayPagedStream _stream;
        private readonly BinaryReader _reader;
        private bool _disposed;

        public int Position => checked((int)_stream.Position);
        public int Length => checked((int)_stream.Length);
        public int Remaining => checked((int)(_stream.Length - _stream.Position));

        public RuntimeReplayCheckpointReader(
            byte[] payload,
            int maxBytes = DEFAULT_MAX_BLOB_BYTES)
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
                throw new FormatException(
                    $"Replay checkpoint payload is {payload.Length} bytes; maximum is {maxBytes}.");
            }

            _stream = new RuntimeReplayPagedStream(
                RuntimeReplayCheckpointPageUtils.Split(payload),
                payload.Length);
            _reader = new BinaryReader(_stream, UTF8, leaveOpen: true);
        }

        public RuntimeReplayCheckpointReader(
            IReadOnlyList<RuntimeReplayCheckpointPage> pages,
            int payloadLength,
            int maxBytes = DEFAULT_MAX_BLOB_BYTES)
        {
            if (pages == null)
            {
                throw new ArgumentNullException(nameof(pages));
            }
            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }
            if (payloadLength < 0 || payloadLength > maxBytes)
            {
                throw new FormatException(
                    $"Replay checkpoint payload is {payloadLength} bytes; maximum is {maxBytes}.");
            }

            _stream = new RuntimeReplayPagedStream(pages, payloadLength);
            _reader = new BinaryReader(_stream, UTF8, leaveOpen: true);
        }

        public byte ReadByte()
        {
            RequireAvailable(sizeof(byte));
            return _reader.ReadByte();
        }

        public bool ReadBoolean()
        {
            RequireAvailable(sizeof(byte));
            return _reader.ReadBoolean();
        }

        public int ReadInt32()
        {
            RequireAvailable(sizeof(int));
            return _reader.ReadInt32();
        }

        public uint ReadUInt32()
        {
            RequireAvailable(sizeof(uint));
            return _reader.ReadUInt32();
        }

        public long ReadInt64()
        {
            RequireAvailable(sizeof(long));
            return _reader.ReadInt64();
        }

        public ulong ReadUInt64()
        {
            RequireAvailable(sizeof(ulong));
            return _reader.ReadUInt64();
        }

        public float ReadSingle()
        {
            RequireAvailable(sizeof(float));
            return _reader.ReadSingle();
        }

        public double ReadDouble()
        {
            RequireAvailable(sizeof(double));
            return _reader.ReadDouble();
        }

        public Guid ReadGuid()
        {
            return new Guid(ReadRawBytes(16));
        }

        public string ReadString(int maxByteCount = DEFAULT_MAX_STRING_BYTES)
        {
            var byteCount = ReadInt32();
            if (byteCount == -1)
            {
                return null;
            }
            ValidateLength(byteCount, maxByteCount, "string");
            return UTF8.GetString(ReadRawBytes(byteCount));
        }

        public byte[] ReadBytes(int maxByteCount = DEFAULT_MAX_BLOB_BYTES)
        {
            var byteCount = ReadInt32();
            if (byteCount == -1)
            {
                return null;
            }
            ValidateLength(byteCount, maxByteCount, "blob");
            return ReadRawBytes(byteCount);
        }

        public byte[] ReadRawBytes(int byteCount)
        {
            if (byteCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteCount));
            }

            RequireAvailable(byteCount);
            var result = _reader.ReadBytes(byteCount);
            if (result.Length != byteCount)
            {
                throw new EndOfStreamException(
                    $"Replay checkpoint expected {byteCount} bytes, received {result.Length}.");
            }
            return result;
        }

        public void RequireEnd()
        {
            ThrowIfDisposed();
            if (_stream.Position != _stream.Length)
            {
                throw new FormatException(
                    $"Replay checkpoint payload has {Remaining} unread bytes.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _reader.Dispose();
            _stream.Dispose();
        }

        private void RequireAvailable(int byteCount)
        {
            ThrowIfDisposed();
            if (byteCount < 0 || _stream.Length - _stream.Position < byteCount)
            {
                throw new EndOfStreamException(
                    $"Replay checkpoint requires {byteCount} bytes at offset {_stream.Position}, "
                    + $"but only {_stream.Length - _stream.Position} remain.");
            }
        }

        private static void ValidateLength(int byteCount, int maximum, string label)
        {
            if (maximum < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximum));
            }
            if (byteCount < 0 || byteCount > maximum)
            {
                throw new FormatException(
                    $"Replay checkpoint {label} length {byteCount} is outside 0..{maximum}.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RuntimeReplayCheckpointReader));
            }
        }
    }

    class RuntimeReplayPagedStream : Stream
    {
        private readonly List<byte[]> _pages;
        private readonly int _pageBytes;
        private readonly int _maxBytes;
        private readonly bool _writable;
        private long _length;
        private long _position;
        private bool _disposed;

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => !_disposed && _writable;
        public override long Length
        {
            get
            {
                ThrowIfDisposed();
                return _length;
            }
        }

        public override long Position
        {
            get
            {
                ThrowIfDisposed();
                return _position;
            }
            set
            {
                ThrowIfDisposed();
                if (value < 0 || value > _length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                _position = value;
            }
        }

        public RuntimeReplayPagedStream(
            int pageBytes,
            int maxBytes,
            int initialCapacity)
        {
            if (pageBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pageBytes));
            }
            if (maxBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }

            _pageBytes = pageBytes;
            _maxBytes = maxBytes;
            _writable = true;
            _pages = new List<byte[]>(Math.Max(
                1,
                (initialCapacity + pageBytes - 1) / pageBytes));
        }

        public RuntimeReplayPagedStream(
            IReadOnlyList<RuntimeReplayCheckpointPage> pages,
            int payloadLength)
        {
            if (pages == null)
            {
                throw new ArgumentNullException(nameof(pages));
            }
            if (pages.Count == 0)
            {
                throw new ArgumentException(
                    "A paged replay stream requires at least one page.",
                    nameof(pages));
            }
            if (payloadLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength));
            }

            _pageBytes = RuntimeReplayCheckpointCodec.PAGE_BYTES;
            _maxBytes = payloadLength;
            _writable = false;
            _length = payloadLength;
            _pages = new List<byte[]>(pages.Count);
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
                    && page.PayloadLength != _pageBytes)
                {
                    throw new ArgumentException(
                        $"Checkpoint page {i} is not a full intermediate page.",
                        nameof(pages));
                }
                actualLength = checked(actualLength + page.PayloadLength);
                _pages.Add(page.UnsafePayload);
            }
            if (actualLength != payloadLength)
            {
                throw new ArgumentException(
                    $"Checkpoint pages contain {actualLength} bytes, expected {payloadLength}.",
                    nameof(payloadLength));
            }
            var expectedPageCount = Math.Max(
                1,
                (payloadLength + _pageBytes - 1) / _pageBytes);
            if (pages.Count != expectedPageCount)
            {
                throw new ArgumentException(
                    $"Checkpoint payload requires {expectedPageCount} pages, received {pages.Count}.",
                    nameof(pages));
            }
        }

        public byte[] ToArray()
        {
            ThrowIfDisposed();
            var result = new byte[checked((int)_length)];
            var offset = 0;
            for (var i = 0; i < _pages.Count && offset < result.Length; i++)
            {
                var length = Math.Min(_pages[i].Length, result.Length - offset);
                if (length > 0)
                {
                    Buffer.BlockCopy(_pages[i], 0, result, offset, length);
                }
                offset += length;
            }
            return result;
        }

        public IReadOnlyList<RuntimeReplayCheckpointPage>
            ToCheckpointPages()
        {
            ThrowIfDisposed();
            var pageCount = Math.Max(
                1,
                (checked((int)_length) + _pageBytes - 1) / _pageBytes);
            var result = new RuntimeReplayCheckpointPage[pageCount];
            for (var i = 0; i < pageCount; i++)
            {
                var offset = i * _pageBytes;
                var length = Math.Min(
                    _pageBytes,
                    checked((int)_length) - offset);
                if (length < 0)
                {
                    length = 0;
                }
                var payload = new byte[length];
                if (length > 0)
                {
                    Buffer.BlockCopy(_pages[i], 0, payload, 0, length);
                }
                result[i] = new RuntimeReplayCheckpointPage(i, payload);
            }
            return Array.AsReadOnly(result);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            ValidateBuffer(buffer, offset, count);
            var remaining = (int)Math.Min(count, _length - _position);
            var read = remaining;
            while (remaining > 0)
            {
                var pageIndex = checked((int)(_position / _pageBytes));
                var pageOffset = checked((int)(_position % _pageBytes));
                var length = Math.Min(
                    remaining,
                    _pages[pageIndex].Length - pageOffset);
                if (length <= 0)
                {
                    break;
                }
                Buffer.BlockCopy(
                    _pages[pageIndex],
                    pageOffset,
                    buffer,
                    offset,
                    length);
                offset += length;
                remaining -= length;
                _position += length;
            }
            return read - remaining;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisposed();
            if (!_writable)
            {
                throw new NotSupportedException("Replay page stream is read-only.");
            }
            ValidateBuffer(buffer, offset, count);
            if (_position + count > _maxBytes)
            {
                throw new InvalidOperationException(
                    $"Replay checkpoint payload exceeds the {_maxBytes}-byte writer limit.");
            }

            var remaining = count;
            while (remaining > 0)
            {
                var pageIndex = checked((int)(_position / _pageBytes));
                var pageOffset = checked((int)(_position % _pageBytes));
                EnsurePage(pageIndex);
                var length = Math.Min(remaining, _pageBytes - pageOffset);
                Buffer.BlockCopy(
                    buffer,
                    offset,
                    _pages[pageIndex],
                    pageOffset,
                    length);
                offset += length;
                remaining -= length;
                _position += length;
            }
            _length = Math.Max(_length, _position);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ThrowIfDisposed();
            var next = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            Position = next;
            return _position;
        }

        public override void SetLength(long value)
        {
            ThrowIfDisposed();
            if (!_writable)
            {
                throw new NotSupportedException("Replay page stream is read-only.");
            }
            if (value < 0 || value > _maxBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (value > 0)
            {
                EnsurePage(checked((int)((value - 1) / _pageBytes)));
            }

            _length = value;
            if (_position > _length)
            {
                _position = _length;
            }
        }

        public override void Flush()
        {
            ThrowIfDisposed();
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        private void EnsurePage(int pageIndex)
        {
            while (_pages.Count <= pageIndex)
            {
                _pages.Add(new byte[_pageBytes]);
            }
        }

        private static void ValidateBuffer(
            byte[] buffer,
            int offset,
            int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0
                || count < 0
                || offset > buffer.Length - count)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(RuntimeReplayPagedStream));
            }
        }
    }

    public static class RuntimeReplayHash
    {
        public const int SHA256_BYTES = 32;
        public const int SHA256_HEX_CHARS = SHA256_BYTES * 2;

        public static byte[] CalculateSha256(byte[] payload)
        {
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            using var sha = SHA256.Create();
            return sha.ComputeHash(payload);
        }

        public static byte[] CalculateSha256(
            IReadOnlyList<RuntimeReplayCheckpointPage> pages,
            int payloadLength)
        {
            if (pages == null)
            {
                throw new ArgumentNullException(nameof(pages));
            }
            if (payloadLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength));
            }

            using var hash = IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
            var remaining = payloadLength;
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
                var payload = page.UnsafePayload;
                actualLength = checked(actualLength + payload.Length);
                var length = Math.Min(payload.Length, remaining);
                if (length > 0)
                {
                    hash.AppendData(payload, 0, length);
                }
                remaining -= length;
            }
            if (remaining != 0 || actualLength != payloadLength)
            {
                throw new ArgumentException(
                    $"Checkpoint pages contain {actualLength} bytes, expected {payloadLength}.",
                    nameof(pages));
            }
            return hash.GetHashAndReset();
        }

        public static byte[] CalculateSha256(Stream stream, long byteCount)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }
            if (!stream.CanRead)
            {
                throw new ArgumentException("Replay hash source must be readable.", nameof(stream));
            }
            if (byteCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(byteCount));
            }

            using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var remaining = byteCount;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = stream.Read(buffer, 0, requested);
                if (read <= 0)
                {
                    throw new EndOfStreamException(
                        $"Replay hash source ended with {remaining} bytes remaining.");
                }

                incremental.AppendData(buffer, 0, read);
                remaining -= read;
            }
            return incremental.GetHashAndReset();
        }

        public static string ToHex(byte[] hash)
        {
            RequireSha256(hash, nameof(hash));
            var builder = new StringBuilder(SHA256_HEX_CHARS);
            for (var i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }
            return builder.ToString();
        }

        public static bool IsSha256Hex(string value)
        {
            if (value == null || value.Length != SHA256_HEX_CHARS)
            {
                return false;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var valid = c >= '0' && c <= '9'
                            || c >= 'a' && c <= 'f'
                            || c >= 'A' && c <= 'F';
                if (!valid)
                {
                    return false;
                }
            }
            return true;
        }

        public static bool FixedTimeEquals(byte[] first, byte[] second)
        {
            if (first == null || second == null || first.Length != second.Length)
            {
                return false;
            }

            var difference = 0;
            for (var i = 0; i < first.Length; i++)
            {
                difference |= first[i] ^ second[i];
            }
            return difference == 0;
        }

        public static void RequireSha256(byte[] hash, string parameterName)
        {
            if (hash == null || hash.Length != SHA256_BYTES)
            {
                throw new ArgumentException(
                    $"SHA-256 value must contain exactly {SHA256_BYTES} bytes.",
                    parameterName);
            }
        }
    }
}
