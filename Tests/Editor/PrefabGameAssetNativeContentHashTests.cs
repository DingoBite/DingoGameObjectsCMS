using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Editor.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class PrefabGameAssetNativeContentHashTests
    {
        private PrefabGameAssetTestAssets _assets;
        private string _previousIndexPath;
        private Func<string, bool> _previousInclude;
        private Func<IReadOnlyList<GameAssetComponent>, IEnumerable<string>> _previousResourceDependencies;
        private PrefabGameAssetDefinitions _definitions;

        [SetUp]
        public void SetUp()
        {
            _assets = new PrefabGameAssetTestAssets();
            _previousIndexPath = PrefabGameAssetIndexConfiguration.IndexAssetPath;
            _previousInclude = PrefabGameAssetIndexConfiguration.IncludeAssetPath;
            _previousResourceDependencies = PrefabGameAssetIndexConfiguration.ResourceDependencies;
            AssetDatabase.CreateFolder(_assets.Folder, "Shared");
            PrefabGameAssetIndexConfiguration.Configure(_assets.Folder + "/S_PrefabGameAssetIndex.asset", path => path.StartsWith(_assets.Folder + "/", StringComparison.Ordinal) && !path.StartsWith(_assets.Folder + "/Shared/", StringComparison.Ordinal));
        }

        [TearDown]
        public void TearDown()
        {
            _definitions?.Dispose();
            PrefabGameAssetIndexConfiguration.Configure(_previousIndexPath, _previousInclude, _previousResourceDependencies);
            _assets.Dispose();
        }

        [UnityTest]
        public IEnumerator ResourceImportUpdatesIndexDependencyHashWithoutChangingPreparedSnapshot()
        {
            var resourcePath = _assets.Folder + "/Shared/dependency.png";
            Assert.That(PrefabGameAssetIndexConfiguration.Includes(resourcePath), Is.False);
            WritePng(resourcePath, Color.black);
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(resourcePath);
            var originalResourceHash = AssetDatabase.GetAssetDependencyHash(resourcePath);
            var authoring = _assets.CreatePrefab("resource-dependency", new[] { new PrefabAuthoringAlpha_GAC { Resource = texture } });
            var prefabPath = AssetDatabase.GetAssetPath(authoring);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var initialEntryHash = index.Entries.Single().ContentHash;
            Assert.That(initialEntryHash, Is.EqualTo(PrefabGameAssetContentHashUtility.Compute(prefabPath)));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out _definitions, out var error), Is.True, error);
            var frozenHash = _definitions.NativeContentHash;

            WritePng(resourcePath, Color.red);
            Assert.That(AssetDatabase.GetAssetDependencyHash(resourcePath), Is.Not.EqualTo(originalResourceHash), "The imported PNG itself must change before checking definition hashing.");
            var changedSourceHash = PrefabGameAssetContentHashUtility.Compute(prefabPath);
            Assert.That(changedSourceHash, Is.Not.EqualTo(initialEntryHash), "The source content hash must include changed resource dependencies even if Unity's aggregate prefab hash is unchanged.");

            yield return WaitFor(() => index.Entries.Single().ContentHash == changedSourceHash, "Importing a referenced Unity resource must refresh its prefab definition content hash.");
            Assert.That(index.Entries.Single().ContentHash, Is.EqualTo(PrefabGameAssetContentHashUtility.Compute(prefabPath)));
            Assert.That(_definitions.NativeContentHash, Is.EqualTo(frozenHash));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out var next, out error), Is.True, error);
            try
            {
                Assert.That(next.NativeContentHash, Is.Not.EqualTo(frozenHash));
            }
            finally
            {
                next.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ConfiguredResourceDependencyChangesDefinitionHashWithoutDirectUnityReference()
        {
            var resourcePath = _assets.Folder + "/Shared/indirect-dependency.png";
            Assert.That(PrefabGameAssetIndexConfiguration.Includes(resourcePath), Is.False);
            WritePng(resourcePath, Color.black);
            var authoring = _assets.CreatePrefab("indirect-resource-dependency", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var prefabPath = AssetDatabase.GetAssetPath(authoring);
            Assert.That(AssetDatabase.GetDependencies(prefabPath, true).Contains(resourcePath), Is.False, "The resource must be absent from Unity's serialized dependency graph.");
            PrefabGameAssetIndexConfiguration.Configure(PrefabGameAssetIndexConfiguration.IndexAssetPath, PrefabGameAssetIndexConfiguration.IncludeAssetPath, components => components.OfType<PrefabAuthoringAlpha_GAC>().Any() ? new[] { resourcePath } : Array.Empty<string>());
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var originalSourceHash = index.Entries.Single().ContentHash;
            var originalResourceHash = AssetDatabase.GetAssetDependencyHash(resourcePath);
            Assert.That(originalSourceHash, Is.EqualTo(PrefabGameAssetContentHashUtility.Compute(prefabPath)));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out _definitions, out var error), Is.True, error);
            var preparedHash = _definitions.NativeContentHash;

            WritePng(resourcePath, Color.red);
            Assert.That(AssetDatabase.GetAssetDependencyHash(resourcePath), Is.Not.EqualTo(originalResourceHash));
            Assert.That(AssetDatabase.GetDependencies(prefabPath, true).Contains(resourcePath), Is.False);
            var changedSourceHash = PrefabGameAssetContentHashUtility.Compute(prefabPath);
            Assert.That(changedSourceHash, Is.Not.EqualTo(originalSourceHash), "Explicit product resource dependencies must contribute even without a direct Unity reference.");
            yield return WaitFor(() => index.Entries.Single().ContentHash == changedSourceHash, "Importing a configured indirect resource must update its definition's index hash.");
            var component = (PrefabAuthoringAlpha_GAC)authoring.Components.Single();
            Assert.That(component.Value, Is.EqualTo(5));
            Assert.That(component.Resource, Is.Null);
            Assert.That(_definitions.NativeContentHash, Is.EqualTo(preparedHash));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out var next, out error), Is.True, error);
            try
            {
                Assert.That(next.NativeContentHash, Is.Not.EqualTo(preparedHash));
            }
            finally
            {
                next.Dispose();
            }
        }

        [UnityTest]
        public IEnumerator ChangingResourceReferenceRefreshesHashAndKeepsPreparedReference()
        {
            var firstPath = _assets.Folder + "/first.asset";
            var secondPath = _assets.Folder + "/second.asset";
            var first = CreateTexture(firstPath, Color.black);
            var second = CreateTexture(secondPath, Color.white);
            var authoring = _assets.CreatePrefab("resource-reference", new[] { new PrefabAuthoringAlpha_GAC { Resource = first } });
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var originalHash = index.Entries.Single().ContentHash;
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out _definitions, out var error), Is.True, error);
            var prepared = (PrefabAuthoringAlpha_GAC)_definitions.Assets.Single().Components.Single();

            ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Resource = second;
            EditorUtility.SetDirty(authoring);
            PrefabUtility.SavePrefabAsset(authoring.gameObject);

            yield return WaitFor(() => index.Entries.Single().ContentHash != originalHash, "Saving a changed prefab resource reference must refresh its native content hash.");
            Assert.That(AssetDatabase.GetAssetPath(prepared.Resource), Is.EqualTo(firstPath));
            Assert.That(prepared.Resource.GetEntityId(), Is.EqualTo(AssetDatabase.LoadAssetAtPath<Texture2D>(firstPath).GetEntityId()));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out var next, out error), Is.True, error);
            try
            {
                var nextComponent = (PrefabAuthoringAlpha_GAC)next.Assets.Single().Components.Single();
                Assert.That(AssetDatabase.GetAssetPath(nextComponent.Resource), Is.EqualTo(secondPath));
                Assert.That(next.NativeContentHash, Is.Not.EqualTo(_definitions.NativeContentHash));
            }
            finally
            {
                next.Dispose();
            }
        }

        [Test]
        public void UnreferencedAssetEditsDoNotChangeNativeDefinitionHash()
        {
            _assets.CreatePrefab("unrelated-resource");
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var before = index.Entries.Single().ContentHash;
            CreateTexture(_assets.Folder + "/unreferenced.asset", Color.yellow);
            index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries.Single().ContentHash, Is.EqualTo(before));
        }

        [UnityTest]
        public IEnumerator SavingExternalResourceRefreshesRootDependencyHash()
        {
            var resourcePath = _assets.Folder + "/Shared/saved-dependency.asset";
            var texture = CreateTexture(resourcePath, Color.black);
            var authoring = _assets.CreatePrefab("saved-dependency", new[] { new PrefabAuthoringAlpha_GAC { Resource = texture } });
            var prefabPath = AssetDatabase.GetAssetPath(authoring);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var initialHash = index.Entries.Single().ContentHash;
            Assert.That(PrefabGameAssetIndexConfiguration.Includes(resourcePath), Is.False);

            texture.SetPixel(0, 0, Color.red);
            texture.Apply();
            EditorUtility.SetDirty(texture);
            AssetDatabase.SaveAssets();
            var savedHash = PrefabGameAssetContentHashUtility.Compute(prefabPath);
            Assert.That(savedHash, Is.Not.EqualTo(initialHash));
            yield return WaitFor(() => index.Entries.Single().ContentHash == savedHash, "Saving a dependency outside the authoring root must refresh its indexed root.");
        }

        [UnityTest]
        public IEnumerator ExternalResourceImportRefreshesSavedSceneOverrideHash()
        {
            var resourcePath = _assets.Folder + "/Shared/override-dependency.png";
            WritePng(resourcePath, Color.black);
            var source = _assets.CreatePrefab("override-dependency", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var scenePath = _assets.Folder + "/override-dependency.unity";
            SaveResourceOverrideScene(AssetDatabase.GetAssetPath(source), scenePath, AssetDatabase.LoadAssetAtPath<Texture2D>(resourcePath));
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var initialOverride = index.Overrides.Single();
            Assert.That(AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(source), true), Does.Not.Contain(resourcePath), "This resource belongs only to the saved scene override.");
            Assert.That(PrefabGameAssetIndexConfiguration.Includes(resourcePath), Is.False);

            WritePng(resourcePath, Color.red);
            var changedHash = PrefabGameAssetContentHashUtility.Compute(scenePath, initialOverride.Components);
            Assert.That(changedHash, Is.Not.EqualTo(initialOverride.ContentHash));
            yield return WaitFor(() => index.Overrides.Single().ContentHash == changedHash, "A resource used only by a saved scene override must participate in import invalidation.");
            Assert.That(index.Overrides.Single().Id, Is.EqualTo(initialOverride.Id));
        }

        [UnityTest]
        public IEnumerator MovedExternalDependencyIsTrackedAtItsNewPath()
        {
            var resourcePath = _assets.Folder + "/Shared/moved-dependency.png";
            var movedPath = _assets.Folder + "/Shared/moved-result.png";
            WritePng(resourcePath, Color.black);
            var authoring = _assets.CreatePrefab("moved-dependency", new[] { new PrefabAuthoringAlpha_GAC { Resource = AssetDatabase.LoadAssetAtPath<Texture2D>(resourcePath) } });
            var prefabPath = AssetDatabase.GetAssetPath(authoring);
            var collections = 0;
            PrefabGameAssetIndexConfiguration.Configure(PrefabGameAssetIndexConfiguration.IndexAssetPath, PrefabGameAssetIndexConfiguration.IncludeAssetPath, components =>
            {
                collections++;
                return Array.Empty<string>();
            });
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var beforeMoveCollections = collections;

            Assert.That(AssetDatabase.MoveAsset(resourcePath, movedPath), Is.Empty);
            yield return WaitFor(() => collections > beforeMoveCollections, "The old dependency path must trigger rebuilding the tracked paths after a move.");
            var movedHash = index.Entries.Single().ContentHash;
            WritePng(movedPath, Color.red);
            var changedHash = PrefabGameAssetContentHashUtility.Compute(prefabPath);
            Assert.That(changedHash, Is.Not.EqualTo(movedHash));
            yield return WaitFor(() => index.Entries.Single().ContentHash == changedHash, "Subsequent imports must track the dependency's new path.");
        }

        [UnityTest]
        public IEnumerator DeletingConfiguredExternalDependencyRefreshesDefinitionHash()
        {
            var resourcePath = _assets.Folder + "/Shared/deleted-dependency.png";
            WritePng(resourcePath, Color.black);
            var authoring = _assets.CreatePrefab("deleted-dependency", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var prefabPath = AssetDatabase.GetAssetPath(authoring);
            PrefabGameAssetIndexConfiguration.Configure(PrefabGameAssetIndexConfiguration.IndexAssetPath, PrefabGameAssetIndexConfiguration.IncludeAssetPath, components => components.OfType<PrefabAuthoringAlpha_GAC>().Any() ? new[] { resourcePath } : Array.Empty<string>());
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var originalHash = index.Entries.Single().ContentHash;
            Assert.That(AssetDatabase.GetDependencies(prefabPath, true), Does.Not.Contain(resourcePath));

            Assert.That(AssetDatabase.DeleteAsset(resourcePath), Is.True);
            var changedHash = PrefabGameAssetContentHashUtility.Compute(prefabPath);
            Assert.That(changedHash, Is.Not.EqualTo(originalHash));
            yield return WaitFor(() => index.Entries.Single().ContentHash == changedHash, "Deleting an indirect dependency must invalidate its old tracked path.");
        }

        [UnityTest]
        public IEnumerator UnrelatedResourceAndGeneratedIndexImportsDoNotReconcileOrRewriteIndex()
        {
            _assets.CreatePrefab("unrelated-import");
            var collections = 0;
            PrefabGameAssetIndexConfiguration.Configure(PrefabGameAssetIndexConfiguration.IndexAssetPath, PrefabGameAssetIndexConfiguration.IncludeAssetPath, components =>
            {
                collections++;
                return Array.Empty<string>();
            });
            PrefabGameAssetIndexEditorUtility.Reconcile();
            for (var frame = 0; frame < 5; frame++)
            {
                yield return null;
            }
            var beforeCollections = collections;
            var indexPath = PrefabGameAssetIndexConfiguration.IndexAssetPath;
            var absoluteIndexPath = Path.Combine(Path.GetDirectoryName(Application.dataPath), indexPath);
            var bytes = File.ReadAllBytes(absoluteIndexPath);
            var lastWrite = File.GetLastWriteTimeUtc(absoluteIndexPath);

            WritePng(_assets.Folder + "/Shared/unrelated.png", Color.yellow);
            // Even a broadly included resource is irrelevant if no indexed definition depends on it.
            WritePng(_assets.Folder + "/included-unrelated.png", Color.white);
            AssetDatabase.ImportAsset(indexPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            for (var frame = 0; frame < 5; frame++)
            {
                yield return null;
            }
            Assert.That(collections, Is.EqualTo(beforeCollections), "Unrelated imports and the generated index itself must not schedule another hierarchy scan/hash collection.");
            Assert.That(File.ReadAllBytes(absoluteIndexPath), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(absoluteIndexPath), Is.EqualTo(lastWrite));
        }

        private static void SaveResourceOverrideScene(string prefabPath, string scenePath, Texture resource)
        {
            var originalActive = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, string.IsNullOrEmpty(originalActive.path) ? NewSceneMode.Single : NewSceneMode.Additive);
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath), scene);
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Resource = resource;
                EditorUtility.SetDirty(authoring);
                PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
            }
            finally
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

        private static Texture2D CreateTexture(string path, Color color)
        {
            var texture = new Texture2D(2, 2);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }

        private void WritePng(string assetPath, Color color)
        {
            var projectPath = Path.GetDirectoryName(Application.dataPath);
            var ownedRoot = Path.GetFullPath(Path.Combine(projectPath, _assets.Folder)).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var absolutePath = Path.GetFullPath(Path.Combine(projectPath, assetPath));
            Assert.That(absolutePath.StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase), Is.True, "Imported texture fixtures must stay inside their owned test folder.");
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels(Enumerable.Repeat(color, 4).ToArray());
                texture.Apply();
                File.WriteAllBytes(absolutePath, texture.EncodeToPNG());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
        }

        private static IEnumerator WaitFor(Func<bool> condition, string reason)
        {
            for (var frame = 0; frame < 100; frame++)
            {
                if (condition())
                    yield break;
                yield return null;
            }
            Assert.That(condition(), Is.True, reason);
        }
    }
}
