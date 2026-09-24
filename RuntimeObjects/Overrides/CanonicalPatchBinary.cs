using System;
using System.Buffers.Binary;
using System.Text;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public class CanonicalPatchBinaryWriter : IDisposable
    {
        private static readonly UTF8Encoding UTF8 = new(false, true);

        private NativeList<byte> _buffer;

        public int Length => _buffer.Length;

        public CanonicalPatchBinaryWriter(int capacity = 128, Allocator allocator = Allocator.Temp)
        {
            _buffer = new NativeList<byte>(capacity, allocator);
        }

        public void WriteByte(byte value)
        {
            _buffer.Add(value);
        }

        public void WriteBoolean(bool value)
        {
            WriteByte(value ? (byte)1 : (byte)0);
        }

        public void WriteInt32(int value)
        {
            WriteUInt32(unchecked((uint)value));
        }

        public void WriteUInt32(uint value)
        {
            var start = _buffer.Length;
            _buffer.ResizeUninitialized(start + sizeof(uint));
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan().Slice(start, sizeof(uint)), value);
        }

        public void WriteInt64(long value)
        {
            WriteUInt64(unchecked((ulong)value));
        }

        public void WriteUInt64(ulong value)
        {
            var start = _buffer.Length;
            _buffer.ResizeUninitialized(start + sizeof(ulong));
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan().Slice(start, sizeof(ulong)), value);
        }

        public void WriteSingle(float value)
        {
            var bits = value == 0f
                ? 0
                : float.IsNaN(value)
                    ? unchecked((int)0x7fc00000)
                    : BitConverter.SingleToInt32Bits(value);
            WriteInt32(bits);
        }

        public void WriteDouble(double value)
        {
            var bits = value == 0d
                ? 0L
                : double.IsNaN(value)
                    ? unchecked((long)0x7ff8000000000000)
                    : BitConverter.DoubleToInt64Bits(value);
            WriteInt64(bits);
        }

        public void WriteString(string value)
        {
            if (value == null)
            {
                WriteInt32(-1);
                return;
            }

            var byteCount = UTF8.GetByteCount(value);
            WriteInt32(byteCount);
            var start = _buffer.Length;
            _buffer.ResizeUninitialized(start + byteCount);
            UTF8.GetBytes(value.AsSpan(), _buffer.AsSpan().Slice(start, byteCount));
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

        public void WriteBytes(NativeArray<byte> value)
        {
            WriteInt32(value.Length);
            WriteRawBytes(value.AsReadOnlySpan());
        }

        public void WriteHash128(Hash128 value)
        {
            WriteString(value.ToString());
        }

        public byte[] ToArray()
        {
            var result = new byte[_buffer.Length];
            _buffer.AsReadOnlySpan().CopyTo(result);
            return result;
        }

        public NativeArray<byte> ToNativeArray(Allocator allocator)
        {
            var result = new NativeArray<byte>(_buffer.Length, allocator);
            _buffer.AsReadOnlySpan().CopyTo(result.AsSpan());
            return result;
        }

        // The view is valid only until this writer grows or is disposed.
        public ReadOnlySpan<byte> AsReadOnlySpan() => _buffer.AsReadOnlySpan();

        // The view is valid only until this writer grows or is disposed. Do not dispose it.
        public NativeArray<byte> AsArray() => _buffer.AsArray();

        public int BeginLengthPrefixedBlock()
        {
            var offset = Length;
            WriteInt32(0);
            return offset;
        }

        public void EndLengthPrefixedBlock(int offset)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan().Slice(offset, sizeof(int)), Length - offset - sizeof(int));
        }

        public void WriteRawBytes(ReadOnlySpan<byte> value) => _buffer.AddRange(value);

        public void Dispose()
        {
            if (_buffer.IsCreated)
            {
                _buffer.Dispose();
            }
        }
    }

    public class CanonicalPatchBinaryReader
    {
        private static readonly UTF8Encoding UTF8 = new(false, true);

        private readonly ReadOnlyMemory<byte> _managedBuffer;
        private readonly NativeArray<byte> _nativeBuffer;
        private readonly bool _isNative;
        private int _position;

        public int Position => _position;
        public int Length => _isNative ? _nativeBuffer.Length : _managedBuffer.Length;
        public bool IsAtEnd => _position == Length;

        public CanonicalPatchBinaryReader(byte[] buffer)
        {
            _managedBuffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        public CanonicalPatchBinaryReader(ReadOnlyMemory<byte> buffer)
        {
            _managedBuffer = buffer;
        }

        // The input must remain allocated and unchanged for this reader's lifetime.
        public CanonicalPatchBinaryReader(NativeArray<byte> buffer)
        {
            _nativeBuffer = buffer;
            _isNative = true;
        }

        public byte ReadByte()
        {
            Require(1);
            return ReadSpan(1)[0];
        }

        public bool ReadBoolean()
        {
            var value = ReadByte();
            if (value > 1)
                throw new FormatException($"Invalid canonical boolean value {value} at offset {_position - 1}.");
            return value == 1;
        }

        public int ReadInt32()
        {
            return unchecked((int)ReadUInt32());
        }

        public uint ReadUInt32()
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(ReadSpan(sizeof(uint)));
        }

        public long ReadInt64()
        {
            return unchecked((long)ReadUInt64());
        }

        public ulong ReadUInt64()
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(ReadSpan(sizeof(ulong)));
        }

        public float ReadSingle()
        {
            return BitConverter.Int32BitsToSingle(ReadInt32());
        }

        public double ReadDouble()
        {
            return BitConverter.Int64BitsToDouble(ReadInt64());
        }

        public string ReadString()
        {
            var length = ReadLength();
            if (length < 0)
                return null;
            return UTF8.GetString(ReadSpan(length));
        }

        public byte[] ReadBytes()
        {
            var length = ReadLength();
            if (length < 0)
                return null;
            var result = ReadSpan(length).ToArray();
            return result;
        }

        public byte[] ReadBytes(int maxLength, string label)
        {
            if (maxLength < 0)
                throw new ArgumentOutOfRangeException(nameof(maxLength));
            var length = ReadLength();
            if (length < 0)
                return null;
            if (length > maxLength)
            {
                throw new FormatException(
                    $"Canonical {label ?? "byte payload"} length {length} exceeds maximum {maxLength}.");
            }
            var result = ReadSpan(length).ToArray();
            return result;
        }

        public bool TryReadBytesSpan(int maxLength, string label, out ReadOnlySpan<byte> value)
        {
            value = default;
            if (maxLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength));
            }
            var length = ReadLength();
            if (length < 0)
            {
                return false;
            }
            if (length > maxLength)
            {
                throw new FormatException($"Canonical {label ?? "byte payload"} length {length} exceeds maximum {maxLength}.");
            }
            value = ReadSpan(length);
            return true;
        }

        // The view borrows the managed input. Native input is copied because ReadOnlyMemory cannot refer to NativeArray.
        public bool TryReadBytesMemory(out ReadOnlyMemory<byte> value)
        {
            value = default;
            var length = ReadLength();
            if (length < 0)
            {
                return false;
            }
            Require(length);
            if (_isNative)
            {
                value = ReadSpan(length).ToArray();
            }
            else
            {
                value = _managedBuffer.Slice(_position, length);
                _position += length;
            }
            return true;
        }

        public CanonicalPatchBinaryReader ReadBytesReader(int maxLength, string label)
        {
            if (maxLength < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLength));
            }
            var length = ReadLength();
            if (length < 0)
            {
                return null;
            }
            if (length > maxLength)
            {
                throw new FormatException($"Canonical {label ?? "byte payload"} length {length} exceeds maximum {maxLength}.");
            }
            Require(length);
            var result = _isNative ? new CanonicalPatchBinaryReader(_nativeBuffer.GetSubArray(_position, length)) : new CanonicalPatchBinaryReader(_managedBuffer.Slice(_position, length));
            _position += length;
            return result;
        }

        public Hash128 ReadHash128()
        {
            var value = ReadString();
            if (value == null || value.Length != 32)
            {
                throw new FormatException(
                    "Canonical Hash128 must be exactly 32 lowercase hexadecimal characters.");
            }
            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                if ((character < '0' || character > '9')
                    && (character < 'a' || character > 'f'))
                {
                    throw new FormatException(
                        "Canonical Hash128 must be exactly 32 lowercase hexadecimal characters.");
                }
            }

            var result = Hash128.Parse(value);
            if (!string.Equals(result.ToString(), value, StringComparison.Ordinal))
            {
                throw new FormatException(
                    "Canonical Hash128 does not round-trip to its source representation.");
            }
            return result;
        }

        public void RequireEnd()
        {
            if (!IsAtEnd)
                throw new FormatException($"Canonical payload has {Length - _position} trailing bytes.");
        }

        private int ReadLength()
        {
            var length = ReadInt32();
            if (length < -1)
                throw new FormatException($"Invalid canonical payload length {length} at offset {_position - 4}.");
            return length;
        }

        private void Require(int count)
        {
            if (count < 0 || _position > Length - count)
                throw new FormatException($"Canonical payload ended at offset {_position}; {count} more bytes were required.");
        }

        private ReadOnlySpan<byte> ReadSpan(int count)
        {
            Require(count);
            var result = _isNative ? _nativeBuffer.AsReadOnlySpan().Slice(_position, count) : _managedBuffer.Span.Slice(_position, count);
            _position += count;
            return result;
        }
    }
}
