#if NEWTONSOFT_EXISTS
using System.Collections.Generic;
using System.IO;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class GameAssetDerivedIdentityTests
    {
        private const string SCHEMA = "schema-hash";

        [Test]
        public void SameBaseAndOverrides_ProduceTheSameIdentity()
        {
            var first = GameAssetDerivedIdentity.CreateGuid(Base(), Capacity(1), SCHEMA);
            var second = GameAssetDerivedIdentity.CreateGuid(Base(), Capacity(1), SCHEMA);

            Assert.That(first, Is.EqualTo(second));
            Assert.That(
                GameAssetDerivedIdentity.CreateKey(Base(), Capacity(1), SCHEMA),
                Is.EqualTo(GameAssetDerivedIdentity.CreateKey(Base(), Capacity(1), SCHEMA)));
        }

        [Test]
        public void MemberOrderDoesNotChangeTheIdentity()
        {
            var forward = new GameAssetOverrides
            {
                OverrideFields = new Dictionary<string, JToken>
                {
                    ["/Tavern_GAC/Capacity"] = 1,
                    ["/Sprite_GAC/PixelsPerUnit"] = 32,
                },
                RemovedComponents = new List<string> { "B_GAC", "A_GAC" },
            };
            var reversed = new GameAssetOverrides
            {
                OverrideFields = new Dictionary<string, JToken>
                {
                    ["/Sprite_GAC/PixelsPerUnit"] = 32,
                    ["/Tavern_GAC/Capacity"] = 1,
                },
                RemovedComponents = new List<string> { "A_GAC", "B_GAC" },
            };

            Assert.That(
                GameAssetDerivedIdentity.CreateGuid(Base(), forward, SCHEMA),
                Is.EqualTo(GameAssetDerivedIdentity.CreateGuid(Base(), reversed, SCHEMA)));
        }

        [Test]
        public void NestedMemberOrderDoesNotChangeTheIdentity()
        {
            var forward = FieldValue(new JObject { ["x"] = 1, ["y"] = 2 });
            var reversed = FieldValue(new JObject { ["y"] = 2, ["x"] = 1 });

            Assert.That(
                GameAssetDerivedIdentity.CreateGuid(Base(), forward, SCHEMA),
                Is.EqualTo(GameAssetDerivedIdentity.CreateGuid(Base(), reversed, SCHEMA)));
        }

        [Test]
        public void DifferentOverrideValue_ChangesTheIdentity()
        {
            Assert.That(
                GameAssetDerivedIdentity.CreateGuid(Base(), Capacity(1), SCHEMA),
                Is.Not.EqualTo(GameAssetDerivedIdentity.CreateGuid(Base(), Capacity(2), SCHEMA)));
        }

        [Test]
        public void DifferentSchema_ChangesTheIdentity()
        {
            Assert.That(
                GameAssetDerivedIdentity.CreateGuid(Base(), Capacity(1), SCHEMA),
                Is.Not.EqualTo(GameAssetDerivedIdentity.CreateGuid(Base(), Capacity(1), "other-schema")));
        }

        [Test]
        public void DifferentBaseContent_ChangesTheIdentity()
        {
            var other = new ResolvedGameAssetReference(
                Base().ExactKey,
                Base().AssetGuid,
                "a-different-materialized-content-hash");

            Assert.That(
                GameAssetDerivedIdentity.CreateGuid(Base(), Capacity(1), SCHEMA),
                Is.Not.EqualTo(GameAssetDerivedIdentity.CreateGuid(other, Capacity(1), SCHEMA)));
        }

        [Test]
        public void DerivedKey_StaysInsideTheBaseModuleAndType()
        {
            var key = GameAssetDerivedIdentity.CreateKey(Base(), Capacity(1), SCHEMA);

            Assert.That(key.Mod, Is.EqualTo("base"));
            Assert.That(key.Type, Is.EqualTo("static"));
            Assert.That(key.Version, Is.EqualTo("0.0.0"));
            Assert.That(key.Key, Does.StartWith("tavern_mage#"));
            Assert.That(key.Key, Has.Length.EqualTo("tavern_mage#".Length + GameAssetDerivedIdentity.KEY_SUFFIX_LENGTH));
            Assert.That(GameAssetDerivedIdentity.IsDerived(key), Is.True);
            Assert.That(GameAssetDerivedIdentity.IsDerived(Base().ExactKey), Is.False);
        }

        [Test]
        public void EmptyAndAbsentOverrides_AreTheSameIdentity()
        {
            Assert.That(
                GameAssetDerivedIdentity.CreateGuid(Base(), null, SCHEMA),
                Is.EqualTo(GameAssetDerivedIdentity.CreateGuid(Base(), new GameAssetOverrides(), SCHEMA)));
        }

        [Test]
        public void InexactBase_IsRejected()
        {
            var unresolved = new ResolvedGameAssetReference(Base().ExactKey, Base().AssetGuid, string.Empty);

            Assert.Throws<InvalidDataException>(
                () => GameAssetDerivedIdentity.CreateGuid(unresolved, Capacity(1), SCHEMA));
        }

        private static ResolvedGameAssetReference Base()
        {
            return new ResolvedGameAssetReference(
                new GameAssetKey("base", "static", "tavern_mage", "0.0.0"),
                Hash128.Parse("b57e517db09ecfda3a19b84a5e3bf6b2"),
                "materialized-content-hash");
        }

        private static GameAssetOverrides Capacity(int value)
        {
            return new GameAssetOverrides
            {
                OverrideFields = new Dictionary<string, JToken> { ["/UnitStack_GAC/InitialMemberCount"] = value },
            };
        }

        private static GameAssetOverrides FieldValue(JToken value)
        {
            return new GameAssetOverrides
            {
                OverrideFields = new Dictionary<string, JToken> { ["/Tavern_GAC/Offset"] = value },
            };
        }
    }
}
#endif
