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
using UnityEngine;
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
            PrefabGameAssetIndexConfiguration.Configure(_assets.Folder + "/S_PrefabGameAssetIndex.asset", path => path.StartsWith(_assets.Folder + "/", StringComparison.Ordinal));
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
            var resourcePath = _assets.Folder + "/dependency.png";
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
            var resourcePath = _assets.Folder + "/indirect-dependency.png";
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
