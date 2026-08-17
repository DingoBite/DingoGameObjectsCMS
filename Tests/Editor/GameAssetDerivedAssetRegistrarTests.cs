#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using SnakeAndMice.GameComponents.Combat.Components;
using SnakeAndMice.GameComponents.RuntimePatches.Editor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Tests.Editor
{
    /// <summary>
    /// Covers the session seal: every variant an authored placement asks for
    /// becomes an entry of the immutable library lock, so a runtime object
    /// materialized from that placement can take its catalog indices from the
    /// session like any other asset.
    /// </summary>
    public class GameAssetDerivedAssetRegistrarTests
    {
        private const string MODULE = "base";

        private static readonly GameAssetKey UNIT = new(MODULE, "unit", "mouse", "1.0.0");
        private static readonly GameAssetKey WEAPON = new(MODULE, "item", "sting", "1.0.0");
        private static readonly GameAssetKey LEVEL = new(MODULE, "map", "arena", "1.0.0");

        private string _root;
        private RuntimePatchCodecRegistry _registry;
        private GameAssetTemplateCache _templates;
        private readonly List<GameAsset> _owned = new();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                $"{nameof(GameAssetDerivedAssetRegistrarTests)}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
            _registry = RuntimePatchEditorEnvironment.CreateRegistry();
            _templates = new GameAssetTemplateCache(_registry, RuntimeTemplatePatchCodecContext.Instance);
        }

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _owned.Count; index++)
            {
                Object.DestroyImmediate(_owned[index]);
            }
            _owned.Clear();
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void Build_RegistersTheVariantAnAuthoredPlacementAsksFor()
        {
            var assetLock = BuildLock(
                Unit(maximum: 100),
                Level(new DiscoveryFixture_GAC { Direct = Placement(UNIT, Maximum(1)) }));

            var derived = DerivedEntries(assetLock).Single();

            Assert.That(
                derived.ResolvedKey.Key,
                Does.StartWith(UNIT.Key + GameAssetDerivedIdentity.KEY_SUFFIX_SEPARATOR),
                "the origin of a variant stays readable in its key");
            Assert.That(derived.ResolvedKey.Mod, Is.EqualTo(UNIT.Mod));
            Assert.That(derived.ResolvedKey.Type, Is.EqualTo(UNIT.Type));
            Assert.That(derived.ResolvedKey.Version, Is.EqualTo(UNIT.Version));
            Assert.That(
                derived.ResolvedKey,
                Is.EqualTo(GameAssetDerivedIdentity.CreateKey(
                    _templates.ResolveStrict(new GameAssetReference(UNIT), assetLock).Asset,
                    Maximum(1),
                    _registry.SchemaHash)),
                "the entry is keyed by the identity both sides compute from the base and the override");
            Assert.That(
                GameAssetLibraryLockBuilder.TryResolve(derived.ResolvedKey, assetLock, out _),
                Is.True,
                "a derived asset resolves through the lock like any other");
        }

        [Test]
        public void DerivedLockEntry_MaterializesThePlacementWithSessionCatalogIndices()
        {
            var assetLock = BuildLock(
                Unit(maximum: 100),
                Level(new DiscoveryFixture_GAC { Direct = Placement(UNIT, Maximum(1)) }));

            var runtimeObject = _templates.Materialize(Placement(LEVEL, assetLock), assetLock);

            Assert.That(runtimeObject.TakeRO<HitPoints_GRC>().Maximum, Is.EqualTo(1), "overridden by the placement");
            Assert.That(runtimeObject.Has<DamageImmunity_GRC>(), Is.True, "inherited from the base");
            Assert.That(runtimeObject.Key, Is.EqualTo(DerivedEntries(assetLock).Single().ResolvedKey));
            Assert.That(
                runtimeObject.RuntimeGameAssetIdentity.IsValid,
                Is.True,
                "the variant is a session asset, not only a template cache registration");
        }

        [Test]
        public void Build_RegistersOneAssetPerDistinctVariant()
        {
            var assetLock = BuildLock(
                Unit(maximum: 100),
                Level(new DiscoveryFixture_GAC
                {
                    Direct = Placement(UNIT, Maximum(1)),
                    Nested = new List<DiscoveryPlacement>
                    {
                        new() { Instance = Placement(UNIT, Maximum(1)), Note = "same variant" },
                        new() { Instance = Placement(UNIT, Maximum(2)), Note = "another variant" },
                    },
                }));

            var derived = DerivedEntries(assetLock);

            Assert.That(derived, Has.Count.EqualTo(2), "identity is content-derived, so two equal overrides share it");
        }

        [Test]
        public void Build_RegistersVariantsIntroducedInsideADerivedAsset()
        {
            // The level rewrites the unit's own placement of its weapon. That
            // inner variant exists in no authored document — only in the
            // composed one — so it is found by scanning the derived asset.
            var assetLock = BuildLock(
                Weapon(maximum: 10),
                Unit(maximum: 100, new DiscoveryFixture_GAC { Direct = Placement(WEAPON, Maximum(5)) }),
                Level(new DiscoveryFixture_GAC
                {
                    Direct = Placement(UNIT, new GameAssetOverrides
                    {
                        OverrideFields = new Dictionary<string, JToken>
                        {
                            ["/DiscoveryFixture_GAC/Direct/Overrides/OverrideFields/~1HitPoints_GAC~1Maximum"] = 7,
                        },
                    }),
                }));

            Assert.That(
                DerivedEntries(assetLock).Where(entry => IsVariantOf(entry, WEAPON)).ToList(),
                Has.Count.EqualTo(2),
                "the weapon the unit authored and the one the level rewrote");

            var unitVariant = DerivedEntries(assetLock).Single(entry => IsVariantOf(entry, UNIT));
            Assert.That(
                _templates.Materialize(Placement(unitVariant.ResolvedKey, assetLock), assetLock)
                    .TakeRO<HitPoints_GRC>()
                    .Maximum,
                Is.EqualTo(7),
                "the rewritten inner placement resolves to its own registered variant");
        }

        [Test]
        public void Build_RejectsAPlacementOverridingAnAssetTheSessionDoesNotProvide()
        {
            var ghost = new GameAssetKey(MODULE, "unit", "ghost", "1.0.0");

            var exception = Assert.Throws<InvalidDataException>(() => BuildLock(
                Unit(maximum: 100),
                Level(new DiscoveryFixture_GAC { Direct = Placement(ghost, Maximum(1)) })));

            Assert.That(exception.Message, Does.Contain(ghost.ToString()));
            Assert.That(exception.Message, Does.Contain("Direct"), "the message locates the placement");
        }

        private static IReadOnlyList<GameAssetLibraryLockEntry> DerivedEntries(GameAssetLibraryLock assetLock)
        {
            return assetLock.Entries.Values
                .Where(entry => GameAssetDerivedIdentity.IsDerived(entry.ResolvedKey))
                .ToList();
        }

        private static bool IsVariantOf(GameAssetLibraryLockEntry entry, GameAssetKey baseKey)
        {
            return entry.ResolvedKey.Key.StartsWith(
                baseKey.Key + GameAssetDerivedIdentity.KEY_SUFFIX_SEPARATOR,
                StringComparison.Ordinal);
        }

        /// <summary>
        /// The placement authored inside the asset the lock resolves for
        /// <paramref name="owner"/>.
        /// </summary>
        private static GameAssetInstance Placement(GameAssetKey owner, GameAssetLibraryLock assetLock)
        {
            GameAssetLibraryLockBuilder.TryResolve(owner, assetLock, out var resolved);
            return ((GameAsset)resolved).Components
                .OfType<DiscoveryFixture_GAC>()
                .Single()
                .Direct;
        }

        private static GameAssetInstance Placement(GameAssetKey asset, GameAssetOverrides overrides)
        {
            return new GameAssetInstance(
                    RuntimeInstanceIdentity.Next(),
                    new GameAssetReference(asset),
                    (RuntimeObjectPatch)null)
                .WithOverrides(overrides);
        }

        private static GameAssetOverrides Maximum(int value)
        {
            return new GameAssetOverrides
            {
                OverrideFields = new Dictionary<string, JToken> { ["/HitPoints_GAC/Maximum"] = value },
            };
        }

        private GameAsset Unit(int maximum, params GameAssetComponent[] extra)
        {
            var components = new List<GameAssetComponent>
            {
                new HitPoints_GAC { Maximum = maximum },
                new DamageImmunity_GAC(),
            };
            components.AddRange(extra);
            return CreateAsset(UNIT, components);
        }

        private GameAsset Weapon(int maximum)
        {
            return CreateAsset(WEAPON, new List<GameAssetComponent> { new HitPoints_GAC { Maximum = maximum } });
        }

        private GameAsset Level(GameAssetComponent placements)
        {
            return CreateAsset(LEVEL, new List<GameAssetComponent> { placements });
        }

        private GameAsset CreateAsset(GameAssetKey key, List<GameAssetComponent> components)
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            asset.ResetToDefault(key, IdUtils.NewHash128FromGuid());
            asset.SetComponents(components);
            _owned.Add(asset);
            return asset;
        }

        /// <summary>
        /// Writes the authored assets as a real mounted module and builds the
        /// session lock from it, so the registration runs exactly where the
        /// session seals rather than through a hand-assembled lock.
        /// </summary>
        private GameAssetLibraryLock BuildLock(params GameAsset[] assets)
        {
            var moduleRoot = Path.Combine(_root, MODULE);
            var manifestAssets = new JArray();
            for (var index = 0; index < assets.Length; index++)
            {
                var asset = assets[index];
                var relativeJsonPath =
                    $"{asset.Key.Type}/{asset.Key.Key}/{asset.Key.Key}@{asset.Key.Version}.json";
                var assetPath = Path.Combine(
                    moduleRoot,
                    relativeJsonPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
                File.WriteAllText(assetPath, asset.ToJson());
                manifestAssets.Add(new JObject
                {
                    ["Key"] = JObject.FromObject(asset.Key, GameAssetJson.JsonSerializer),
                    ["GUID"] = asset.GUID.ToString(),
                    ["RelativeJsonPath"] = relativeJsonPath,
                });
            }

            File.WriteAllText(
                Path.Combine(moduleRoot, "manifest.json"),
                new JObject
                {
                    ["Mod"] = MODULE,
                    ["ManifestVersion"] = 1,
                    ["GeneratedUtc"] = "2026-01-01T00:00:00Z",
                    ["Assets"] = manifestAssets,
                }.ToString());

            var package = new ModPackage(GameAssetModuleContentScanner.Scan(moduleRoot, MODULE));
            return GameAssetLibraryLockBuilder.Build(_templates, new[] { package });
        }
    }
}
#endif
