using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using Newtonsoft.Json;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary
{
    /// <summary>
    /// Canonical checked-in representation of the immutable GameAsset package.
    /// This file, rather than the live manifest, defines the GA identity accepted
    /// by a runtime/session.
    /// </summary>
    public static class GameAssetLibraryLockFile
    {
        public const string DEFAULT_FILE_NAME = "game_asset_library.lock.json";

        private static readonly JsonSerializerSettings JsonSettings = new()
        {
            Culture = CultureInfo.InvariantCulture,
            DateParseHandling = DateParseHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error,
            NullValueHandling = NullValueHandling.Include,
        };

        public static string GetDefaultPath(string streamingAssetsPath)
        {
            if (string.IsNullOrWhiteSpace(streamingAssetsPath))
                throw new ArgumentException("A StreamingAssets path is required.", nameof(streamingAssetsPath));
            return Path.Combine(streamingAssetsPath, DEFAULT_FILE_NAME);
        }

        public static GameAssetLibraryLock Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A GameAsset library lock path is required.", nameof(path));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException(
                    $"The checked-in GameAsset library lock is missing at '{path}'. Generate it explicitly before running or building.",
                    path);
            }

            return Deserialize(File.ReadAllText(path));
        }

        public static GameAssetLibraryLock LoadStrict(string path, GameAssetTemplateCache templateCache)
        {
            var assetLock = Load(path);
            ValidateStrict(assetLock, templateCache);
            return assetLock;
        }

        public static void ValidateStrict(GameAssetLibraryLock assetLock, GameAssetTemplateCache templateCache)
        {
            if (assetLock == null)
                throw new ArgumentNullException(nameof(assetLock));
            if (templateCache == null)
                throw new ArgumentNullException(nameof(templateCache));
            if (!assetLock.IsReadOnly)
                throw new InvalidOperationException("A runtime GameAsset library lock must be sealed.");

            var liveImmutableLock = GameAssetLibraryLockBuilder.Build(templateCache);
            var lockedJson = SerializeCanonical(assetLock);
            var liveJson = SerializeCanonical(liveImmutableLock);
            if (!string.Equals(lockedJson, liveJson, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The checked-in GameAsset library lock does not exactly match the live immutable built-in package "
                    + "(exact key, GUID, manifest identity, or materialized content hash differs). Regenerate the lock explicitly.");
            }
        }

        public static string SerializeCanonical(GameAssetLibraryLock assetLock)
        {
            ValidateLockHeader(assetLock);

            var document = new LockDocument
            {
                FormatVersion = assetLock.FormatVersion,
                Mods = assetLock.Mods
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => ToDocument(pair.Key, pair.Value))
                    .ToList(),
                Entries = assetLock.Entries
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => ToDocument(assetLock, pair.Key, pair.Value))
                    .ToList(),
            };

            return NormalizeNewlines(JsonConvert.SerializeObject(document, Formatting.Indented, JsonSettings)) + "\n";
        }

        public static GameAssetLibraryLock Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidDataException("The GameAsset library lock is empty.");

            LockDocument document;
            try
            {
                document = JsonConvert.DeserializeObject<LockDocument>(json, JsonSettings);
            }
            catch (Exception exception) when (exception is JsonException || exception is FormatException)
            {
                throw new InvalidDataException("The GameAsset library lock JSON is invalid.", exception);
            }

            if (document == null)
                throw new InvalidDataException("The GameAsset library lock JSON has no root object.");
            if (document.FormatVersion != GameAssetLibraryLock.CURRENT_FORMAT_VERSION)
            {
                throw new InvalidDataException(
                    $"GameAsset library lock format {document.FormatVersion} does not match required format {GameAssetLibraryLock.CURRENT_FORMAT_VERSION}.");
            }
            if (document.Mods == null || document.Mods.Count == 0)
                throw new InvalidDataException("The GameAsset library lock contains no immutable package manifests.");
            if (document.Entries == null || document.Entries.Count == 0)
                throw new InvalidDataException("The GameAsset library lock contains no resolved assets.");

            var result = new GameAssetLibraryLock(document.FormatVersion);
            var seenMods = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < document.Mods.Count; i++)
            {
                var item = document.Mods[i] ?? throw new InvalidDataException($"GameAsset library lock mod {i} is null.");
                RequirePart(item.Mod, $"mods[{i}].mod");
                if (string.IsNullOrWhiteSpace(item.GeneratedUtc))
                    throw new InvalidDataException($"GameAsset library lock mods[{i}].generatedUtc is empty.");
                var normalized = GameAssetIdentityKey.NormalizeMod(item.Mod);
                if (!seenMods.Add(normalized))
                    throw new InvalidDataException($"Duplicate GameAsset library lock mod '{normalized}'.");
                result.SetMod(new GameAssetLibraryLockMod(item.Mod, item.ManifestVersion, item.GeneratedUtc));
            }

            var seenRequests = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < document.Entries.Count; i++)
            {
                var item = document.Entries[i] ?? throw new InvalidDataException($"GameAsset library lock entry {i} is null.");
                var requestedKey = FromDocument(item.RequestedKey, $"entries[{i}].requestedKey", allowLatest: true);
                var resolvedKey = FromDocument(item.ResolvedKey, $"entries[{i}].resolvedKey", allowLatest: false);
                if (!SameIdentity(requestedKey, resolvedKey))
                    throw new InvalidDataException($"GameAsset library lock entry {i} resolves to a different asset identity.");
                if (!string.IsNullOrWhiteSpace(requestedKey.Version)
                    && !string.Equals(requestedKey.Version, resolvedKey.Version, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"GameAsset library lock exact request at entry {i} resolves to another version.");
                }

                var normalizedRequest = GameAssetRequestKey.Normalize(requestedKey);
                if (!seenRequests.Add(normalizedRequest))
                    throw new InvalidDataException($"Duplicate GameAsset library lock request '{normalizedRequest}'.");

                var guid = ParseGuid(item.ResolvedGuid, $"entries[{i}].resolvedGuid");
                RequireLowerHex(item.MaterializedContentHash, 64, $"entries[{i}].materializedContentHash");
                result.Set(requestedKey, new GameAssetLibraryLockEntry(resolvedKey, guid, item.MaterializedContentHash));
            }

            return result.Seal();
        }

        public static bool WriteCanonical(string path, GameAssetLibraryLock assetLock)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A GameAsset library lock path is required.", nameof(path));
            var content = SerializeCanonical(assetLock);
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                return false;

            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException($"GameAsset library lock path '{path}' has no directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }

        private static LockModDocument ToDocument(string normalizedMod, GameAssetLibraryLockMod value)
        {
            if (value == null)
                throw new InvalidDataException($"GameAsset library lock mod '{normalizedMod}' is null.");
            RequirePart(value.Mod, $"mods['{normalizedMod}'].mod");
            if (string.IsNullOrWhiteSpace(value.GeneratedUtc))
                throw new InvalidDataException($"GameAsset library lock mod '{normalizedMod}' has no generated identity.");
            if (!string.Equals(normalizedMod, GameAssetIdentityKey.NormalizeMod(value.Mod), StringComparison.Ordinal))
                throw new InvalidDataException($"GameAsset library lock mod key '{normalizedMod}' is not canonical for '{value.Mod}'.");
            return new LockModDocument
            {
                Mod = value.Mod,
                ManifestVersion = value.ManifestVersion,
                GeneratedUtc = value.GeneratedUtc,
            };
        }

        private static LockEntryDocument ToDocument(
            GameAssetLibraryLock assetLock,
            string normalizedRequest,
            GameAssetLibraryLockEntry value)
        {
            if (value == null)
                throw new InvalidDataException($"GameAsset library lock entry '{normalizedRequest}' is null.");
            if (!assetLock.TryGetRequestedKey(normalizedRequest, out var requestedKey))
                throw new InvalidDataException($"GameAsset library lock lost the requested key for '{normalizedRequest}'.");
            if (!string.Equals(normalizedRequest, GameAssetRequestKey.Normalize(requestedKey), StringComparison.Ordinal))
                throw new InvalidDataException($"GameAsset library lock request key '{normalizedRequest}' is not canonical.");
            ValidateKey(requestedKey, $"entries['{normalizedRequest}'].requestedKey", allowLatest: true);
            ValidateKey(value.ResolvedKey, $"entries['{normalizedRequest}'].resolvedKey", allowLatest: false);
            if (!SameIdentity(requestedKey, value.ResolvedKey))
                throw new InvalidDataException($"GameAsset library lock entry '{normalizedRequest}' resolves to another identity.");
            if (!string.IsNullOrWhiteSpace(requestedKey.Version)
                && !string.Equals(requestedKey.Version, value.ResolvedKey.Version, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"GameAsset library lock entry '{normalizedRequest}' resolves an exact request to another version.");
            }
            if (!value.ResolvedGuid.isValid)
                throw new InvalidDataException($"GameAsset library lock entry '{normalizedRequest}' has no resolved GUID.");
            RequireLowerHex(value.ResolvedGuid.ToString(), 32, $"entries['{normalizedRequest}'].resolvedGuid");
            RequireLowerHex(value.MaterializedContentHash, 64, $"entries['{normalizedRequest}'].materializedContentHash");

            return new LockEntryDocument
            {
                RequestedKey = ToDocument(requestedKey),
                ResolvedKey = ToDocument(value.ResolvedKey),
                ResolvedGuid = value.ResolvedGuid.ToString(),
                MaterializedContentHash = value.MaterializedContentHash,
            };
        }

        private static LockKeyDocument ToDocument(GameAssetKey value)
        {
            return new LockKeyDocument
            {
                Mod = value.Mod,
                Type = value.Type,
                Key = value.Key,
                Version = value.Version ?? string.Empty,
            };
        }

        private static GameAssetKey FromDocument(LockKeyDocument value, string path, bool allowLatest)
        {
            if (value == null)
                throw new InvalidDataException($"GameAsset library lock {path} is null.");
            RequirePart(value.Mod, path + ".mod");
            RequirePart(value.Type, path + ".type");
            RequirePart(value.Key, path + ".key");
            if (!allowLatest && string.IsNullOrWhiteSpace(value.Version))
                throw new InvalidDataException($"GameAsset library lock {path}.version must be exact.");
            if (value.Version == null)
                throw new InvalidDataException($"GameAsset library lock {path}.version is null.");
            return new GameAssetKey(value.Mod, value.Type, value.Key, value.Version);
        }

        private static void ValidateKey(GameAssetKey value, string path, bool allowLatest)
        {
            RequirePart(value.Mod, path + ".mod");
            RequirePart(value.Type, path + ".type");
            RequirePart(value.Key, path + ".key");
            if (value.Version == null)
                throw new InvalidDataException($"GameAsset library lock {path}.version is null.");
            if (!allowLatest && string.IsNullOrWhiteSpace(value.Version))
                throw new InvalidDataException($"GameAsset library lock {path}.version must be exact.");
        }

        private static Hash128 ParseGuid(string value, string path)
        {
            RequireLowerHex(value, 32, path);
            try
            {
                var parsed = Hash128.Parse(value);
                if (!parsed.isValid || !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
                    throw new FormatException();
                return parsed;
            }
            catch (Exception exception) when (exception is FormatException || exception is ArgumentException)
            {
                throw new InvalidDataException($"GameAsset library lock {path} is not a canonical GUID.", exception);
            }
        }

        private static void ValidateLockHeader(GameAssetLibraryLock assetLock)
        {
            if (assetLock == null)
                throw new ArgumentNullException(nameof(assetLock));
            if (assetLock.FormatVersion != GameAssetLibraryLock.CURRENT_FORMAT_VERSION)
            {
                throw new InvalidDataException(
                    $"GameAsset library lock format {assetLock.FormatVersion} does not match required format {GameAssetLibraryLock.CURRENT_FORMAT_VERSION}.");
            }
            if (assetLock.Mods.Count == 0)
                throw new InvalidDataException("The GameAsset library lock contains no immutable package manifests.");
            if (assetLock.Entries.Count == 0)
                throw new InvalidDataException("The GameAsset library lock contains no resolved assets.");
        }

        private static void RequirePart(string value, string path)
        {
            if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
                throw new InvalidDataException($"GameAsset library lock {path} is empty or not canonical.");
        }

        private static void RequireLowerHex(string value, int length, string path)
        {
            if (value == null || value.Length != length)
                throw new InvalidDataException($"GameAsset library lock {path} must contain {length} lowercase hex characters.");
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if ((c < '0' || c > '9') && (c < 'a' || c > 'f'))
                    throw new InvalidDataException($"GameAsset library lock {path} must contain {length} lowercase hex characters.");
            }
        }

        private static bool SameIdentity(GameAssetKey left, GameAssetKey right)
        {
            return string.Equals(left.Mod, right.Mod, StringComparison.Ordinal)
                   && string.Equals(left.Type, right.Type, StringComparison.Ordinal)
                   && string.Equals(left.Key, right.Key, StringComparison.Ordinal);
        }

        private static string NormalizeNewlines(string value)
        {
            return value.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        [JsonObject(MemberSerialization.OptIn)]
        private sealed class LockDocument
        {
            [JsonProperty("formatVersion", Order = 0, Required = Required.Always)]
            public int FormatVersion { get; set; }

            [JsonProperty("mods", Order = 1, Required = Required.Always)]
            public List<LockModDocument> Mods { get; set; }

            [JsonProperty("entries", Order = 2, Required = Required.Always)]
            public List<LockEntryDocument> Entries { get; set; }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private sealed class LockModDocument
        {
            [JsonProperty("mod", Order = 0, Required = Required.Always)]
            public string Mod { get; set; }

            [JsonProperty("manifestVersion", Order = 1, Required = Required.Always)]
            public int ManifestVersion { get; set; }

            [JsonProperty("generatedUtc", Order = 2, Required = Required.Always)]
            public string GeneratedUtc { get; set; }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private sealed class LockEntryDocument
        {
            [JsonProperty("requestedKey", Order = 0, Required = Required.Always)]
            public LockKeyDocument RequestedKey { get; set; }

            [JsonProperty("resolvedKey", Order = 1, Required = Required.Always)]
            public LockKeyDocument ResolvedKey { get; set; }

            [JsonProperty("resolvedGuid", Order = 2, Required = Required.Always)]
            public string ResolvedGuid { get; set; }

            [JsonProperty("materializedContentHash", Order = 3, Required = Required.Always)]
            public string MaterializedContentHash { get; set; }
        }

        [JsonObject(MemberSerialization.OptIn)]
        private sealed class LockKeyDocument
        {
            [JsonProperty("mod", Order = 0, Required = Required.Always)]
            public string Mod { get; set; }

            [JsonProperty("type", Order = 1, Required = Required.Always)]
            public string Type { get; set; }

            [JsonProperty("key", Order = 2, Required = Required.Always)]
            public string Key { get; set; }

            [JsonProperty("version", Order = 3, Required = Required.Always)]
            public string Version { get; set; }
        }
    }
}
