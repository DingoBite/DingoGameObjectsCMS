using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using System.Text.RegularExpressions;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class PrefabGameAssetTestAssets
    {
        public const string TEST_RESOURCE_ROOT = "Assets/AppSDK/DingoGameObjectsCMS/Tests/TestResources";

        public readonly string Folder;

        public PrefabGameAssetTestAssets()
        {
            Folder = TEST_RESOURCE_ROOT + "/PrefabAuthoring_" + Guid.NewGuid().ToString("N");
            EnsureFolder(Folder);
        }

        public GameAssetAuthoring CreatePrefab(string name, IEnumerable<GameAssetComponent> components = null, bool withConverter = false)
        {
            var root = new GameObject(name);
            try
            {
                var authoring = root.AddComponent<GameAssetAuthoring>();
                authoring.SetComponents(components ?? new GameAssetComponent[] { new PrefabAuthoringBeta_GAC() });
                if (withConverter)
                    authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(root.transform, new PrefabAuthoringTransformConverter { Offset = 5 }) });
                return PrefabUtility.SaveAsPrefabAsset(root, Folder + "/" + name + ".prefab").GetComponent<GameAssetAuthoring>();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        public void Dispose()
        {
            var projectPath = Path.GetDirectoryName(Application.dataPath);
            var absoluteRoot = Path.GetFullPath(Path.Combine(projectPath, TEST_RESOURCE_ROOT)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var absoluteFolder = Path.GetFullPath(Path.Combine(projectPath, Folder));
            Assert.That(absoluteFolder.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase), Is.True, "Test cleanup must stay inside its resource root.");
            AssetDatabase.DeleteAsset(Folder);
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;
            var parent = Path.GetDirectoryName(folder).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }

    public class PrefabGameAssetDefinitionsTests
    {
        private PrefabGameAssetTestAssets _assets;
        private PrefabGameAssetIndex _index;
        private PrefabGameAssetDefinitions _definitions;

        [SetUp]
        public void SetUp()
        {
            _assets = new PrefabGameAssetTestAssets();
            _index = ScriptableObject.CreateInstance<PrefabGameAssetIndex>();
        }

        [TearDown]
        public void TearDown()
        {
            _definitions?.Dispose();
            UnityEngine.Object.DestroyImmediate(_index);
            _assets.Dispose();
        }

        [Test]
        public void NativeReferenceResolvesToAutomaticKeyAndSamePrefabGuid()
        {
            var authoring = _assets.CreatePrefab("identity");
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, authoring) });
            var reference = new PrefabGameAssetReference(authoring);

            Assert.That(reference.Authoring, Is.SameAs(authoring));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.That(_definitions.TryResolveReference(reference, out var resolved), Is.True);
            Assert.That(resolved.RequestedKey.Mod, Is.EqualTo("_p_unity"));
            Assert.That(resolved.RequestedKey.Type, Is.EqualTo("prefab"));
            Assert.That(resolved.RequestedKey.Key, Is.EqualTo(guid));
            Assert.That(resolved.RequestedKey.Version, Is.EqualTo("0.0.0"));
            Assert.That(_definitions.TryGetAsset(reference, out var asset), Is.True);
            Assert.That(asset.GUID, Is.EqualTo(Hash128.Parse(guid)));
            Assert.That(asset.Key, Is.EqualTo(resolved.RequestedKey));
            Assert.That(AssetDatabase.Contains(asset), Is.False, "Prepared definitions must not become separate authored assets.");
        }

        [Test]
        public void PreparationClonesManagedValuesAndPreservesUnityResourceReferences()
        {
            var resource = new Texture2D(2, 2);
            var resourcePath = _assets.Folder + "/resource.asset";
            AssetDatabase.CreateAsset(resource, resourcePath);
            var manual = new PrefabAuthoringAlpha_GAC { Value = 10, Values = new List<int> { 1, 2 }, Resource = resource };
            var authoring = _assets.CreatePrefab("snapshot", new[] { manual });
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, authoring) });
            var reference = new PrefabGameAssetReference(authoring);

            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.That(_definitions.TryGetAsset(reference, out var asset), Is.True);
            var prepared = (PrefabAuthoringAlpha_GAC)asset.Components.Single();
            var source = (PrefabAuthoringAlpha_GAC)authoring.Components.Single();
            Assert.That(prepared, Is.Not.SameAs(source));
            Assert.That(prepared.Values, Is.Not.SameAs(source.Values));
            var expectedResource = AssetDatabase.LoadAssetAtPath<Texture2D>(resourcePath);
            Assert.That(source.Resource.GetEntityId(), Is.EqualTo(expectedResource.GetEntityId()));
            Assert.That(prepared.Resource.GetEntityId(), Is.EqualTo(expectedResource.GetEntityId()), "Cloning the definition must preserve the native Unity resource identity.");
            Assert.That(AssetDatabase.GetAssetPath(prepared.Resource), Is.EqualTo(resourcePath));
            Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prepared.Resource)), Is.EqualTo(AssetDatabase.AssetPathToGUID(resourcePath)));
            source.Value = 99;
            source.Values[0] = 99;
            Assert.That(prepared.Value, Is.EqualTo(10));
            Assert.That(prepared.Values, Is.EqualTo(new[] { 1, 2 }));

            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var second, out error), Is.True, error);
            try
            {
                Assert.That(second.TryGetAsset(reference, out var secondAsset), Is.True);
                var secondPrepared = (PrefabAuthoringAlpha_GAC)secondAsset.Components.Single();
                Assert.That(secondPrepared.Value, Is.EqualTo(99));
                Assert.That(secondPrepared.Values[0], Is.EqualTo(99));
                Assert.That(secondPrepared, Is.Not.SameAs(prepared));
            }
            finally
            {
                second.Dispose();
            }
            Assert.That(prepared.Value, Is.EqualTo(10));
        }

        [Test]
        public void ConvertedValuesAreFrozenUntilNextPreparation()
        {
            var authoring = _assets.CreatePrefab("converted", withConverter: true);
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, authoring) });
            var reference = new PrefabGameAssetReference(authoring);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.That(_definitions.TryGetAsset(reference, out var asset), Is.True);
            var prepared = asset.Components.OfType<PrefabAuthoringAlpha_GAC>().Single();
            ((PrefabAuthoringTransformConverter)authoring.Converters.Single().Converter).Offset = 50;
            authoring.transform.localPosition = Vector3.right * 100;
            Assert.That(prepared.Value, Is.EqualTo(5));
        }

        [Test]
        public void DisposeReleasesOnlyOwnedDefinitionsAndRejectsFurtherResolution()
        {
            var authoring = _assets.CreatePrefab("disposal");
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, authoring) });
            var reference = new PrefabGameAssetReference(authoring);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.That(_definitions.TryGetAsset(reference, out var asset), Is.True);
            _definitions.Dispose();
            _definitions.Dispose();
            Assert.That(asset == null, Is.True);
            Assert.That(authoring != null, Is.True);
            Assert.That(_index != null, Is.True);
            Assert.That(_definitions.TryGetAsset(reference, out _), Is.False);
            Assert.That(_definitions.TryResolveReference(reference, out _), Is.False);
        }

        [Test]
        public void FailedPreparationDoesNotLeakPartiallyBuiltScriptableObjects()
        {
            var prefabs = new[] { _assets.CreatePrefab("first"), _assets.CreatePrefab("second") }.OrderBy(authoring => AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring)), StringComparer.Ordinal).ToArray();
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefabs[0])), prefabs[0]), new PrefabGameAssetIndexEntry("not-a-guid", prefabs[1]) });
            var before = Resources.FindObjectsOfTypeAll<GameAsset>().Select(asset => asset.GetEntityId()).ToArray();
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.False);
            Assert.That(_definitions, Is.Null);
            Assert.That(error, Does.Contain("not-a-guid"));
            var after = Resources.FindObjectsOfTypeAll<GameAsset>().Select(asset => asset.GetEntityId()).ToArray();
            Assert.That(after, Is.EquivalentTo(before));
        }

        [Test]
        public void InvalidContentKeepsItsIdentityAndDoesNotDiscardHealthyDefinitionsOrResources()
        {
            var resource = new Texture2D(2, 2);
            AssetDatabase.CreateAsset(resource, _assets.Folder + "/healthy-resource.asset");
            var healthy = _assets.CreatePrefab("healthy", new[] { new PrefabAuthoringAlpha_GAC { Value = 17, Resource = resource } });
            var broken = _assets.CreatePrefab("broken", new GameAssetComponent[] { new PrefabAuthoringBeta_GAC(), new PrefabAuthoringBeta_GAC() });
            var brokenGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(broken));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(healthy)), healthy, "healthy-hash"), new PrefabGameAssetIndexEntry(brokenGuid, broken, "broken-hash") });
            LogAssert.Expect(LogType.Error, new Regex("Prefab 'broken'.*authored more than once.*Using an empty GameAsset definition"));

            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.That(error, Is.Null);
            Assert.That(_definitions.TryGetAsset(broken, out var fallback), Is.True);
            Assert.That(fallback.Key, Is.EqualTo(new GameAssetKey(PrefabGameAssetDefinitions.MOD, PrefabGameAssetDefinitions.TYPE, brokenGuid, PrefabGameAssetDefinitions.VERSION)));
            Assert.That(fallback.GUID, Is.EqualTo(Hash128.Parse(brokenGuid)));
            Assert.That(fallback.Components, Is.Empty);
            Assert.That(_definitions.TryGetAsset(healthy, out var healthyAsset), Is.True);
            var value = healthyAsset.Components.OfType<PrefabAuthoringAlpha_GAC>().Single();
            Assert.That(value.Value, Is.EqualTo(17));
            Assert.That(value.Resource.GetEntityId(), Is.EqualTo(resource.GetEntityId()));
            Assert.That(_definitions.ResourceAssets, Is.EquivalentTo(new[] { fallback, healthyAsset }));

            var templates = new GameAssetTemplateCache(new RuntimePatchCodecRegistry("authoring-content-fallback"), RuntimeTemplatePatchCodecContext.Instance);
            try
            {
                var library = GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), _definitions);
                Assert.That(library.TryGet(fallback.Key, out var entry), Is.True);
                Assert.That(entry.ResolvedGuid, Is.EqualTo(fallback.GUID));
                var runtime = templates.Materialize(GameAssetInstanceFactory.Create(new GameAssetReference(fallback.Key), Hash128.Compute("inert-fallback-instance")), library);
                Assert.That(runtime.Components, Is.Empty);
                Assert.That(runtime.Origin.Asset.ExactKey, Is.EqualTo(fallback.Key));
            }
            finally
            {
                templates.Clear();
            }
            _definitions.Dispose();
            Assert.That(fallback == null, Is.True);
            Assert.That(healthyAsset == null, Is.True);
            Assert.That(broken != null && healthy != null && resource != null, Is.True);
        }

        [Test]
        public void InvalidContentFallbackRemainsDeterministicAndItsSourceHashStillInvalidatesTheCatalog()
        {
            var broken = _assets.CreatePrefab("broken-hash", new GameAssetComponent[] { new PrefabAuthoringBeta_GAC(), new PrefabAuthoringBeta_GAC() });
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(broken));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, broken, "first-content-hash") });
            LogAssert.Expect(LogType.Error, new Regex("Prefab 'broken-hash'.*Using an empty GameAsset definition"));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            var frozenHash = _definitions.NativeContentHash;
            Assert.That(_definitions.TryGetAsset(broken, out var first), Is.True);
            LogAssert.Expect(LogType.Error, new Regex("Prefab 'broken-hash'.*Using an empty GameAsset definition"));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var repeated, out error), Is.True, error);
            try
            {
                Assert.That(repeated.NativeContentHash, Is.EqualTo(frozenHash));
                Assert.That(repeated.TryGetAsset(broken, out var second), Is.True);
                Assert.That(second.Key, Is.EqualTo(first.Key));
                Assert.That(second.GUID, Is.EqualTo(first.GUID));
                Assert.That(second.Components, Is.Empty);
            }
            finally
            {
                repeated.Dispose();
            }
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, broken, "second-content-hash") });
            LogAssert.Expect(LogType.Error, new Regex("Prefab 'broken-hash'.*Using an empty GameAsset definition"));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var changed, out error), Is.True, error);
            try
            {
                Assert.That(changed.NativeContentHash, Is.Not.EqualTo(frozenHash));
                Assert.That(_definitions.NativeContentHash, Is.EqualTo(frozenHash));
            }
            finally
            {
                changed.Dispose();
            }
        }

        [Test]
        public void InvalidOrRepeatedIndexIdentitiesAreRejectedAtomically()
        {
            var first = _assets.CreatePrefab("index-first");
            var second = _assets.CreatePrefab("index-second");
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(first));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry("not-a-guid", first) });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.False);
            Assert.That(_definitions, Is.Null);
            Assert.That(error, Does.Contain("not-a-guid"));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, first), new PrefabGameAssetIndexEntry(guid, second) });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out error), Is.False);
            Assert.That(_definitions, Is.Null);
            Assert.That(error, Does.Contain(guid));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, null) });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out error), Is.False);
            Assert.That(_definitions, Is.Null);
            Assert.That(error, Is.Not.Empty);
        }

        [Test]
        public void UnknownAndMissingPrefabReferencesDoNotResolve()
        {
            var known = _assets.CreatePrefab("known");
            var unknown = _assets.CreatePrefab("unknown");
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(known)), known) });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.That(_definitions.TryGetAsset(new PrefabGameAssetReference(unknown), out _), Is.False);
            Assert.That(_definitions.TryResolveReference(default(PrefabGameAssetReference), out _), Is.False);
        }
    }
}
