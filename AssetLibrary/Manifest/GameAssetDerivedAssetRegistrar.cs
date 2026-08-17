using System;
using System.Collections.Generic;
using System.IO;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary
{
    /// <summary>
    /// Registers the variant every authored placement override asks for as a
    /// first-class asset of the session.
    ///
    /// A placement carrying overrides resolves to a derived asset instead of the
    /// one it references, and a materialized runtime object reads its catalog
    /// indices out of the library lock — so a derived asset has to be a lock
    /// entry, not only a template cache registration. This runs while the lock
    /// is still mutable, immediately before it is sealed.
    ///
    /// Composition happens on the document, exactly like a named prefab does:
    /// the resolved base is written back to its document, the sparse override is
    /// applied to it, and the result is deserialized as an ordinary complete
    /// GameAsset carrying the derived identity. Nothing downstream can tell it
    /// from a hand duplicated asset.
    /// </summary>
    public static class GameAssetDerivedAssetRegistrar
    {
        /// <summary>
        /// Composes and registers every derived asset the mounted content can
        /// reach, and returns their exact keys in registration order.
        ///
        /// A derived asset is authored content too, so it is scanned in turn: an
        /// override may change a placement that itself carries overrides, and
        /// that inner variant exists only inside the composed document. The walk
        /// closes because identity is content-derived — a placement that the
        /// composition left alone lands back on the derived asset already
        /// registered, and the queue drops it.
        /// </summary>
        public static IReadOnlyList<GameAssetKey> RegisterAuthoredOverrides(
            GameAssetLibraryLock assetLock,
            GameAssetTemplateCache templateCache,
            IReadOnlyList<GameAsset> authoredAssets)
        {
            if (assetLock == null)
                throw new ArgumentNullException(nameof(assetLock));
            if (templateCache == null)
                throw new ArgumentNullException(nameof(templateCache));
            if (authoredAssets == null)
                throw new ArgumentNullException(nameof(authoredAssets));

            var schemaHash = templateCache.CodecRegistry.SchemaHash;
            var registered = new List<GameAssetKey>();
            var known = new HashSet<Hash128>();
            var pending = new Queue<GameAsset>(authoredAssets.Count);
            for (var index = 0; index < authoredAssets.Count; index++)
            {
                pending.Enqueue(
                    authoredAssets[index]
                    ?? throw new InvalidOperationException(
                        $"Mounted GameAsset {index} is null and cannot be scanned for authored overrides."));
            }

            while (pending.Count > 0)
            {
                var owner = pending.Dequeue();
                var sites = GameAssetOverrideDiscovery.Collect(owner);
                for (var index = 0; index < sites.Count; index++)
                {
                    var site = sites[index];
                    var baseAsset = ResolveBase(assetLock, site);
                    var baseBlueprint = templateCache.ResolveStrict(site.Asset, assetLock);
                    var guid = GameAssetDerivedIdentity.CreateGuid(
                        baseBlueprint.Asset,
                        site.Overrides,
                        schemaHash);
                    if (!known.Add(guid))
                        continue;

                    var key = GameAssetDerivedIdentity.CreateKey(
                        baseBlueprint.Asset,
                        site.Overrides,
                        schemaHash);
                    var derived = Compose(site, baseAsset, key, guid);
                    var blueprint = templateCache.RegisterDerived(
                        baseBlueprint.Asset,
                        site.Overrides,
                        derived);
                    assetLock.Set(key, new GameAssetLibraryLockEntry(
                        key,
                        guid,
                        blueprint.Asset.MaterializedContentHash,
                        derived));
                    registered.Add(key);
                    pending.Enqueue(derived);
                }
            }

            return Array.AsReadOnly(registered.ToArray());
        }

        private static GameAsset ResolveBase(
            GameAssetLibraryLock assetLock,
            in GameAssetOverrideSite site)
        {
            if (GameAssetLibraryLockBuilder.TryResolve(
                    site.Asset.RequestedKey,
                    assetLock,
                    out var resolved)
                && resolved is GameAsset gameAsset)
            {
                return gameAsset;
            }

            throw new InvalidDataException(
                $"GameAsset '{site.Owner}' overrides '{site.Asset.RequestedKey}' at '{site.Path}', "
                + "which no mounted module resolves to a GameAsset.");
        }

        private static GameAsset Compose(
            in GameAssetOverrideSite site,
            GameAsset baseAsset,
            GameAssetKey key,
            Hash128 guid)
        {
            JObject document;
            try
            {
                document = GameAssetDocumentComposer.ApplyOverrides(
                    baseAsset.Key,
                    JObject.Parse(baseAsset.ToJson()),
                    site.Overrides);
            }
            catch (Exception exception) when (
                exception is InvalidDataException
                || exception is JsonException)
            {
                throw new InvalidDataException(
                    $"GameAsset '{site.Owner}' cannot compose its override of '{baseAsset.Key}' at '{site.Path}'.",
                    exception);
            }

            document[GameAssetDocumentComposer.KEY_PROPERTY] =
                JObject.FromObject(key, GameAssetJson.JsonSerializer);
            document[GameAssetDocumentComposer.GUID_PROPERTY] = guid.ToString();

            // Lineage belongs to the base document alone. Keeping it would let a
            // later composition re-derive this asset from its own base with the
            // overrides already applied.
            document.Remove(GameAssetDocumentComposer.PREFAB_PROPERTY);

            GameAssetScriptableObject composed;
            try
            {
                composed = GameAssetJson.FromJObject(document);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"GameAsset '{site.Owner}' composed an invalid asset from its override of "
                    + $"'{baseAsset.Key}' at '{site.Path}'.",
                    exception);
            }

            if (composed is not GameAsset derived)
            {
                throw new InvalidDataException(
                    $"GameAsset '{site.Owner}' composed its override of '{baseAsset.Key}' at '{site.Path}' "
                    + $"into '{composed?.GetType().FullName ?? "null"}', which is not a {nameof(GameAsset)}.");
            }

            derived.name = $"{key.Key}@{key.Version}";
            return derived;
        }
    }
}
