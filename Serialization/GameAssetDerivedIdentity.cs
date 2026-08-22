#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using UnityEngine;

namespace DingoGameObjectsCMS.Serialization
{
    public static class GameAssetDerivedIdentity
    {
        public const int FORMAT_VERSION = 1;
        public const string KEY_SUFFIX_SEPARATOR = "#";
        public const int KEY_SUFFIX_LENGTH = 12;

        public static GameAssetKey CreateKey(
            in ResolvedGameAssetReference baseAsset,
            GameAssetOverrides overrides,
            string schemaHash)
        {
            var exactKey = baseAsset.ExactKey;
            return new GameAssetKey(
                exactKey.Mod,
                exactKey.Type,
                exactKey.Key + KEY_SUFFIX_SEPARATOR + Fingerprint(baseAsset, overrides, schemaHash)[..KEY_SUFFIX_LENGTH],
                exactKey.Version);
        }

        public static Hash128 CreateGuid(
            in ResolvedGameAssetReference baseAsset,
            GameAssetOverrides overrides,
            string schemaHash)
        {
            return Hash128.Parse(Fingerprint(baseAsset, overrides, schemaHash)[..32]);
        }

        public static bool IsDerived(in GameAssetKey key)
        {
            return key.Key != null && key.Key.Contains(KEY_SUFFIX_SEPARATOR, StringComparison.Ordinal);
        }

        private static string Fingerprint(
            in ResolvedGameAssetReference baseAsset,
            GameAssetOverrides overrides,
            string schemaHash)
        {
            if (!baseAsset.AssetGuid.isValid)
                throw new InvalidDataException("A derived asset requires a base with a valid GUID.");
            if (string.IsNullOrWhiteSpace(baseAsset.MaterializedContentHash))
                throw new InvalidDataException($"A derived asset requires an exact base; '{baseAsset.ExactKey}' has no materialized content hash.");
            if (string.IsNullOrWhiteSpace(schemaHash))
                throw new InvalidDataException("A derived asset requires the active runtime schema hash.");

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(FORMAT_VERSION);
                writer.Write(baseAsset.AssetGuid.ToString());
                writer.Write(baseAsset.MaterializedContentHash);
                writer.Write(schemaHash);
                writer.Write(GameAssetDocumentComposer.CanonicalizeOverrides(overrides));
            }

            using var sha = SHA256.Create();
            return ToHex(sha.ComputeHash(buffer.ToArray()));
        }

        private static string ToHex(byte[] hash)
        {
            var builder = new StringBuilder(hash.Length * 2);
            for (var index = 0; index < hash.Length; index++)
            {
                builder.Append(hash[index].ToString("x2"));
            }

            return builder.ToString();
        }
    }
}
#endif
