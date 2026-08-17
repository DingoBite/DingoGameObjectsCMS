#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
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
    public class GameAssetDerivedMaterializationTests
    {
        private static readonly GameAssetKey BASE_KEY = new("base", "static", "dummy", "0.0.0");

        private RuntimePatchCodecRegistry _registry;
        private GameAssetTemplateCache _cache;
        private GameAsset _baseAsset;
        private GameAsset _derivedAsset;
        private GameAssetTemplateBlueprint _baseBlueprint;
        private readonly List<GameAsset> _owned = new();

        [SetUp]
        public void SetUp()
        {
            _registry = RuntimePatchEditorEnvironment.CreateRegistry();
            _cache = new GameAssetTemplateCache(_registry, RuntimeTemplatePatchCodecContext.Instance);
            _baseAsset = CreateAsset(BASE_KEY, IdUtils.NewHash128FromGuid(), maximum: 100);
            _baseBlueprint = _cache.GetOrCreate(new GameAssetReference(BASE_KEY), _baseAsset);
        }

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
        public void RegisteredDerivedAsset_IsResolvedByItsBaseAndOverrides()
        {
            var overrides = Maximum(1);

            var registered = RegisterDerived(overrides);
            var resolved = _cache.ResolveDerivedStrict(_baseBlueprint.Asset, overrides);

            Assert.That(resolved, Is.SameAs(registered));
            Assert.That(
                resolved.Asset.MaterializedContentHash,
                Is.Not.EqualTo(_baseBlueprint.Asset.MaterializedContentHash));
        }

        [Test]
        public void UnregisteredOverrides_AreRejectedRatherThanComposedOnTheFly()
        {
            var exception = Assert.Throws<InvalidOperationException>(
                () => _cache.ResolveDerivedStrict(_baseBlueprint.Asset, Maximum(1)));

            Assert.That(exception.Message, Does.Contain("is not registered in this session"));
        }

        [Test]
        public void RegisterDerived_RejectsAnAssetWhoseIdentityDoesNotMatchItsOverrides()
        {
            var overrides = Maximum(1);
            var impostor = CreateAsset(
                GameAssetDerivedIdentity.CreateKey(_baseBlueprint.Asset, overrides, _registry.SchemaHash),
                IdUtils.NewHash128FromGuid(),
                maximum: 1);

            var exception = Assert.Throws<InvalidOperationException>(
                () => _cache.RegisterDerived(_baseBlueprint.Asset, overrides, impostor));

            Assert.That(exception.Message, Does.Contain("resolve to"));
        }

        [Test]
        public void InstanceWithOverrides_MaterializesFromTheDerivedAsset()
        {
            var overrides = Maximum(1);
            var derived = RegisterDerived(overrides);
            var instance = Instance().WithOverrides(overrides);

            var runtimeObject = _cache.Materialize(instance, BuildLock(derived));

            Assert.That(runtimeObject.TakeRO<HitPoints_GRC>().Maximum, Is.EqualTo(1), "overridden by the placement");
            Assert.That(runtimeObject.Has<DamageImmunity_GRC>(), Is.True, "inherited from the base");
            Assert.That(runtimeObject.Key, Is.EqualTo(derived.Asset.ExactKey));
            Assert.That(runtimeObject.AssetGUID, Is.EqualTo(derived.Asset.AssetGuid));
        }

        [Test]
        public void InstanceWithoutOverrides_StillMaterializesFromItsOwnAsset()
        {
            var runtimeObject = _cache.Materialize(Instance(), BuildLock());

            Assert.That(runtimeObject.TakeRO<HitPoints_GRC>().Maximum, Is.EqualTo(100));
            Assert.That(runtimeObject.Key, Is.EqualTo(BASE_KEY));
        }

        private GameAssetInstance Instance()
        {
            return new GameAssetInstance(
                RuntimeInstanceIdentity.Next(),
                new GameAssetReference(BASE_KEY),
                (RuntimeObjectPatch)null);
        }

        private GameAssetTemplateBlueprint RegisterDerived(GameAssetOverrides overrides)
        {
            // The composition step the library will own once authored overrides
            // are registered while the session is sealed.
            var composed = GameAssetDocumentComposer.ApplyOverrides(
                BASE_KEY,
                JObject.Parse(_baseAsset.ToJson()),
                overrides);
            var key = GameAssetDerivedIdentity.CreateKey(_baseBlueprint.Asset, overrides, _registry.SchemaHash);
            var guid = GameAssetDerivedIdentity.CreateGuid(_baseBlueprint.Asset, overrides, _registry.SchemaHash);
            composed["Key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer);
            composed["GUID"] = guid.ToString();

            _derivedAsset = (GameAsset)GameAssetJson.FromJObject(composed);
            _owned.Add(_derivedAsset);
            return _cache.RegisterDerived(_baseBlueprint.Asset, overrides, _derivedAsset);
        }

        /// <summary>
        /// A derived asset is a first-class lock entry, because the runtime
        /// object takes its catalog indices from the lock. This models what
        /// registering authored overrides at session seal will produce.
        /// </summary>
        private GameAssetLibraryLock BuildLock(params GameAssetTemplateBlueprint[] derived)
        {
            var assetLock = new GameAssetLibraryLock();
            assetLock.Set(BASE_KEY, new GameAssetLibraryLockEntry(
                BASE_KEY,
                _baseAsset.GUID,
                _baseBlueprint.Asset.MaterializedContentHash,
                _baseAsset));
            for (var index = 0; index < derived.Length; index++)
            {
                var blueprint = derived[index];
                assetLock.Set(blueprint.Asset.ExactKey, new GameAssetLibraryLockEntry(
                    blueprint.Asset.ExactKey,
                    blueprint.Asset.AssetGuid,
                    blueprint.Asset.MaterializedContentHash,
                    _derivedAsset));
            }

            return assetLock.Seal();
        }

        private GameAsset CreateAsset(GameAssetKey key, Hash128 guid, int maximum)
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            asset.ResetToDefault(key, guid);
            asset.SetComponents(new GameAssetComponent[]
            {
                new HitPoints_GAC { Maximum = maximum },
                new DamageImmunity_GAC(),
            });
            _owned.Add(asset);
            return asset;
        }

        private static GameAssetOverrides Maximum(int value)
        {
            return new GameAssetOverrides
            {
                OverrideFields = new Dictionary<string, JToken> { ["/HitPoints_GAC/Maximum"] = value },
            };
        }
    }
}
#endif
