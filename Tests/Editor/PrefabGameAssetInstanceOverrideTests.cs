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
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

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

        [TestCase(null)]
        [TestCase("")]
        [TestCase("null")]
        [TestCase("{}")]
        public void MissingValueOverridesResolveToBaseWithoutDiscardingHealthyOverrides(string json)
        {
            var broken = new PrefabGameAssetOverrideEntry("invalid-json", _source, new[] { new PrefabAuthoringAlpha_GAC { Value = 99 } }, json, "broken-content-hash");
            AssertBaseFallbackWithHealthyOverride(broken);
        }

        [Test]
        public void MalformedJsonReportsTheExceptionAndResolvesToBase()
        {
            var broken = new PrefabGameAssetOverrideEntry("invalid-json", _source, new[] { new PrefabAuthoringAlpha_GAC { Value = 99 } }, "{", "broken-json-hash");
            LogAssert.Expect(LogType.Exception, new Regex("JsonSerializationException|JsonReaderException"));
            AssertBaseFallbackWithHealthyOverride(broken);
        }

        [TestCase("{\"OverrideComponents\":[null]}", false)]
        [TestCase("{\"OverrideComponents\":[{\"Value\":99}]}", false)]
        [TestCase("{\"OverrideComponents\":[{\"$type\":\"Unknown_GAC\",\"Value\":99}]}", false)]
        [TestCase("{\"OverrideComponents\":[{\"$type\":[]}]}", false)]
        [TestCase("{\"OverrideComponents\":[{\"$type\":\"PrefabAuthoringAlpha_GAC\"},{\"$type\":\"PrefabAuthoringAlpha_GAC\"}]}", false)]
        [TestCase("{\"OverrideComponents\":[{\"$type\":\"PrefabAuthoringAlpha_GAC\",\"UnknownField\":99}]}", false)]
        [TestCase("{\"OverrideFields\":{\"/PrefabAuthoringAlpha_GAC/UnknownField\":99}}", false)]
        [TestCase("{\"OverrideFields\":{\"/PrefabAuthoringAlpha_GAC/Value/InvalidChild\":99}}", true)]
        [TestCase("{\"OverrideFields\":{\"/Unknown_GAC/Value\":99}}", true)]
        [TestCase("{\"OverrideFields\":{\"/PrefabAuthoringAlpha_GAC/$type\":[]}}", false)]
        [TestCase("{\"OverrideFields\":{\"/PrefabAuthoringAlpha_GAC/$type\":123}}", false)]
        [TestCase("{\"RemovedFields\":[\"/PrefabAuthoringAlpha_GAC/$type\"]}", false)]
        [TestCase("{\"RemovedFields\":[null]}", true)]
        [TestCase("{\"RemovedComponents\":[null]}", true)]
        [TestCase("{\"RemovedComponents\":[\"PrefabAuthoringAlpha_GAC\"]}", false)]
        public void MalformedValueOverrideStructureResolvesToBaseAndKeepsHealthyOverride(string json, bool reportsException)
        {
            var broken = new PrefabGameAssetOverrideEntry("invalid-value-structure", _source, new[] { new PrefabAuthoringAlpha_GAC { Value = 99 } }, json, "broken-value-structure-hash");
            if (reportsException)
                LogAssert.Expect(LogType.Exception, new Regex("InvalidDataException"));
            AssertBaseFallbackWithHealthyOverride(broken);
        }

        [Test]
        public void FullComponentOverrideKeepsNativeResourceFingerprintAndFrozenResource()
        {
            var resource = new Texture2D(2, 2);
            var resourcePath = _assets.Folder + "/override-resource.asset";
            AssetDatabase.CreateAsset(resource, resourcePath);
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(resource, out string resourceGuid, out long resourceId);
            _source.SetComponents(new[] { new PrefabAuthoringAlpha_GAC { Value = 5, Resource = resource } });
            var changed = new JObject
            {
                ["$type"] = nameof(PrefabAuthoringAlpha_GAC),
                [nameof(PrefabAuthoringAlpha_GAC.Value)] = 25,
                [nameof(PrefabAuthoringAlpha_GAC.Resource)] = new JObject { ["UnityAssetGuid"] = resourceGuid, ["LocalFileId"] = resourceId }
            };
            var json = GameAssetDocumentComposer.CanonicalizeOverrides(new GameAssetOverrides { OverrideComponents = new List<JObject> { changed } });
            _index.ReplaceOverrides(new[] { new PrefabGameAssetOverrideEntry("native-resource", _source, new[] { new PrefabAuthoringAlpha_GAC { Value = 25, Resource = resource } }, json, "native-resource-content-hash") });
            var instance = Instance("native-resource", 25);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            var templates = Templates();
            try
            {
                var library = GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), _definitions);
                Assert.That(_definitions.TryGetAsset(instance, out var derived), Is.True);
                var value = derived.Components.OfType<PrefabAuthoringAlpha_GAC>().Single();
                Assert.That(value.Value, Is.EqualTo(25));
                Assert.That(value.Resource.GetEntityId(), Is.EqualTo(resource.GetEntityId()));
                Assert.That(_definitions.TryResolveReference(instance, out var reference), Is.True);
                Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.True);
                Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(2));
            }
            finally
            {
                templates.Clear();
            }
        }

        [Test]
        public void RemovedFieldOverrideRetainsDeclaredDefaultSemantics()
        {
            var json = GameAssetDocumentComposer.CanonicalizeOverrides(new GameAssetOverrides { RemovedFields = new List<string> { "/" + nameof(PrefabAuthoringAlpha_GAC) + "/" + nameof(PrefabAuthoringAlpha_GAC.Value) } });
            _index.ReplaceOverrides(new[] { new PrefabGameAssetOverrideEntry("removed-field", _source, new[] { new PrefabAuthoringAlpha_GAC() }, json, "removed-field-content-hash") });
            var instance = Instance("removed-field", 0);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            var templates = Templates();
            try
            {
                var library = GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), _definitions);
                Assert.That(_definitions.TryResolveReference(instance, out var reference), Is.True);
                Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.True);
                var runtime = templates.Materialize(GameAssetInstanceFactory.Create(reference, Hash128.Compute("removed-field-runtime")), library);
                Assert.That(runtime.Origin.Asset.ExactKey, Is.EqualTo(reference.RequestedKey));
                Assert.That(_definitions.TryGetAsset(instance, out var derived), Is.True);
                Assert.That(derived.Components.OfType<PrefabAuthoringAlpha_GAC>().Single().Value, Is.EqualTo(new PrefabAuthoringAlpha_GAC().Value));
            }
            finally
            {
                templates.Clear();
            }
        }

        [TestCase("null")]
        [TestCase("duplicate")]
        [TestCase("missing")]
        [TestCase("extra")]
        public void InvalidSnapshotCompositionResolvesToBaseWithoutDiscardingHealthyOverrides(string scenario)
        {
            GameAssetComponent[] components = scenario switch
            {
                "null" => new GameAssetComponent[] { null },
                "duplicate" => new GameAssetComponent[] { new PrefabAuthoringAlpha_GAC { Value = 99 }, new PrefabAuthoringAlpha_GAC() },
                "missing" => Array.Empty<GameAssetComponent>(),
                _ => new GameAssetComponent[] { new PrefabAuthoringAlpha_GAC { Value = 99 }, new PrefabAuthoringBeta_GAC() }
            };
            var broken = new PrefabGameAssetOverrideEntry("invalid-structure", _source, components, OverrideJson(99), "broken-snapshot-hash");
            AssertBaseFallbackWithHealthyOverride(broken);
        }

        [Test]
        public void OverrideCannotRestoreComponentsOfAnInvalidBaseDefinition()
        {
            _source.SetComponents(new GameAssetComponent[] { new PrefabAuthoringAlpha_GAC(), new PrefabAuthoringAlpha_GAC() });
            _index.ReplaceOverrides(new[] { Override("invalid-base", 99) });
            var instance = Instance("invalid-base", 99);
            LogAssert.Expect(LogType.Error, new Regex("Prefab 'override-source'.*Using an empty GameAsset definition"));
            LogAssert.Expect(LogType.Error, new Regex("Prefab instance override 'invalid-base'.*Using base GameAsset definition"));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.That(_definitions.TryGetAsset(_source, out var fallback), Is.True);
            Assert.That(_definitions.TryGetAsset(instance, out var resolved), Is.True);
            Assert.That(resolved, Is.SameAs(fallback));
            Assert.That(resolved.Components, Is.Empty);
            var templates = Templates();
            try
            {
                var library = GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), _definitions);
                Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(1));
                Assert.That(_definitions.TryResolveReference(instance, out var reference), Is.True);
                Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.False);
            }
            finally
            {
                templates.Clear();
            }
        }

        private void AssertBaseFallbackWithHealthyOverride(PrefabGameAssetOverrideEntry broken)
        {
            _index.ReplaceOverrides(new[] { broken, Override("healthy-instance", 25) });
            var invalidInstance = Instance(broken.Id, 99);
            var healthyInstance = Instance("healthy-instance", 25);
            LogAssert.Expect(LogType.Error, new Regex("Prefab instance override '" + broken.Id + "'.*Using base GameAsset definition"));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.That(error, Is.Null);
            var templates = Templates();
            try
            {
                var library = GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), _definitions);
                Assert.That(_definitions.TryGetAsset(_source, out var baseAsset), Is.True);
                Assert.That(_definitions.TryGetAsset(invalidInstance, out var fallback), Is.True);
                Assert.That(fallback, Is.SameAs(baseAsset));
                Assert.That(((PrefabAuthoringAlpha_GAC)fallback.Components.Single()).Value, Is.EqualTo(5));
                Assert.That(_definitions.TryResolveReference(invalidInstance, out var reference), Is.True);
                Assert.That(reference.RequestedKey.Key, Is.EqualTo(_sourceGuid));
                Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.False);
                Assert.That(_definitions.TryGetAsset(healthyInstance, out var healthy), Is.True);
                Assert.That(((PrefabAuthoringAlpha_GAC)healthy.Components.Single()).Value, Is.EqualTo(25));
                Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(2));
                Assert.That(((PrefabAuthoringAlpha_GAC)_source.Components.Single()).Value, Is.EqualTo(5));
                _definitions.Dispose();
                Assert.That(fallback == null && healthy == null, Is.True);
                Assert.That(_source != null, Is.True);
            }
            finally
            {
                templates.Clear();
            }
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
