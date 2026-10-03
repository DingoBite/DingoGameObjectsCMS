#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Editor.Authoring;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class PrefabGameAssetIndexedSceneMetadataTests
    {
        private PrefabGameAssetTestAssets _assets;
        private string _previousIndexPath;
        private Func<string, bool> _previousInclude;
        private Func<IReadOnlyList<GameAssetComponent>, IEnumerable<string>> _previousResourceDependencies;
        private readonly List<PrefabGameAssetDefinitions> _definitions = new();
        private readonly List<GameAssetTemplateCache> _templates = new();

        [SetUp]
        public void SetUp()
        {
            _assets = new PrefabGameAssetTestAssets();
            _previousIndexPath = PrefabGameAssetIndexConfiguration.IndexAssetPath;
            _previousInclude = PrefabGameAssetIndexConfiguration.IncludeAssetPath;
            _previousResourceDependencies = PrefabGameAssetIndexConfiguration.ResourceDependencies;
            PrefabGameAssetIndexConfiguration.Configure(_assets.Folder + "/S_PrefabGameAssetIndex.asset", path => path.StartsWith(_assets.Folder + "/", StringComparison.Ordinal));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var templates in _templates)
            {
                templates.Clear();
            }
            _templates.Clear();
            foreach (var definitions in _definitions)
            {
                definitions.Dispose();
            }
            _definitions.Clear();
            PrefabGameAssetIndexConfiguration.Configure(_previousIndexPath, _previousInclude, _previousResourceDependencies);
            _assets.Dispose();
        }

        [Test]
        public void BaseEditThatRemovesSavedOverrideClearsStaleMetadataWithoutSavingSceneOrUsingLiveValues()
        {
            var source = _assets.CreatePrefab("indexed-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var sourcePath = AssetDatabase.GetAssetPath(source);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var scenePath = _assets.Folder + "/zeroed-override.unity";
            var originalId = SaveInstanceScene(sourcePath, scenePath, 25);
            Assert.That(PrefabGameAssetIndexEditorUtility.Reconcile().Overrides, Has.Count.EqualTo(1));
            source = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath).GetComponent<GameAssetAuthoring>();
            ((PrefabAuthoringAlpha_GAC)source.Components.Single()).Value = 25;
            EditorUtility.SetDirty(source);
            PrefabUtility.SavePrefabAsset(source.gameObject);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Overrides, Is.Empty);
            var definitions = Prepare(index);
            var library = Build(definitions);
            var savedBytes = ReadAssetBytes(scenePath);
            var preview = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                var authoring = preview.GetRootGameObjects().Single().GetComponent<GameAssetAuthoring>();
                Assert.That(authoring.RuntimeOverrideId, Is.EqualTo(originalId), "Reconciliation must not rewrite a closed user scene.");
                ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = 70;
                var dirtyBefore = preview.isDirty;
                Assert.That(PrefabGameAssetOverrideEditorUtility.ApplyIndexedSceneMetadata(preview, index), Is.True);
                Assert.That(authoring.RuntimeOverrideId, Is.Null.Or.Empty);
                Assert.That(preview.isDirty, Is.EqualTo(dirtyBefore));
                Assert.That(((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value, Is.EqualTo(70), "Metadata preparation must preserve unsaved object values.");
                Assert.That(definitions.TryGetAsset(authoring, out var asset), Is.True);
                Assert.That(GameAssetDerivedIdentity.IsDerived(asset.Key), Is.False);
                Assert.That(((PrefabAuthoringAlpha_GAC)asset.Components.Single()).Value, Is.EqualTo(25), "Runtime data uses the saved index instead of converting live scene values.");
                Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(1));
                Assert.That(ReadAssetBytes(scenePath), Is.EqualTo(savedBytes));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        [Test]
        public void UnopenedSceneCopyResolvesItsOwnSnapshotAfterOriginalChangesWithoutAliasingSerializedIds()
        {
            var source = _assets.CreatePrefab("copy-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var sourcePath = AssetDatabase.GetAssetPath(source);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var originalPath = _assets.Folder + "/indexed-original.unity";
            var originalId = SaveInstanceScene(sourcePath, originalPath, 25);
            var copyPath = _assets.Folder + "/indexed-copy.unity";
            Assert.That(AssetDatabase.CopyAsset(originalPath, copyPath), Is.True);
            SaveExistingScene(originalPath, 35);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Overrides, Has.Count.EqualTo(2));
            var copiedId = index.Overrides.Single(entry => entry.Id.StartsWith(AssetDatabase.AssetPathToGUID(copyPath) + ":", StringComparison.Ordinal)).Id;
            Assert.That(copiedId, Is.Not.EqualTo(originalId));
            var definitions = Prepare(index);
            var library = Build(definitions);
            foreach (var path in new[] { copyPath, originalPath })
            {
                var savedBytes = ReadAssetBytes(path);
                var preview = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    var authoring = preview.GetRootGameObjects().Single().GetComponent<GameAssetAuthoring>();
                    Assert.That(authoring.RuntimeOverrideId, Is.EqualTo(originalId), "The unopened copy still contains its original serialized descriptor.");
                    var dirtyBefore = preview.isDirty;
                    new PrefabGameAssetSceneBuildProcessor().OnProcessScene(preview, null);
                    Assert.That(authoring.RuntimeOverrideId, Is.EqualTo(path == copyPath ? copiedId : originalId));
                    Assert.That(preview.isDirty, Is.EqualTo(dirtyBefore));
                    Assert.That(definitions.TryGetAsset(authoring, out var asset), Is.True);
                    Assert.That(GameAssetDerivedIdentity.IsDerived(asset.Key), Is.True);
                    Assert.That(((PrefabAuthoringAlpha_GAC)asset.Components.Single()).Value, Is.EqualTo(path == copyPath ? 25 : 35));
                    Assert.That(ReadAssetBytes(path), Is.EqualTo(savedBytes));
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }
            }
            Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(3), "Diverging scene values require separate derived identities.");
        }

        private PrefabGameAssetDefinitions Prepare(PrefabGameAssetIndex index)
        {
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out var definitions, out var error), Is.True, error);
            _definitions.Add(definitions);
            return definitions;
        }

        private GameAssetLibraryLock Build(PrefabGameAssetDefinitions definitions)
        {
            var templates = new GameAssetTemplateCache(new RuntimePatchCodecRegistry("indexed-scene-metadata"), RuntimeTemplatePatchCodecContext.Instance);
            _templates.Add(templates);
            return GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), definitions);
        }

        private static byte[] ReadAssetBytes(string path)
        {
            return File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Application.dataPath), path));
        }

        private static string SaveInstanceScene(string prefabPath, string scenePath, int value)
        {
            var originalActive = SceneManager.GetActiveScene();
            var mode = string.IsNullOrEmpty(originalActive.path) ? NewSceneMode.Single : NewSceneMode.Additive;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = value;
                EditorUtility.SetDirty(authoring);
                PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
                Assert.That(authoring.RuntimeOverrideId, Is.Not.Null.And.Not.Empty);
                return authoring.RuntimeOverrideId;
            }
            finally
            {
                CloseTestScene(scene, originalActive);
            }
        }

        private static void SaveExistingScene(string path, int value)
        {
            var originalActive = SceneManager.GetActiveScene();
            var mode = string.IsNullOrEmpty(originalActive.path) ? OpenSceneMode.Single : OpenSceneMode.Additive;
            var scene = EditorSceneManager.OpenScene(path, mode);
            try
            {
                var authoring = scene.GetRootGameObjects().Single().GetComponent<GameAssetAuthoring>();
                ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = value;
                EditorUtility.SetDirty(authoring);
                PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                Assert.That(EditorSceneManager.SaveScene(scene), Is.True);
            }
            finally
            {
                CloseTestScene(scene, originalActive);
            }
        }

        private static void CloseTestScene(Scene scene, Scene originalActive)
        {
            if (SceneManager.sceneCount > 1)
            {
                EditorSceneManager.CloseScene(scene, true);
                if (originalActive.IsValid() && originalActive.isLoaded)
                    SceneManager.SetActiveScene(originalActive);
            }
            else
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }
    }
}
#endif
