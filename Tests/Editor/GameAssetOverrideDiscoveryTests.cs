#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Scripting;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Tests.Editor
{
    [Serializable, Preserve]
    public class DiscoveryPlacement
    {
        public GameAssetInstance Instance;
        public string Note;
    }

    [Serializable, Preserve]
    public class DiscoveryFixture_GAC : GameAssetComponent
    {
        public GameAssetInstance Direct;
        public List<DiscoveryPlacement> Nested = new();
        public Dictionary<string, GameAssetInstance> Keyed = new();
    }

    public class GameAssetOverrideDiscoveryTests
    {
        private static readonly GameAssetKey OWNER = new("playground", "map", "level", "0.0.0");
        private static readonly GameAssetKey TARGET = new("base", "static", "tavern_mage", "0.0.0");

        private readonly List<GameAsset> _owned = new();

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < _owned.Count; index++)
            {
                Object.DestroyImmediate(_owned[index]);
            }
            _owned.Clear();
        }

        [Test]
        public void Collect_FindsOverridesAtEveryDepth()
        {
            var asset = CreateAsset(new DiscoveryFixture_GAC
            {
                Direct = Instance(Capacity(1)),
                Nested = new List<DiscoveryPlacement>
                {
                    new() { Instance = Instance(null), Note = "untouched" },
                    new() { Instance = Instance(Capacity(2)) },
                },
                Keyed = new Dictionary<string, GameAssetInstance>
                {
                    ["b"] = Instance(Capacity(3)),
                    ["a"] = Instance(Capacity(4)),
                },
            });

            var sites = GameAssetOverrideDiscovery.Collect(asset);

            Assert.That(sites, Has.Count.EqualTo(4), "the placement without overrides is not a site");
            Assert.That(sites.Select(site => Value(site)), Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
            Assert.That(sites.Select(site => site.Owner), Is.All.EqualTo(OWNER));
            Assert.That(sites.Select(site => site.Asset.RequestedKey), Is.All.EqualTo(TARGET));
        }

        [Test]
        public void Collect_ReportsWhereEachOverrideLives()
        {
            var asset = CreateAsset(new DiscoveryFixture_GAC { Direct = Instance(Capacity(1)) });

            var site = GameAssetOverrideDiscovery.Collect(asset).Single();

            Assert.That(site.Path, Does.Contain("Components"));
            Assert.That(site.Path, Does.Contain("Direct"));
        }

        [Test]
        public void Collect_OrdersDictionaryEntriesOrdinally()
        {
            var asset = CreateAsset(new DiscoveryFixture_GAC
            {
                Keyed = new Dictionary<string, GameAssetInstance>
                {
                    ["b"] = Instance(Capacity(2)),
                    ["a"] = Instance(Capacity(1)),
                },
            });

            var sites = GameAssetOverrideDiscovery.Collect(asset);

            Assert.That(sites.Select(site => Value(site)), Is.EqualTo(new[] { 1, 2 }));
        }

        [Test]
        public void Collect_IgnoresAnAssetWithoutAnyOverride()
        {
            var asset = CreateAsset(new DiscoveryFixture_GAC { Direct = Instance(null) });

            Assert.That(GameAssetOverrideDiscovery.Collect(asset), Is.Empty);
        }

        [Test]
        public void Collect_DoesNotWalkIntoRawOverrideJson()
        {
            var overrides = new GameAssetOverrides
            {
                OverrideComponents = new List<JObject>
                {
                    new() { ["$type"] = "Whatever_GAC", ["Deep"] = new JObject { ["Nested"] = new JArray(1, 2, 3) } },
                },
            };
            var asset = CreateAsset(new DiscoveryFixture_GAC { Direct = Instance(overrides) });

            var site = GameAssetOverrideDiscovery.Collect(asset).Single();

            Assert.That(site.Overrides, Is.SameAs(overrides));
        }

        private static int Value(GameAssetOverrideSite site)
        {
            return site.Overrides.OverrideFields["/UnitStack_GAC/InitialMemberCount"].Value<int>();
        }

        private static GameAssetInstance Instance(GameAssetOverrides overrides)
        {
            return new GameAssetInstance(
                    RuntimeInstanceIdentity.Next(),
                    new GameAssetReference(TARGET),
                    (RuntimeObjectPatch)null)
                .WithOverrides(overrides);
        }

        private static GameAssetOverrides Capacity(int value)
        {
            return new GameAssetOverrides
            {
                OverrideFields = new Dictionary<string, JToken>
                {
                    ["/UnitStack_GAC/InitialMemberCount"] = value,
                },
            };
        }

        private GameAsset CreateAsset(GameAssetComponent component)
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            asset.ResetToDefault(OWNER, IdUtils.NewHash128FromGuid());
            asset.SetComponents(new[] { component });
            _owned.Add(asset);
            return asset;
        }
    }
}
#endif
