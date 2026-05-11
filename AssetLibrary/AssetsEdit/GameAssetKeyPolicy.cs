#if NEWTONSOFT_EXISTS
using System;
using System.Linq;
using DingoGameObjectsCMS;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoUnityExtensions.Utils;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class GameAssetKeyPolicy
    {
        private const int DEFAULT_KEY_MAX_LENGTH = 48;
        private const int COLLISION_KEY_MAX_LENGTH = 39;
        private const int REPEATED_COLLISION_KEY_MAX_LENGTH = 36;
        private const int MAX_KEY_ATTEMPTS = 1000;

        public static string NormalizeModName(string mod, string fallback = GameAssetKey.UNDEFINED)
        {
            var normalized = (mod ?? string.Empty).NormalizePath().Trim('/');
            return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
        }

        public static string NormalizeSegment(string value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)
                || string.Equals(value, GameAssetKey.NONE, StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, GameAssetKey.UNDEFINED, StringComparison.OrdinalIgnoreCase))
            {
                return fallback;
            }

            return BuildSlug(value, fallback);
        }

        public static string BuildSlug(string value, string fallback, int maxLength = DEFAULT_KEY_MAX_LENGTH)
        {
            var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            var chars = source.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray();
            var slug = new string(chars).Trim('_');
            while (slug.Contains("__", StringComparison.Ordinal))
                slug = slug.Replace("__", "_", StringComparison.Ordinal);

            if (string.IsNullOrWhiteSpace(slug))
                slug = fallback;

            return TrimTo(slug, maxLength);
        }

        public static string TrimTo(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            return value.Length <= maxLength ? value : value.Substring(0, maxLength).Trim('_');
        }

        public static string IncrementPatchVersion(string version)
        {
            if (!Version.TryParse(string.IsNullOrWhiteSpace(version) ? GameAssetKey.ZERO_V : version.Trim(), out var parsed))
                return "0.0.1";

            var major = Mathf.Max(0, parsed.Major);
            var minor = Mathf.Max(0, parsed.Minor);
            var patch = Mathf.Max(0, parsed.Build) + 1;
            return $"{major}.{minor}.{patch}";
        }

        public static GameAssetKey CreateUniqueKey(GameAsset asset, string mod, string fallbackType, Func<GameAssetKey, bool> keyExists)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));
            if (keyExists == null)
                throw new ArgumentNullException(nameof(keyExists));

            var normalizedMod = NormalizeModName(mod);
            var type = NormalizeSegment(asset.Key.Type, fallbackType);
            var baseKey = BuildSlug(!string.IsNullOrWhiteSpace(asset.name) ? asset.name : asset.Key.Key, fallbackType);
            var key = new GameAssetKey(normalizedMod, type, baseKey, GameAssetKey.ZERO_V);
            if (!keyExists(key))
                return key;

            var suffix = IdUtils.NewHash128FromGuid().ToString().Substring(0, 8);
            key = new GameAssetKey(normalizedMod, type, $"{TrimTo(baseKey, COLLISION_KEY_MAX_LENGTH)}_{suffix}", GameAssetKey.ZERO_V);
            if (!keyExists(key))
                return key;

            for (var i = 2; i < MAX_KEY_ATTEMPTS; i++)
            {
                key = new GameAssetKey(normalizedMod, type, $"{TrimTo(baseKey, REPEATED_COLLISION_KEY_MAX_LENGTH)}_{suffix}_{i}", GameAssetKey.ZERO_V);
                if (!keyExists(key))
                    return key;
            }

            throw new InvalidOperationException($"Cannot create unique asset key for '{baseKey}'.");
        }

        public static GameAssetKey CreateNextVersionKey(GameAssetKey previousKey, Func<GameAssetKey, bool> keyExists)
        {
            if (keyExists == null)
                throw new ArgumentNullException(nameof(keyExists));

            var version = previousKey.Version;
            for (var i = 0; i < MAX_KEY_ATTEMPTS; i++)
            {
                version = IncrementPatchVersion(version);
                var nextKey = new GameAssetKey(previousKey.Mod, previousKey.Type, previousKey.Key, version);
                if (!keyExists(nextKey))
                    return nextKey;
            }

            throw new InvalidOperationException($"Cannot create next asset version for '{previousKey}'.");
        }
    }
}
#endif
