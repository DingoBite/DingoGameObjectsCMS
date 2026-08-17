#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class GameAssetDocumentComposerTests
    {
        private const string MOD = "base";
        private const string TYPE = "building";
        private const string CAPACITY_PATH = "/Tavern_GAC/Capacity";
        private const string SPRITE_PATH = "/Sprite_GAC/Visual/Resource/RelativePath";

        private Dictionary<GameAssetKey, JObject> _library;

        [SetUp]
        public void SetUp()
        {
            _library = new Dictionary<GameAssetKey, JObject>();
        }

        [Test]
        public void Compose_WithoutBase_ReturnsDocumentUnchanged()
        {
            var key = Key("tavern");
            var document = Tavern(key, capacity: 10);

            Assert.That(GameAssetDocumentComposer.Compose(key, document, Resolve), Is.SameAs(document));
        }

        [Test]
        public void OverrideFields_ReplacesOneField_AndInheritsEverythingElse()
        {
            var composed = ComposeDerived(prefab => prefab["OverrideFields"] = new JObject
            {
                [CAPACITY_PATH] = 1_000_000,
            });

            var tavern = Component(composed, "Tavern_GAC");
            Assert.That(tavern["Capacity"].Value<long>(), Is.EqualTo(1_000_000));
            Assert.That(tavern["Name"].Value<string>(), Is.EqualTo("tavern"));
            Assert.That(Component(composed, "Sprite_GAC")["PixelsPerUnit"].Value<int>(), Is.EqualTo(32));
        }

        [Test]
        public void OverrideFields_ReachesNestedLeafWithoutRestatingItsParent()
        {
            var composed = ComposeDerived(prefab => prefab["OverrideFields"] = new JObject
            {
                [SPRITE_PATH] = "sprites/dark_tavern.png",
            });

            var visual = Component(composed, "Sprite_GAC")["Visual"];
            Assert.That(visual["Resource"]["RelativePath"].Value<string>(), Is.EqualTo("sprites/dark_tavern.png"));
            Assert.That(visual["Resource"]["ModuleId"].Value<string>(), Is.EqualTo("base"));
            Assert.That(visual["PresetId"].Value<string>(), Is.EqualTo("tile"));
        }

        [Test]
        public void OverrideFields_RejectsARootFieldBecauseOnlyComponentsAreOverridable()
        {
            var baseDocument = Tavern(Key("level"), capacity: 1);
            baseDocument["$type"] = "LevelGridAsset";
            baseDocument["Surfaces"] = new JArray { new JObject { ["Width"] = 4 } };
            var baseKey = Publish("level", baseDocument);
            var derivedKey = Key("level_wide");
            var derived = Derived(derivedKey, baseKey);
            derived["$type"] = "LevelGridAsset";
            derived["Prefab"]["OverrideFields"] = new JObject { ["/Surfaces/0/Width"] = 16 };

            Assert.That(Throws(derivedKey, derived), Does.Contain("does not match any 'Surfaces'"));
        }

        [Test]
        public void OverrideFields_ReachesIntoAnArrayInsideAComponent()
        {
            var composed = ComposeDerived(prefab => prefab["OverrideFields"] = new JObject
            {
                ["/Tavern_GAC/Slots/1"] = "second_replaced",
            });

            Assert.That(Component(composed, "Tavern_GAC")["Slots"][0].Value<string>(), Is.EqualTo("first"));
            Assert.That(Component(composed, "Tavern_GAC")["Slots"][1].Value<string>(), Is.EqualTo("second_replaced"));
        }

        [Test]
        public void OverrideFields_ReachesADocumentRootFieldThroughParentSegment()
        {
            var baseDocument = Tavern(Key("level"), capacity: 1);
            baseDocument["$type"] = "LevelGridAsset";
            baseDocument["Surfaces"] = new JArray { new JObject { ["Width"] = 4 } };
            var baseKey = Publish("level", baseDocument);
            var derivedKey = Key("level_wide");
            var derived = Derived(derivedKey, baseKey);
            derived["$type"] = "LevelGridAsset";
            derived["Prefab"]["OverrideFields"] = new JObject { ["/../Surfaces/0/Width"] = 16 };

            var composed = GameAssetDocumentComposer.Compose(derivedKey, derived, Resolve);

            Assert.That(composed["Surfaces"][0]["Width"].Value<int>(), Is.EqualTo(16));
        }

        [Test]
        public void OverrideFields_ReachesASiblingComponentThroughParentSegment()
        {
            var composed = ComposeDerived(prefab => prefab["OverrideFields"] = new JObject
            {
                ["/Tavern_GAC/../Sprite_GAC/PixelsPerUnit"] = 128,
            });

            Assert.That(Component(composed, "Sprite_GAC")["PixelsPerUnit"].Value<int>(), Is.EqualTo(128));
        }

        [Test]
        public void OverrideFields_RejectsAReservedRootPropertyReachedThroughParentSegment()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["OverrideFields"] = new JObject { ["/../GUID"] = "deadbeef" }),
                Does.Contain("reserved property"));
        }

        [Test]
        public void OverrideFields_RejectsWalkingAboveTheDocumentRoot()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["OverrideFields"] = new JObject { ["/../../Whatever"] = 1 }),
                Does.Contain("above the document root"));
        }

        [Test]
        public void OverrideFields_AppliesInOrdinalOrderRegardlessOfDocumentOrder()
        {
            var forward = ComposeDerived(prefab => prefab["OverrideFields"] = new JObject
            {
                [CAPACITY_PATH] = 5,
                [SPRITE_PATH] = "sprites/a.png",
            });
            var reversed = ComposeDerived(prefab => prefab["OverrideFields"] = new JObject
            {
                [SPRITE_PATH] = "sprites/a.png",
                [CAPACITY_PATH] = 5,
            });

            Assert.That(forward["Components"].ToString(), Is.EqualTo(reversed["Components"].ToString()));
        }

        [Test]
        public void RemovedFields_DropPropertySoItFallsBackToDeclaredDefault()
        {
            var composed = ComposeDerived(prefab => prefab["RemovedFields"] = new JArray { CAPACITY_PATH });

            Assert.That(Component(composed, "Tavern_GAC").Property("Capacity"), Is.Null);
            Assert.That(Component(composed, "Tavern_GAC")["Name"], Is.Not.Null);
        }

        [Test]
        public void RemovedComponents_DropComponentFromBase()
        {
            var composed = ComposeDerived(prefab => prefab["RemovedComponents"] = new JArray { "Sprite_GAC" });

            Assert.That(TryComponent(composed, "Sprite_GAC"), Is.Null);
            Assert.That(TryComponent(composed, "Tavern_GAC"), Is.Not.Null);
        }

        [Test]
        public void OverrideComponents_AddAbsentAndReplacePresent()
        {
            var composed = ComposeDerived(prefab => prefab["OverrideComponents"] = new JArray
            {
                new JObject { ["$type"] = "Haunted_GAC", ["Intensity"] = 3 },
                new JObject { ["$type"] = "Tavern_GAC", ["Capacity"] = 7 },
            });

            Assert.That(Component(composed, "Haunted_GAC")["Intensity"].Value<int>(), Is.EqualTo(3));
            var tavern = Component(composed, "Tavern_GAC");
            Assert.That(tavern["Capacity"].Value<int>(), Is.EqualTo(7));
            Assert.That(tavern.Property("Name"), Is.Null, "a component override replaces the whole component");
        }

        [Test]
        public void Compose_TakesIdentityFromDerivedDocument()
        {
            var baseKey = Publish("tavern", Tavern(Key("tavern"), capacity: 10));
            var derivedKey = Key("tavern_million");
            var derived = Derived(derivedKey, baseKey);

            var composed = GameAssetDocumentComposer.Compose(derivedKey, derived, Resolve);

            Assert.That(composed["Key"].ToObject<GameAssetKey>(GameAssetJson.JsonSerializer), Is.EqualTo(derivedKey));
            Assert.That(composed["GUID"].Value<string>(), Is.EqualTo(derived["GUID"].Value<string>()));
        }

        [Test]
        public void Compose_DoesNotInheritSourceAssetKey()
        {
            var baseDocument = Tavern(Key("tavern"), capacity: 10);
            baseDocument["SourceAssetKey"] = JObject.FromObject(Key("presentation_source"), GameAssetJson.JsonSerializer);
            var baseKey = Publish("tavern", baseDocument);
            var derivedKey = Key("tavern_million");
            var derived = Derived(derivedKey, baseKey);

            var composed = GameAssetDocumentComposer.Compose(derivedKey, derived, Resolve);

            Assert.That(composed.Property("SourceAssetKey"), Is.Null);
        }

        [Test]
        public void Compose_KeepsOwnSourceAssetKey()
        {
            var baseKey = Publish("tavern", Tavern(Key("tavern"), capacity: 10));
            var derivedKey = Key("tavern_million");
            var derived = Derived(derivedKey, baseKey);
            var ownSource = Key("own_presentation");
            derived["SourceAssetKey"] = JObject.FromObject(ownSource, GameAssetJson.JsonSerializer);

            var composed = GameAssetDocumentComposer.Compose(derivedKey, derived, Resolve);

            Assert.That(
                composed["SourceAssetKey"].ToObject<GameAssetKey>(GameAssetJson.JsonSerializer),
                Is.EqualTo(ownSource));
        }

        [Test]
        public void Compose_DropsDeclaration_SoComposingTwiceIsANoOp()
        {
            var composed = ComposeDerived(prefab => prefab["OverrideFields"] = new JObject
            {
                [CAPACITY_PATH] = 1_000_000,
            });

            Assert.That(composed.Property("Prefab"), Is.Null);
            Assert.That(GameAssetDocumentComposer.HasBase(composed), Is.False);

            var again = GameAssetDocumentComposer.Compose(Key("tavern_million"), composed, Resolve);
            Assert.That(again, Is.SameAs(composed));
            Assert.That(Component(again, "Tavern_GAC")["Capacity"].Value<long>(), Is.EqualTo(1_000_000));
        }

        [Test]
        public void Compose_ReadsLineageFromTheRawDocument()
        {
            var baseKey = Publish("tavern", Tavern(Key("tavern"), capacity: 10));
            var derived = Derived(Key("tavern_million"), baseKey);

            Assert.That(GameAssetDocumentComposer.TryReadBaseKey(derived, out var lineage), Is.True);
            Assert.That(lineage, Is.EqualTo(baseKey));
        }

        [Test]
        public void Compose_DoesNotMutateInputDocuments()
        {
            var baseDocument = Tavern(Key("tavern"), capacity: 10);
            var baseKey = Publish("tavern", baseDocument);
            var baseSnapshot = baseDocument.ToString();
            var derivedKey = Key("tavern_million");
            var derived = Derived(derivedKey, baseKey);
            derived["Prefab"]["OverrideFields"] = new JObject { [CAPACITY_PATH] = 1_000_000 };
            var derivedSnapshot = derived.ToString();

            GameAssetDocumentComposer.Compose(derivedKey, derived, Resolve);

            Assert.That(baseDocument.ToString(), Is.EqualTo(baseSnapshot));
            Assert.That(derived.ToString(), Is.EqualTo(derivedSnapshot));
        }

        [Test]
        public void Compose_ResolvesChainedBases()
        {
            var rootKey = Publish("tavern", Tavern(Key("tavern"), capacity: 10));
            var middleKey = Key("tavern_big");
            var middle = Derived(middleKey, rootKey);
            middle["Prefab"]["OverrideFields"] = new JObject { [CAPACITY_PATH] = 100 };
            middle["Prefab"]["OverrideComponents"] = new JArray
            {
                new JObject { ["$type"] = "Haunted_GAC", ["Intensity"] = 1 },
            };
            _library[middleKey] = middle;
            var leafKey = Key("tavern_big_dark");
            var leaf = Derived(leafKey, middleKey);
            leaf["Prefab"]["OverrideFields"] = new JObject
            {
                [SPRITE_PATH] = "sprites/dark.png",
                ["/Haunted_GAC/Intensity"] = 9,
            };
            leaf["Prefab"]["RemovedFields"] = new JArray { "/Tavern_GAC/Name" };

            var composed = GameAssetDocumentComposer.Compose(leafKey, leaf, Resolve);

            Assert.That(Component(composed, "Tavern_GAC")["Capacity"].Value<int>(), Is.EqualTo(100));
            Assert.That(Component(composed, "Tavern_GAC").Property("Name"), Is.Null);
            Assert.That(Component(composed, "Haunted_GAC")["Intensity"].Value<int>(), Is.EqualTo(9));
            Assert.That(
                Component(composed, "Sprite_GAC")["Visual"]["Resource"]["RelativePath"].Value<string>(),
                Is.EqualTo("sprites/dark.png"));
        }

        [Test]
        public void Compose_RejectsUnpinnedBase()
        {
            var derivedKey = Key("tavern_million");
            var derived = Derived(derivedKey, Key("tavern"));
            derived["Prefab"]["Base"]["Version"] = string.Empty;

            Assert.That(Throws(derivedKey, derived), Does.Contain("exact version"));
        }

        [Test]
        public void Compose_RejectsMissingBase()
        {
            var derivedKey = Key("tavern_million");

            Assert.That(
                Throws(derivedKey, Derived(derivedKey, Key("tavern"))),
                Does.Contain("no mounted module provides"));
        }

        [Test]
        public void Compose_RejectsCycle()
        {
            var firstKey = Key("a");
            var secondKey = Key("b");
            _library[firstKey] = Derived(firstKey, secondKey);
            _library[secondKey] = Derived(secondKey, firstKey);

            Assert.That(Throws(firstKey, _library[firstKey]), Does.Contain("prefab cycle"));
        }

        [Test]
        public void Compose_RejectsSelfReference()
        {
            var key = Key("tavern");

            Assert.That(Throws(key, Derived(key, key)), Does.Contain("its own prefab base"));
        }

        [Test]
        public void Compose_RejectsChainDeeperThanTheLimit()
        {
            var previousKey = Publish("depth0", Tavern(Key("depth0"), capacity: 1));
            for (var index = 1; index <= GameAssetDocumentComposer.MAX_BASE_DEPTH + 1; index++)
            {
                var currentKey = Key($"depth{index}");
                _library[currentKey] = Derived(currentKey, previousKey);
                previousKey = currentKey;
            }

            Assert.That(Throws(previousKey, _library[previousKey]), Does.Contain("maximum prefab depth"));
        }

        [Test]
        public void Compose_RejectsTypeDivergenceFromBase()
        {
            var baseKey = Publish("tavern", Tavern(Key("tavern"), capacity: 10));
            var derivedKey = Key("tavern_level");
            var derived = Derived(derivedKey, baseKey);
            derived["$type"] = "LevelGridAsset";

            Assert.That(Throws(derivedKey, derived), Does.Contain("same concrete type"));
        }

        [Test]
        public void Compose_RejectsPathIntoAnAbsentComponent()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["OverrideFields"] = new JObject
                {
                    ["/Haunted_GAC/Intensity"] = 3,
                }),
                Does.Contain("does not match any 'Haunted_GAC'"));
        }

        [Test]
        public void Compose_RejectsPathIntoAnAbsentField()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["OverrideFields"] = new JObject
                {
                    ["/Tavern_GAC/Missing/Leaf"] = 3,
                }),
                Does.Contain("does not exist in its prefab base"));
        }

        [Test]
        public void Compose_RejectsRemovalOfAnAbsentField()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["RemovedFields"] = new JArray { "/Tavern_GAC/Missing" }),
                Does.Contain("does not provide"));
        }

        [Test]
        public void Compose_RejectsRemovalOfAnAbsentComponent()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["RemovedComponents"] = new JArray { "Haunted_GAC" }),
                Does.Contain("does not provide"));
        }

        [Test]
        [TestCase("/GUID")]
        [TestCase("/Key")]
        [TestCase("/Components")]
        [TestCase("/SourceAssetKey")]
        public void Compose_RejectsPathThatIsNotRootedAtAComponent(string path)
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["OverrideFields"] = new JObject { [path] = 1 }),
                Does.Contain("must start with a component type alias"));
        }

        [Test]
        public void Compose_RejectsPathEndingWithParentSegment()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["OverrideFields"] = new JObject { ["/Tavern_GAC/.."] = 1 }),
                Does.Contain("targets nothing"));
        }

        [Test]
        public void Compose_RejectsPathTargetingAWholeComponent()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["OverrideFields"] = new JObject { ["/Tavern_GAC"] = new JObject() }),
                Does.Contain("targets a whole component"));
        }

        [Test]
        public void Compose_RejectsRelativePath()
        {
            Assert.That(
                ThrowsDerived(prefab => prefab["OverrideFields"] = new JObject { ["Components/Tavern_GAC/Capacity"] = 1 }),
                Does.Contain("must start with"));
        }

        [Test]
        public void Compose_RejectsOverridesWithoutABase()
        {
            var key = Key("tavern_million");
            var document = Tavern(key, capacity: 10);
            document["Prefab"] = new JObject
            {
                ["OverrideFields"] = new JObject { [CAPACITY_PATH] = 1 },
            };

            Assert.That(Throws(key, document), Does.Contain("without a base"));
        }

        [Test]
        public void Compose_RejectsAmbiguousBaseComponents()
        {
            var baseDocument = Tavern(Key("tavern"), capacity: 10);
            ((JArray)baseDocument["Components"]).Add(new JObject { ["$type"] = "Tavern_GAC", ["Capacity"] = 99 });
            var baseKey = Publish("tavern", baseDocument);
            var derivedKey = Key("tavern_million");
            var derived = Derived(derivedKey, baseKey);
            derived["Prefab"]["RemovedComponents"] = new JArray { "Tavern_GAC" };

            Assert.That(Throws(derivedKey, derived), Does.Contain("more than once"));
        }

        private JObject ComposeDerived(Action<JObject> configurePrefab)
        {
            var baseKey = Publish("tavern", Tavern(Key("tavern"), capacity: 10));
            var derivedKey = Key("tavern_million");
            var derived = Derived(derivedKey, baseKey);
            configurePrefab((JObject)derived["Prefab"]);
            return GameAssetDocumentComposer.Compose(derivedKey, derived, Resolve);
        }

        private string ThrowsDerived(Action<JObject> configurePrefab)
        {
            return Assert.Throws<InvalidDataException>(() => ComposeDerived(configurePrefab)).Message;
        }

        private string Throws(GameAssetKey key, JObject document)
        {
            return Assert.Throws<InvalidDataException>(
                () => GameAssetDocumentComposer.Compose(key, document, Resolve)).Message;
        }

        private JObject Resolve(GameAssetKey key)
        {
            return _library.TryGetValue(key, out var document) ? document : null;
        }

        private GameAssetKey Publish(string key, JObject document)
        {
            var assetKey = Key(key);
            _library[assetKey] = document;
            return assetKey;
        }

        private static GameAssetKey Key(string key, string version = "1.0.0")
        {
            return new GameAssetKey(MOD, TYPE, key, version);
        }

        private static JObject Tavern(GameAssetKey key, int capacity)
        {
            return new JObject
            {
                ["$type"] = "GameAsset",
                ["Key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer),
                ["GUID"] = Guid.NewGuid().ToString("N"),
                ["Components"] = new JArray
                {
                    new JObject
                    {
                        ["$type"] = "Tavern_GAC",
                        ["Name"] = "tavern",
                        ["Capacity"] = capacity,
                        ["Slots"] = new JArray("first", "second"),
                    },
                    new JObject
                    {
                        ["$type"] = "Sprite_GAC",
                        ["Visual"] = new JObject
                        {
                            ["Resource"] = new JObject
                            {
                                ["ModuleId"] = "base",
                                ["RelativePath"] = "sprites/tavern.png",
                            },
                            ["PresetId"] = "tile",
                        },
                        ["PixelsPerUnit"] = 32,
                    },
                },
            };
        }

        private static JObject Derived(GameAssetKey key, GameAssetKey baseKey)
        {
            return new JObject
            {
                ["$type"] = "GameAsset",
                ["Key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer),
                ["GUID"] = Guid.NewGuid().ToString("N"),
                ["Prefab"] = new JObject
                {
                    ["Base"] = JObject.FromObject(baseKey, GameAssetJson.JsonSerializer),
                },
            };
        }

        private static JObject Component(JObject document, string type)
        {
            return TryComponent(document, type)
                   ?? throw new AssertionException($"Composed document has no component '{type}'.");
        }

        private static JObject TryComponent(JObject document, string type)
        {
            if (document["Components"] is not JArray components)
                return null;

            foreach (var component in components)
            {
                if (component is JObject typed
                    && string.Equals(typed["$type"]?.Value<string>(), type, StringComparison.Ordinal))
                {
                    return typed;
                }
            }

            return null;
        }
    }
}
#endif
