#if MIRROR
using System.IO;
using Unity.Collections;

namespace Mirror
{
    public static class NetworkWriterFixedStringExtensions
    {
        [WeaverPriority]
        public static void WriteFixedString32Bytes(this NetworkWriter writer, FixedString32Bytes value)
        {
            var length = value.Length;
            writer.WriteByte((byte)length);
            for (var i = 0; i < length; i++)
            {
                writer.WriteByte(value[i]);
            }
        }

        [WeaverPriority]
        public static void WriteFixedString64Bytes(this NetworkWriter writer, FixedString64Bytes value)
        {
            var length = value.Length;
            writer.WriteByte((byte)length);
            for (var i = 0; i < length; i++)
            {
                writer.WriteByte(value[i]);
            }
        }

        [WeaverPriority]
        public static void WriteFixedString128Bytes(this NetworkWriter writer, FixedString128Bytes value)
        {
            var length = value.Length;
            writer.WriteByte((byte)length);
            for (var i = 0; i < length; i++)
            {
                writer.WriteByte(value[i]);
            }
        }

        [WeaverPriority]
        public static void WriteFixedString512Bytes(this NetworkWriter writer, FixedString512Bytes value)
        {
            var length = value.Length;
            writer.WriteUShort((ushort)length);
            for (var i = 0; i < length; i++)
            {
                writer.WriteByte(value[i]);
            }
        }

        [WeaverPriority]
        public static void WriteFixedString4096Bytes(this NetworkWriter writer, FixedString4096Bytes value)
        {
            var length = value.Length;
            writer.WriteUShort((ushort)length);
            for (var i = 0; i < length; i++)
            {
                writer.WriteByte(value[i]);
            }
        }
    }

    public static class NetworkReaderFixedStringExtensions
    {
        [WeaverPriority]
        public static FixedString32Bytes ReadFixedString32Bytes(this NetworkReader reader)
        {
            var length = reader.ReadByte();
            if (length > FixedString32Bytes.UTF8MaxLengthInBytes)
                throw new EndOfStreamException($"FixedString32Bytes length {length} is out of range.");

            var value = default(FixedString32Bytes);
            value.Length = length;
            for (var i = 0; i < length; i++)
            {
                value[i] = reader.ReadByte();
            }

            return value;
        }

        [WeaverPriority]
        public static FixedString64Bytes ReadFixedString64Bytes(this NetworkReader reader)
        {
            var length = reader.ReadByte();
            if (length > FixedString64Bytes.UTF8MaxLengthInBytes)
                throw new EndOfStreamException($"FixedString64Bytes length {length} is out of range.");

            var value = default(FixedString64Bytes);
            value.Length = length;
            for (var i = 0; i < length; i++)
            {
                value[i] = reader.ReadByte();
            }

            return value;
        }

        [WeaverPriority]
        public static FixedString128Bytes ReadFixedString128Bytes(this NetworkReader reader)
        {
            var length = reader.ReadByte();
            if (length > FixedString128Bytes.UTF8MaxLengthInBytes)
                throw new EndOfStreamException($"FixedString128Bytes length {length} is out of range.");

            var value = default(FixedString128Bytes);
            value.Length = length;
            for (var i = 0; i < length; i++)
            {
                value[i] = reader.ReadByte();
            }

            return value;
        }

        [WeaverPriority]
        public static FixedString512Bytes ReadFixedString512Bytes(this NetworkReader reader)
        {
            var length = reader.ReadUShort();
            if (length > FixedString512Bytes.UTF8MaxLengthInBytes)
                throw new EndOfStreamException($"FixedString512Bytes length {length} is out of range.");

            var value = default(FixedString512Bytes);
            value.Length = length;
            for (var i = 0; i < length; i++)
            {
                value[i] = reader.ReadByte();
            }

            return value;
        }

        [WeaverPriority]
        public static FixedString4096Bytes ReadFixedString4096Bytes(this NetworkReader reader)
        {
            var length = reader.ReadUShort();
            if (length > FixedString4096Bytes.UTF8MaxLengthInBytes)
                throw new EndOfStreamException($"FixedString4096Bytes length {length} is out of range.");

            var value = default(FixedString4096Bytes);
            value.Length = length;
            for (var i = 0; i < length; i++)
            {
                value[i] = reader.ReadByte();
            }

            return value;
        }
    }
}
#endif
