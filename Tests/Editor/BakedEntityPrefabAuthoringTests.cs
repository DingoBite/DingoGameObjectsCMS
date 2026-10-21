using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Editor;
using DingoGameObjectsCMS.Editor.Authoring;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using EntityHash128 = Unity.Entities.Hash128;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class BakedEntityPrefabAuthoringTests
    {
        private PrefabGameAssetTestAssets _assets;
        private PrefabGameAssetDefinitions _definitions;
        private string _previousIndexPath;
        private Func<string, bool> _previousInclude;
        private Func<IReadOnlyList<GameAssetComponent>, IEnumerable<string>> _previousDependencies;
        private IReadOnlyList<string> _previousScenes;

        [SetUp]
        public void SetUp()
        {
            _assets = new PrefabGameAssetTestAssets();
            _previousIndexPath = PrefabGameAssetIndexConfiguration.IndexAssetPath;
            _previousInclude = PrefabGameAssetIndexConfiguration.IncludeAssetPath;
            _previousDependencies = PrefabGameAssetIndexConfiguration.ResourceDependencies;
            _previousScenes = BakedEntityPrefabLibraryBuildConfiguration.ScenePaths;
            PrefabGameAssetIndexConfiguration.Configure(_assets.Folder + "/NativeIndex.asset", path => path.StartsWith(_assets.Folder + "/", StringComparison.Ordinal));
        }

        [TearDown]
        public void TearDown()
        {
            _definitions?.Dispose();
            PrefabGameAssetIndexConfiguration.Configure(_previousIndexPath, _previousInclude, _previousDependencies);
            BakedEntityPrefabLibraryBuildConfiguration.Configure(_previousScenes);
            _assets.Dispose();
        }

        [Test]
        public void MarkerProducesNativeDescriptorAndOrdinaryReferenceWithoutLiveEntity()
        {
            var authoring = CreateTemplate("technical");
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var expectedGuid = new EntityHash128(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring)));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out _definitions, out var error), Is.True, error);
            Assert.That(_definitions.TryResolveReference(new PrefabGameAssetReference(authoring), out var reference), Is.True);
            Assert.That(reference.RequestedKey.Mod, Is.EqualTo(PrefabGameAssetDefinitions.MOD));
            Assert.That(_definitions.TryGetAsset(new PrefabGameAssetReference(authoring), out var definition), Is.True);
            var descriptor = definition.Components.OfType<BakedEntityPrefab_GAC>().Single();
            Assert.That(descriptor.Prefab.AssetGUID, Is.EqualTo(expectedGuid));
            var runtimeDescriptor = new BakedEntityPrefab_GRC { PrefabGuid = descriptor.Prefab.AssetGUID };
            Assert.That(runtimeDescriptor.Prefab, Is.EqualTo(descriptor.Prefab));
            var fields = RuntimePatchSchemaDiscovery.DescribeComponent(typeof(BakedEntityPrefab_GRC), 1).Fields;
            Assert.That(fields.Count, Is.EqualTo(1));
            Assert.That(fields[0].ValueType.Kind, Is.EqualTo(RuntimePatchGeneratedValueKind.Hash128));
        }

        [Test]
        public void NativeDescriptorAndRuntimeGuidRoundtripThroughExistingSerializers()
        {
            var authoring = CreateTemplate("roundtrip");
            Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out var collection, out var error), Is.True, error);
            var descriptor = collection.Components.OfType<BakedEntityPrefab_GAC>().Single();
            var authoredJson = JsonConvert.SerializeObject(descriptor, GameAssetJson.DataSettings);
            Assert.That(authoredJson, Does.Contain(descriptor.Prefab.AssetGUID.ToString()));
            Assert.That(authoredJson, Does.Not.Contain("GlobalId"), "JSON preserves the native prefab identity rather than serializing Unity's internal weak-reference layout.");
            var authoredCopy = JsonConvert.DeserializeObject<BakedEntityPrefab_GAC>(authoredJson, GameAssetJson.DataSettings);
            Assert.That(authoredCopy.Prefab, Is.EqualTo(descriptor.Prefab));
            var runtimeDescriptor = new BakedEntityPrefab_GRC { PrefabGuid = descriptor.Prefab.AssetGUID };
            var runtimeJson = JsonConvert.SerializeObject(runtimeDescriptor, GameRuntimeJson.Settings);
            var runtimeCopy = JsonConvert.DeserializeObject<BakedEntityPrefab_GRC>(runtimeJson, GameRuntimeJson.Settings);
            Assert.That(runtimeCopy.Prefab, Is.EqualTo(descriptor.Prefab));
            Assert.That(runtimeJson, Does.Not.Contain("\"Prefab\":"));
        }

        [Test]
        public void PreparationFreezesDescriptorAndFailedPreparationClearsOldReference()
        {
            var authoring = CreateTemplate("snapshot");
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out _definitions, out var error), Is.True, error);
            Assert.That(_definitions.TryGetAsset(new PrefabGameAssetReference(authoring), out var definition), Is.True);
            var frozen = definition.Components.OfType<BakedEntityPrefab_GAC>().Single().Prefab;
            var binding = authoring.Converters.Single(item => item.Converter is BakedEntityPrefabConverter);
            Assert.That(binding.Converter.Prepare(binding.Source, _ => null), Is.True);
            var current = binding.Converter.Convert(binding.Source).OfType<BakedEntityPrefab_GAC>().Single();
            Assert.That(current.Prefab, Is.EqualTo(default(Unity.Entities.Serialization.EntityPrefabReference)));
            Assert.That(definition.Components.OfType<BakedEntityPrefab_GAC>().Single().Prefab, Is.EqualTo(frozen));
        }

        [Test]
        public void DerivedLibraryFollowsIndexAndVariantIdentityWithoutManualPrefabList()
        {
            var source = CreateTemplate("source");
            _assets.CreatePrefab("ordinary");
            var originalGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            var renamedPath = _assets.Folder + "/renamed.prefab";
            Assert.That(AssetDatabase.MoveAsset(AssetDatabase.GetAssetPath(source), renamedPath), Is.Empty);
            source = AssetDatabase.LoadAssetAtPath<GameObject>(renamedPath).GetComponent<GameAssetAuthoring>();
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source.gameObject);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(instance, _assets.Folder + "/variant.prefab");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var references = BakedEntityPrefabLibrary.GetReferences(index);
            Assert.That(references.Count, Is.EqualTo(2));
            Assert.That(references.Select(item => item.DefinitionGuid), Does.Contain(new EntityHash128(originalGuid)));
            foreach (var reference in references)
            {
                Assert.That(reference.Prefab.AssetGUID, Is.EqualTo(reference.DefinitionGuid));
            }
        }

        [Test]
        public void TemplateResourceChangeUsesExistingNativeHashAndKeepsPreparedSnapshot()
        {
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Hidden/InternalErrorShader");
            var material = new Material(shader);
            AssetDatabase.CreateAsset(material, _assets.Folder + "/visual.mat");
            var authoring = CreateTemplate("resources", material);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var originalHash = index.Entries.Single().ContentHash;
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out _definitions, out var error), Is.True, error);
            var snapshotHash = _definitions.NativeContentHash;
            material.renderQueue = 2601;
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssetIfDirty(material);
            index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries.Single().ContentHash, Is.Not.EqualTo(originalHash));
            Assert.That(_definitions.NativeContentHash, Is.EqualTo(snapshotHash));
        }

        [Test]
        public void ConfiguredLibrarySceneIsIncludedWithoutBeingInBuildSettings()
        {
            var scenePath = _assets.Folder + "/Library.unity";
            // EditMode Test Runner owns this empty isolation scene and restores the user's scene setup.
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            try
            {
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
            }
            finally
            {
                EditorSceneManager.CloseScene(scene, true);
            }
            BakedEntityPrefabLibraryBuildConfiguration.Configure(new[] { scenePath, scenePath.Replace('/', '\\') });
            Assert.That(BakedEntityPrefabLibraryBuildConfiguration.ScenePaths.Count, Is.EqualTo(1));
            var guids = new BakedEntityPrefabLibraryBuildAdditions().RegisterAdditionalEntityScenesToBuild();
            Assert.That(guids, Does.Contain(new EntityHash128(AssetDatabase.AssetPathToGUID(scenePath))));
            BakedEntityPrefabLibraryBuildConfiguration.Configure(new[] { _assets.Folder + "/Missing.unity" });
            Assert.Throws<BuildFailedException>(() => BakedEntityPrefabLibraryBuildConfiguration.CollectSceneGuids());
        }

        private GameAssetAuthoring CreateTemplate(string name, Material material = null)
        {
            var root = new GameObject(name);
            try
            {
                var authoring = root.AddComponent<GameAssetAuthoring>();
                root.AddComponent<BakedEntityPrefabAuthoring>();
                if (material != null)
                    root.AddComponent<MeshRenderer>().sharedMaterial = material;
                var saved = PrefabUtility.SaveAsPrefabAsset(root, _assets.Folder + "/" + name + ".prefab").GetComponent<GameAssetAuthoring>();
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(saved, out _, out var error), Is.True, error);
                return saved;
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
