using System;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public static class StableIdentityHash
    {
        public const ulong FNV1A_64_OFFSET_BASIS =
            14695981039346656037UL;
        public const ulong FNV1A_64_PRIME = 1099511628211UL;

        public const ulong SECONDARY_LANE_OFFSET_BASIS =
            7809847782465536322UL;

        public const ulong LEGACY_TOKEN_MIX_SEED =
            1469598103934665603UL;

        private const ulong UTF16_SECONDARY_LANE_BIAS = 0x9e37UL;
        private const ulong BYTE_SECONDARY_LANE_BIAS = 0x9eUL;

        public static void Initialize128(
            ulong lowDomainSalt,
            ulong highDomainSalt,
            out ulong low,
            out ulong high)
        {
            low = FNV1A_64_OFFSET_BASIS ^ lowDomainSalt;
            high = SECONDARY_LANE_OFFSET_BASIS ^ highDomainSalt;
        }

        public static void HashGameAssetKey128(
            in GameAssetKey key,
            ulong lowDomainSalt,
            ulong highDomainSalt,
            ulong highSeparatorMarker,
            out ulong low,
            out ulong high)
        {
            Initialize128(
                lowDomainSalt,
                highDomainSalt,
                out low,
                out high);
            AppendString128(ref low, ref high, key.Mod);
            AppendSeparator128(ref low, ref high, highSeparatorMarker);
            AppendString128(ref low, ref high, key.Type);
            AppendSeparator128(ref low, ref high, highSeparatorMarker);
            AppendString128(ref low, ref high, key.Key);
            AppendSeparator128(ref low, ref high, highSeparatorMarker);
            AppendString128(ref low, ref high, key.Version);
        }

        public static void AppendString128(
            ref ulong low,
            ref ulong high,
            string value)
        {
            if (value == null)
                return;

            unchecked
            {
                for (var index = 0; index < value.Length; index++)
                {
                    var character = value[index];
                    low = (low ^ character) * FNV1A_64_PRIME;
                    high = (high ^ (character
                                    + UTF16_SECONDARY_LANE_BIAS))
                           * FNV1A_64_PRIME;
                }
            }
        }

        public static void AppendUInt32LittleEndian128(
            ref ulong low,
            ref ulong high,
            uint value)
        {
            AppendByte128(ref low, ref high, (byte)value);
            AppendByte128(ref low, ref high, (byte)(value >> 8));
            AppendByte128(ref low, ref high, (byte)(value >> 16));
            AppendByte128(ref low, ref high, (byte)(value >> 24));
        }

        public static void AppendLengthPrefixedBytes128(
            ref ulong low,
            ref ulong high,
            byte[] value)
        {
            if (value == null)
            {
                AppendUInt32LittleEndian128(
                    ref low,
                    ref high,
                    uint.MaxValue);
                return;
            }

            AppendUInt32LittleEndian128(
                ref low,
                ref high,
                unchecked((uint)value.Length));
            for (var index = 0; index < value.Length; index++)
            {
                AppendByte128(ref low, ref high, value[index]);
            }
        }

        public static void AppendByte128(
            ref ulong low,
            ref ulong high,
            byte value)
        {
            unchecked
            {
                low = (low ^ value) * FNV1A_64_PRIME;
                high = (high ^ (value + BYTE_SECONDARY_LANE_BIAS))
                       * FNV1A_64_PRIME;
            }
        }

        public static void AppendSeparator128(
            ref ulong low,
            ref ulong high,
            ulong highMarker)
        {
            unchecked
            {
                low = (low ^ 0xffUL) * FNV1A_64_PRIME;
                high = (high ^ highMarker) * FNV1A_64_PRIME;
            }
        }

        public static ulong AppendUInt64LittleEndian64(
            ulong hash,
            ulong value)
        {
            unchecked
            {
                for (var shift = 0; shift < 64; shift += 8)
                {
                    hash ^= (byte)(value >> shift);
                    hash *= FNV1A_64_PRIME;
                }
                return hash;
            }
        }

        public static ulong RotateLeft64(ulong value, int shift)
        {
            if ((uint)shift >= 64u)
            {
                throw new ArgumentOutOfRangeException(nameof(shift));
            }
            return shift == 0
                ? value
                : value << shift | value >> (64 - shift);
        }
    }
}
