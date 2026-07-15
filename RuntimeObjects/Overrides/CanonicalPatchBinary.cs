using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public class CanonicalPatchBinaryWriter
    {
        private static readonly UTF8Encoding UTF8 = new(false, true);

        private readonly List<byte> _buffer;

        public int Length => _buffer.Count;

        public CanonicalPatchBinaryWriter(int capacity = 128)
        {
            _buffer = new List<byte>(capacity);
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
            _buffer.Add((byte)value);
            _buffer.Add((byte)(value >> 8));
            _buffer.Add((byte)(value >> 16));
            _buffer.Add((byte)(value >> 24));
        }

        public void WriteInt64(long value)
        {
            WriteUInt64(unchecked((ulong)value));
        }

        public void WriteUInt64(ulong value)
        {
            _buffer.Add((byte)value);
            _buffer.Add((byte)(value >> 8));
            _buffer.Add((byte)(value >> 16));
            _buffer.Add((byte)(value >> 24));
            _buffer.Add((byte)(value >> 32));
            _buffer.Add((byte)(value >> 40));
            _buffer.Add((byte)(value >> 48));
            _buffer.Add((byte)(value >> 56));
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

        public void WriteHash128(Hash128 value)
        {
            WriteString(value.ToString());
        }

        public byte[] ToArray()
        {
            return _buffer.ToArray();
        }

        private void WriteRawBytes(byte[] value)
        {
            for (var i = 0; i < value.Length; i++)
            {
                _buffer.Add(value[i]);
            }
        }
    }

    public class CanonicalPatchBinaryReader
    {
        private static readonly UTF8Encoding UTF8 = new(false, true);

        private readonly byte[] _buffer;
        private int _position;

        public int Position => _position;
        public int Length => _buffer.Length;
        public bool IsAtEnd => _position == _buffer.Length;

        public CanonicalPatchBinaryReader(byte[] buffer)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        }

        public byte ReadByte()
        {
            Require(1);
            return _buffer[_position++];
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
            Require(4);
            var value = (uint)_buffer[_position]
                        | ((uint)_buffer[_position + 1] << 8)
                        | ((uint)_buffer[_position + 2] << 16)
                        | ((uint)_buffer[_position + 3] << 24);
            _position += 4;
            return value;
        }

        public long ReadInt64()
        {
            return unchecked((long)ReadUInt64());
        }

        public ulong ReadUInt64()
        {
            Require(8);
            var value = (ulong)_buffer[_position]
                        | ((ulong)_buffer[_position + 1] << 8)
                        | ((ulong)_buffer[_position + 2] << 16)
                        | ((ulong)_buffer[_position + 3] << 24)
                        | ((ulong)_buffer[_position + 4] << 32)
                        | ((ulong)_buffer[_position + 5] << 40)
                        | ((ulong)_buffer[_position + 6] << 48)
                        | ((ulong)_buffer[_position + 7] << 56);
            _position += 8;
            return value;
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
            Require(length);
            var result = UTF8.GetString(_buffer, _position, length);
            _position += length;
            return result;
        }

        public byte[] ReadBytes()
        {
            var length = ReadLength();
            if (length < 0)
                return null;
            Require(length);
            var result = new byte[length];
            Buffer.BlockCopy(_buffer, _position, result, 0, length);
            _position += length;
            return result;
        }

        public Hash128 ReadHash128()
        {
            var value = ReadString();
            if (string.IsNullOrWhiteSpace(value))
                throw new FormatException("Canonical Hash128 value is empty.");
            return Hash128.Parse(value);
        }

        public void RequireEnd()
        {
            if (!IsAtEnd)
                throw new FormatException($"Canonical payload has {_buffer.Length - _position} trailing bytes.");
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
            if (count < 0 || _position > _buffer.Length - count)
                throw new FormatException($"Canonical payload ended at offset {_position}; {count} more bytes were required.");
        }
    }
}
