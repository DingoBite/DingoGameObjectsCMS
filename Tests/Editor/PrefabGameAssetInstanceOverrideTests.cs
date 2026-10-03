#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class PrefabGameAssetInstanceOverrideTests
    {
        private PrefabGameAssetTestAssets _assets;
        private PrefabGameAssetIndex _index;
        private PrefabGameAssetDefinitions _definitions;
        private GameAssetAuthoring _source;
        private string _sourceGuid;
        private readonly List<GameObject> _instances = new();

        [SetUp]
        public void SetUp()
        {
            _assets = new PrefabGameAssetTestAssets();
            _index = ScriptableObject.CreateInstance<PrefabGameAssetIndex>();
            _source = _assets.CreatePrefab("override-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            _sourceGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_source));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(_sourceGuid, _source, "base-content-hash") });
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var instance in _instances)
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
            _instances.Clear();
            _definitions?.Dispose();
            UnityEngine.Object.DestroyImmediate(_index);
            _assets.Dispose();
        }

        [Test]
        public void ValueOverrideIsRegisteredBeforeSealAndLeavesSharedPrefabDefinitionUntouched()
        {
            _index.ReplaceOverrides(new[] { Override("scene-price", 25) });
            var instance = Instance("scene-price", 25);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            var templates = Templates();
            var library = GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), _definitions);

            Assert.That(_definitions.TryResolveReference(instance, out var reference), Is.True);
            Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.True);
            Assert.That(_definitions.TryGetAsset(instance, out var derived), Is.True);
            Assert.That(((PrefabAuthoringAlpha_GAC)derived.Components.Single()).Value, Is.EqualTo(25));
            Assert.That(_definitions.TryGetAsset(_source, out var original), Is.True);
            Assert.That(((PrefabAuthoringAlpha_GAC)original.Components.Single()).Value, Is.EqualTo(5));
            Assert.That(((PrefabAuthoringAlpha_GAC)_source.Components.Single()).Value, Is.EqualTo(5));
            Assert.That(derived.GUID, Is.Not.EqualTo(original.GUID));
            Assert.That(library.TryGet(reference.RequestedKey, out var entry), Is.True);
            Assert.That(entry.ResolvedGuid, Is.EqualTo(derived.GUID));
            var runtime = templates.Materialize(GameAssetInstanceFactory.Create(reference, Hash128.Compute("scene-override-runtime")), library);
            Assert.That(runtime.Origin.Asset.ExactKey, Is.EqualTo(derived.Key));
            Assert.That(runtime.Origin.Asset.AssetGuid, Is.EqualTo(derived.GUID));
            Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(2));
        }

        [Test]
        public void EqualOverridesOnDifferentInstancesShareStableDerivedCatalogIdentity()
        {
            _index.ReplaceOverrides(new[] { Override("first-scene-instance", 25), Override("second-scene-instance", 25) });
            var first = Instance("first-scene-instance", 25);
            var second = Instance("second-scene-instance", 25);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            var library = GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), _definitions);
            Assert.That(_definitions.TryResolveReference(first, out var firstReference), Is.True);
            Assert.That(_definitions.TryResolveReference(second, out var secondReference), Is.True);
            Assert.That(firstReference.RequestedKey, Is.EqualTo(secondReference.RequestedKey));
            Assert.That(_definitions.TryGetAsset(first, out var firstAsset), Is.True);
            Assert.That(_definitions.TryGetAsset(second, out var secondAsset), Is.True);
            Assert.That(firstAsset.GUID, Is.EqualTo(secondAsset.GUID));
            Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(2));

            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var repeated, out error), Is.True, error);
            try
            {
                var repeatedLibrary = GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), repeated);
                Assert.That(repeated.TryResolveReference(first, out var repeatedReference), Is.True);
                Assert.That(repeatedReference.RequestedKey, Is.EqualTo(firstReference.RequestedKey));
                Assert.That(RuntimeSessionAssetCatalog.FromLock(repeatedLibrary).ManifestEntries.Count, Is.EqualTo(2));
            }
            finally
            {
                repeated.Dispose();
            }
        }

        [Test]
        public void PreparedOverrideSnapshotsRemainIndependentOfLaterIndexChanges()
        {
            var values = new PrefabAuthoringAlpha_GAC { Value = 25 };
            _index.ReplaceOverrides(new[] { new PrefabGameAssetOverrideEntry("frozen-instance", _source, new[] { values }, OverrideJson(25), "override-content-hash") });
            var instance = Instance("frozen-instance", 25);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            var frozenHash = _definitions.NativeContentHash;
            values.Value = 99;
            _index.ReplaceOverrides(Array.Empty<PrefabGameAssetOverrideEntry>());
            GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), _definitions);
            Assert.That(_definitions.NativeContentHash, Is.EqualTo(frozenHash));
            Assert.That(_definitions.TryGetAsset(instance, out var derived), Is.True);
            Assert.That(((PrefabAuthoringAlpha_GAC)derived.Components.Single()).Value, Is.EqualTo(25));
        }

        [Test]
        public void DisposalReleasesRegisteredDerivedDefinitionsAndRejectsInstanceResolution()
        {
            _index.ReplaceOverrides(new[] { Override("disposed-instance", 25) });
            var instance = Instance("disposed-instance", 25);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), _definitions);
            Assert.That(_definitions.TryGetAsset(instance, out var derived), Is.True);
            _definitions.Dispose();
            Assert.That(derived == null, Is.True);
            Assert.That(_source != null, Is.True);
            Assert.That(_definitions.TryResolveReference(instance, out _), Is.False);
            Assert.That(_definitions.TryGetAsset(instance, out _), Is.False);
        }

        private PrefabGameAssetOverrideEntry Override(string id, int value)
        {
            return new PrefabGameAssetOverrideEntry(id, _source, new[] { new PrefabAuthoringAlpha_GAC { Value = value } }, OverrideJson(value), "override-content-hash-" + value);
        }

        private GameAssetAuthoring Instance(string id, int value)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(_source.gameObject);
            _instances.Add(instance);
            var authoring = instance.GetComponent<GameAssetAuthoring>();
            authoring.SetComponents(new[] { new PrefabAuthoringAlpha_GAC { Value = value } });
            authoring.SetRuntimeDefinitionSource(_sourceGuid, id);
            return authoring;
        }

        private static string OverrideJson(int value)
        {
            var overrides = new GameAssetOverrides { OverrideFields = new Dictionary<string, JToken> { ["/" + nameof(PrefabAuthoringAlpha_GAC) + "/" + nameof(PrefabAuthoringAlpha_GAC.Value)] = value } };
            return JObject.FromObject(overrides, GameAssetJson.JsonSerializer).ToString(Formatting.None);
        }

        private static GameAssetTemplateCache Templates()
        {
            return new GameAssetTemplateCache(new RuntimePatchCodecRegistry("native-scene-override-tests"), RuntimeTemplatePatchCodecContext.Instance);
        }
    }
}
#endif
