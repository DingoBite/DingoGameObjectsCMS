using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public class RuntimeReplayCheckpointWriter : IDisposable
    {
        public const int DEFAULT_MAX_BYTES = 64 * 1024 * 1024;

        private static readonly UTF8Encoding UTF8 = new(false, true);

        private readonly MemoryStream _stream;
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
            _stream = new MemoryStream(initialCapacity);
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

        private readonly MemoryStream _stream;
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

            _stream = new MemoryStream(payload, writable: false);
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
