using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary
{
    [Serializable, Preserve]
    public readonly struct GameAssetIndex : IEquatable<GameAssetIndex>
    {
        public readonly uint Value;

        public bool IsValid => Value != 0u;

        public GameAssetIndex(uint value)
        {
            Value = value;
        }

        public bool Equals(GameAssetIndex other) => Value == other.Value;
        public override bool Equals(object value) =>
            value is GameAssetIndex other && Equals(other);
        public override int GetHashCode() => unchecked((int)Value);

        public static bool operator ==(
            GameAssetIndex left,
            GameAssetIndex right) => left.Equals(right);

        public static bool operator !=(
            GameAssetIndex left,
            GameAssetIndex right) => !left.Equals(right);
    }

    [Serializable, Preserve]
    public readonly struct GameAssetIdentityIndex :
        IEquatable<GameAssetIdentityIndex>
    {
        public readonly uint Value;

        public bool IsValid => Value != 0u;

        public GameAssetIdentityIndex(uint value)
        {
            Value = value;
        }

        public bool Equals(GameAssetIdentityIndex other) =>
            Value == other.Value;

        public override bool Equals(object value) =>
            value is GameAssetIdentityIndex other && Equals(other);

        public override int GetHashCode() => unchecked((int)Value);

        public static bool operator ==(
            GameAssetIdentityIndex left,
            GameAssetIdentityIndex right) => left.Equals(right);

        public static bool operator !=(
            GameAssetIdentityIndex left,
            GameAssetIdentityIndex right) => !left.Equals(right);
    }

    public readonly struct GameAssetSessionCatalogEntry
    {
        public readonly GameAssetIndex AssetIndex;
        public readonly GameAssetIdentityIndex IdentityIndex;
        public readonly ResolvedGameAssetReference Asset;

        public GameAssetSessionCatalogEntry(
            in GameAssetIndex assetIndex,
            in GameAssetIdentityIndex identityIndex,
            in ResolvedGameAssetReference asset)
        {
            AssetIndex = assetIndex;
            IdentityIndex = identityIndex;
            Asset = asset;
        }
    }

    public class GameAssetSessionCatalog
    {
        private readonly GameAssetSessionCatalogEntry[] _entries;
        private readonly ReadOnlyCollection<GameAssetSessionCatalogEntry>
            _entryView;
        private readonly Dictionary<ExactAssetIdentity, GameAssetIndex>
            _assetIndices;
        private readonly Dictionary<string, GameAssetIndex> _assetIndicesByKey;
        private readonly Dictionary<string, GameAssetIdentityIndex>
            _identityIndices;

        public IReadOnlyList<GameAssetSessionCatalogEntry> Entries =>
            _entryView;

        public int Count => _entries.Length;

        private GameAssetSessionCatalog(
            GameAssetSessionCatalogEntry[] entries,
            Dictionary<ExactAssetIdentity, GameAssetIndex> assetIndices,
            Dictionary<string, GameAssetIndex> assetIndicesByKey,
            Dictionary<string, GameAssetIdentityIndex> identityIndices)
        {
            _entries = entries;
            _entryView = Array.AsReadOnly(entries);
            _assetIndices = assetIndices;
            _assetIndicesByKey = assetIndicesByKey;
            _identityIndices = identityIndices;
        }

        public static GameAssetSessionCatalog Build(
            IEnumerable<GameAssetLibraryLockEntry> source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var exactByKey = new Dictionary<string, ExactAssetRecord>(
                StringComparer.Ordinal);
            foreach (var entry in source)
            {
                if (entry == null)
                {
                    throw new InvalidOperationException(
                        "A GameAsset session catalog cannot contain a null "
                        + "lock entry.");
                }

                Validate(entry);
                var exactKey = GameAssetRequestKey.Normalize(entry.ResolvedKey);
                var record = new ExactAssetRecord(
                    exactKey,
                    GameAssetIdentityKey.Normalize(entry.ResolvedKey),
                    new ResolvedGameAssetReference(
                        entry.ResolvedKey,
                        entry.ResolvedGuid,
                        entry.MaterializedContentHash));
                if (exactByKey.TryGetValue(exactKey, out var existing))
                {
                    if (!existing.Identity.Equals(record.Identity))
                    {
                        throw new InvalidOperationException(
                            $"Exact GameAsset '{entry.ResolvedKey}' resolves "
                            + "to conflicting GUID or materialized content "
                            + "hash values in one session snapshot.");
                    }

                    continue;
                }

                exactByKey.Add(exactKey, record);
            }

            var ordered = exactByKey.Values
                .OrderBy(value => value.ExactKey, StringComparer.Ordinal)
                .ToArray();
            var orderedIdentities = ordered
                .Select(value => value.VersionlessKey)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();

            RequireSupportedCount(ordered.Length, "exact assets");
            RequireSupportedCount(
                orderedIdentities.Length,
                "versionless asset identities");

            var identityIndices = new Dictionary<
                string,
                GameAssetIdentityIndex>(
                orderedIdentities.Length,
                StringComparer.Ordinal);
            for (var index = 0; index < orderedIdentities.Length; index++)
            {
                identityIndices.Add(
                    orderedIdentities[index],
                    new GameAssetIdentityIndex(checked((uint)index + 1u)));
            }

            var entries = new GameAssetSessionCatalogEntry[ordered.Length];
            var assetIndices = new Dictionary<
                ExactAssetIdentity,
                GameAssetIndex>(ordered.Length);
            var assetIndicesByKey = new Dictionary<string, GameAssetIndex>(
                ordered.Length,
                StringComparer.Ordinal);
            for (var index = 0; index < ordered.Length; index++)
            {
                var record = ordered[index];
                var assetIndex = new GameAssetIndex(
                    checked((uint)index + 1u));
                var identityIndex = identityIndices[record.VersionlessKey];
                var resolvedAsset = record.Identity.Asset;
                entries[index] = new GameAssetSessionCatalogEntry(
                    in assetIndex,
                    in identityIndex,
                    in resolvedAsset);
                assetIndices.Add(record.Identity, assetIndex);
                assetIndicesByKey.Add(record.ExactKey, assetIndex);
            }

            return new GameAssetSessionCatalog(
                entries,
                assetIndices,
                assetIndicesByKey,
                identityIndices);
        }

        public bool TryGet(
            in GameAssetIndex index,
            out ResolvedGameAssetReference asset)
        {
            if (index.Value == 0u || index.Value > (uint)_entries.Length)
            {
                asset = default;
                return false;
            }

            asset = _entries[index.Value - 1u].Asset;
            return true;
        }

        public ResolvedGameAssetReference GetRequired(
            in GameAssetIndex index)
        {
            if (TryGet(in index, out var asset))
            {
                return asset;
            }

            throw new KeyNotFoundException(
                $"GameAsset session catalog does not contain index "
                + $"{index.Value}.");
        }

        public bool TryGetIndex(
            in ResolvedGameAssetReference asset,
            out GameAssetIndex index)
        {
            return _assetIndices.TryGetValue(
                new ExactAssetIdentity(in asset),
                out index);
        }

        public GameAssetIndex GetRequiredIndex(
            in ResolvedGameAssetReference asset)
        {
            if (TryGetIndex(in asset, out var index))
            {
                return index;
            }

            throw new KeyNotFoundException(
                $"GameAsset session catalog does not contain exact asset "
                + $"'{asset.ExactKey}'.");
        }

        public bool TryGetIndex(
            in GameAssetKey exactKey,
            out GameAssetIndex index)
        {
            if (string.IsNullOrWhiteSpace(exactKey.Version))
            {
                index = default;
                return false;
            }

            return _assetIndicesByKey.TryGetValue(
                GameAssetRequestKey.Normalize(exactKey),
                out index);
        }

        public GameAssetIndex GetRequiredIndex(in GameAssetKey exactKey)
        {
            if (TryGetIndex(in exactKey, out var index))
            {
                return index;
            }

            throw new KeyNotFoundException(
                $"GameAsset session catalog does not contain exact key "
                + $"'{exactKey}'.");
        }

        public bool TryGetIdentityIndex(
            in GameAssetKey key,
            out GameAssetIdentityIndex index)
        {
            return _identityIndices.TryGetValue(
                GameAssetIdentityKey.Normalize(key),
                out index);
        }

        public GameAssetIdentityIndex GetRequiredIdentityIndex(
            in GameAssetKey key)
        {
            if (TryGetIdentityIndex(in key, out var index))
            {
                return index;
            }

            throw new KeyNotFoundException(
                $"GameAsset session catalog does not contain versionless "
                + $"identity '{GameAssetIdentityKey.Normalize(key)}'.");
        }

        private static void Validate(GameAssetLibraryLockEntry entry)
        {
            if (!entry.ResolvedGuid.isValid)
            {
                throw new InvalidOperationException(
                    $"GameAsset lock entry '{entry.ResolvedKey}' has no "
                    + "resolved GUID.");
            }
            if (string.IsNullOrWhiteSpace(entry.ResolvedKey.Version))
            {
                throw new InvalidOperationException(
                    $"GameAsset lock entry '{entry.ResolvedKey}' is not "
                    + "exact.");
            }
            if (string.IsNullOrWhiteSpace(entry.MaterializedContentHash))
            {
                throw new InvalidOperationException(
                    $"GameAsset lock entry '{entry.ResolvedKey}' has no "
                    + "materialized content hash.");
            }
        }

        private static void RequireSupportedCount(int count, string label)
        {
            if ((ulong)count > uint.MaxValue)
            {
                throw new InvalidOperationException(
                    $"GameAsset session catalog contains too many {label}: "
                    + $"{count}.");
            }
        }

        private readonly struct ExactAssetRecord
        {
            public readonly string ExactKey;
            public readonly string VersionlessKey;
            public readonly ExactAssetIdentity Identity;

            public ExactAssetRecord(
                string exactKey,
                string versionlessKey,
                in ResolvedGameAssetReference asset)
            {
                ExactKey = exactKey;
                VersionlessKey = versionlessKey;
                Identity = new ExactAssetIdentity(in asset);
            }
        }

        private readonly struct ExactAssetIdentity :
            IEquatable<ExactAssetIdentity>
        {
            public readonly ResolvedGameAssetReference Asset;
            private readonly string _exactKey;

            public ExactAssetIdentity(in ResolvedGameAssetReference asset)
            {
                Asset = asset;
                _exactKey = GameAssetRequestKey.Normalize(asset.ExactKey);
            }

            public bool Equals(ExactAssetIdentity other)
            {
                return string.Equals(
                           _exactKey,
                           other._exactKey,
                           StringComparison.Ordinal)
                       && Asset.AssetGuid == other.Asset.AssetGuid
                       && string.Equals(
                           Asset.MaterializedContentHash,
                           other.Asset.MaterializedContentHash,
                           StringComparison.Ordinal);
            }

            public override bool Equals(object value) =>
                value is ExactAssetIdentity other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(_exactKey, StringComparer.Ordinal);
                hash.Add(Asset.AssetGuid);
                hash.Add(
                    Asset.MaterializedContentHash,
                    StringComparer.Ordinal);
                return hash.ToHashCode();
            }
        }
    }
}
