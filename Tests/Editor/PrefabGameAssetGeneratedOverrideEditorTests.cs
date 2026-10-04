#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Editor.Authoring;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.Serialization;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class PrefabGameAssetGeneratedOverrideEditorTests
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
        public void BuildFinalizerRefreshesLateResourceDependenciesWithoutChangingSavedOverrideIdentityOrValues()
        {
            var sourceResourcePath = _assets.Folder + "/build-source-resource.asset";
            var instanceResourcePath = _assets.Folder + "/build-instance-resource.asset";
            var sourceResource = CreateTexture(sourceResourcePath, Color.black);
            var instanceResource = CreateTexture(instanceResourcePath, Color.white);
            var source = _assets.CreatePrefab("build-finalizer-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5, Values = new List<int> { 1, 2 }, Resource = sourceResource } });
            var prefabPath = AssetDatabase.GetAssetPath(source);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var scenePath = _assets.Folder + "/build-finalizer-instance.unity";
            var instanceId = SaveInstanceScene(prefabPath, scenePath, authoring =>
            {
                var component = (PrefabAuthoringAlpha_GAC)authoring.Components.Single();
                component.Value = 25;
                component.Values = new List<int> { 8, 13 };
                component.Resource = instanceResource;
            });
            var initialIndex = PrefabGameAssetIndexEditorUtility.Reconcile();
            var initialRoot = initialIndex.Entries.Single();
            var initialOverride = initialIndex.Overrides.Single();
            var initialDefinitions = Prepare(initialIndex);
            var initialLibrary = GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), initialDefinitions);
            var initialCatalog = RuntimeSessionAssetCatalog.FromLock(initialLibrary);
            var frozenHash = initialDefinitions.NativeContentHash;
            var initialSourceResourceHash = AssetDatabase.GetAssetDependencyHash(sourceResourcePath);
            var initialInstanceResourceHash = AssetDatabase.GetAssetDependencyHash(instanceResourcePath);

            // A late preprocessor may leave a changed resource dirty after the early CMS preparation.
            foreach (var resource in new[] { sourceResource, instanceResource })
            {
                resource.SetPixel(0, 0, Color.red);
                resource.Apply();
                EditorUtility.SetDirty(resource);
            }
            Assert.That(EditorUtility.IsDirty(sourceResource), Is.True);
            Assert.That(EditorUtility.IsDirty(instanceResource), Is.True);
            Assert.That(initialIndex.Entries.Single().ContentHash, Is.EqualTo(initialRoot.ContentHash), "The synchronous late preprocessor leaves the early index stale until the final build gate runs.");
            Assert.That(initialIndex.Overrides.Single().ContentHash, Is.EqualTo(initialOverride.ContentHash));

            new PrefabGameAssetBuildFinalizer().OnPreprocessBuild(null);
            Assert.That(EditorUtility.IsDirty(sourceResource), Is.False, "Changed dependencies must be saved before computing the hashes packed into the player.");
            Assert.That(EditorUtility.IsDirty(instanceResource), Is.False);
            Assert.That(AssetDatabase.GetAssetDependencyHash(sourceResourcePath), Is.Not.EqualTo(initialSourceResourceHash));
            Assert.That(AssetDatabase.GetAssetDependencyHash(instanceResourcePath), Is.Not.EqualTo(initialInstanceResourceHash));
            var changedRootHash = PrefabGameAssetContentHashUtility.Compute(prefabPath);
            var changedOverrideHash = PrefabGameAssetContentHashUtility.Compute(scenePath, initialOverride.Components);
            Assert.That(changedRootHash, Is.Not.EqualTo(initialRoot.ContentHash));
            Assert.That(changedOverrideHash, Is.Not.EqualTo(initialOverride.ContentHash));
            var indexPath = PrefabGameAssetIndexConfiguration.IndexAssetPath;
            AssetDatabase.ImportAsset(indexPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            var finalized = AssetDatabase.LoadAssetAtPath<DingoGameObjectsCMS.AssetLibrary.Manifest.PrefabGameAssetIndex>(indexPath);
            var root = finalized.Entries.Single();
            var record = finalized.Overrides.Single();
            Assert.That(root.GUID, Is.EqualTo(initialRoot.GUID));
            Assert.That(root.ContentHash, Is.EqualTo(changedRootHash), "The saved Resources index must contain the final dependency hash.");
            Assert.That(record.ContentHash, Is.EqualTo(changedOverrideHash));
            Assert.That(record.Id, Is.EqualTo(instanceId));
            Assert.That(record.Source.GetEntityId(), Is.EqualTo(initialOverride.Source.GetEntityId()));
            Assert.That(record.OverridesJson, Is.EqualTo(initialOverride.OverridesJson));
            var effective = (PrefabAuthoringAlpha_GAC)record.Components.Single();
            Assert.That(effective.Value, Is.EqualTo(25));
            Assert.That(effective.Values, Is.EqualTo(new[] { 8, 13 }));
            Assert.That(AssetDatabase.GetAssetPath(effective.Resource), Is.EqualTo(instanceResourcePath));

            var nextDefinitions = Prepare(finalized);
            var nextTemplates = Templates();
            var nextLibrary = GameAssetLibraryLockBuilder.Build(nextTemplates, Array.Empty<ModPackage>(), nextDefinitions);
            var nextCatalog = RuntimeSessionAssetCatalog.FromLock(nextLibrary);
            Assert.That(initialDefinitions.NativeContentHash, Is.EqualTo(frozenHash), "Already prepared definitions remain frozen.");
            Assert.That(nextDefinitions.NativeContentHash, Is.Not.EqualTo(frozenHash), "The catalog prepared for packing must include dependencies changed by late preprocessors.");
            Assert.That(nextDefinitions.ResourceAssets, Has.Count.EqualTo(2));
            Assert.That(nextCatalog.ManifestEntries.Select(entry => (entry.AssetGuid, entry.ExactKey)), Is.EqualTo(initialCatalog.ManifestEntries.Select(entry => (entry.AssetGuid, entry.ExactKey))), "A dependency edit must not change root or derived domain identities.");
            var preview = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                var instance = preview.GetRootGameObjects().Single().GetComponent<GameAssetAuthoring>();
                Assert.That(instance.RuntimeOverrideId, Is.EqualTo(instanceId));
                Assert.That(nextDefinitions.TryResolveReference(instance, out var reference), Is.True);
                Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.True);
                Assert.That(nextDefinitions.TryGetAsset(instance, out var derived), Is.True);
                var component = (PrefabAuthoringAlpha_GAC)derived.Components.Single();
                Assert.That(component.Value, Is.EqualTo(25));
                Assert.That(component.Values, Is.EqualTo(new[] { 8, 13 }));
                Assert.That(AssetDatabase.GetAssetPath(component.Resource), Is.EqualTo(instanceResourcePath));
                var runtime = nextTemplates.Materialize(GameAssetInstanceFactory.Create(reference, Hash128.Compute("build-finalizer-instance")), nextLibrary);
                Assert.That(runtime.Origin.Asset.AssetGuid, Is.EqualTo(derived.GUID));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }
        }

        [Test]
        public void ResourceOnlyOverrideInSavedSceneGeneratesDerivedDefinitionAndIsRemovedWithScene()
        {
            var firstPath = _assets.Folder + "/original-resource.asset";
            var secondPath = _assets.Folder + "/instance-resource.asset";
            var first = CreateTexture(firstPath, Color.black);
            CreateTexture(secondPath, Color.white);
            var source = _assets.CreatePrefab("resource-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5, Resource = first } });
            var prefabPath = AssetDatabase.GetAssetPath(source);
            var sourceGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var scenePath = _assets.Folder + "/resource-outside-build-settings.unity";
            var instanceId = SaveInstanceScene(prefabPath, scenePath, authoring => ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Resource = AssetDatabase.LoadAssetAtPath<Texture2D>(secondPath));
            Assert.That(EditorBuildSettings.scenes.Any(scene => scene.path == scenePath), Is.False);

            var before = EditorSceneManager.GetSceneManagerSetup();
            var dirtyBefore = Enumerable.Range(0, SceneManager.sceneCount).Select(index => SceneManager.GetSceneAt(index).isDirty).ToArray();
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries, Has.Count.EqualTo(1), "A scene instance must not become a new prefab definition.");
            Assert.That(index.Overrides, Has.Count.EqualTo(1));
            var record = index.Overrides.Single();
            Assert.That(record.Id, Is.EqualTo(instanceId));
            Assert.That(record.Id, Does.StartWith(AssetDatabase.AssetPathToGUID(scenePath) + ":"));
            Assert.That(record.OverridesJson, Does.Contain(AssetDatabase.AssetPathToGUID(secondPath)));
            Assert.That(record.OverridesJson, Does.Contain("UnityAssetGuid"));
            Assert.That(record.OverridesJson, Does.Contain("LocalFileId"));
            var effective = (PrefabAuthoringAlpha_GAC)record.Components.Single();
            Assert.That(effective.Value, Is.EqualTo(5));
            Assert.That(AssetDatabase.GetAssetPath(effective.Resource), Is.EqualTo(secondPath));
            Assert.That(EditorSceneManager.GetSceneManagerSetup().Select(scene => (scene.path, scene.isLoaded, scene.isActive)), Is.EqualTo(before.Select(scene => (scene.path, scene.isLoaded, scene.isActive))));
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount).Select(sceneIndex => SceneManager.GetSceneAt(sceneIndex).isDirty), Is.EqualTo(dirtyBefore));

            var definitions = Prepare(index);
            var templates = Templates();
            var library = GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), definitions);
            var frozenHash = definitions.NativeContentHash;
            Assert.That(definitions.ResourceAssets, Has.Count.EqualTo(2), "Resource traversal must include effective override resources as well as root definitions.");
            var preview = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                var instance = preview.GetRootGameObjects().Single().GetComponent<GameAssetAuthoring>();
                Assert.That(instance.SourceDefinitionGuid, Is.EqualTo(sourceGuid));
                Assert.That(instance.RuntimeOverrideId, Is.EqualTo(instanceId), "The scene save hook must persist generated metadata, not only build it transiently.");
                var globalId = GlobalObjectId.GetGlobalObjectIdSlow(instance);
                Assert.That(instanceId, Is.EqualTo(AssetDatabase.AssetPathToGUID(scenePath) + ":" + globalId.targetObjectId + ":" + globalId.targetPrefabId));
                Assert.That(definitions.TryResolveReference(instance, out var reference), Is.True);
                Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.True);
                Assert.That(definitions.TryGetAsset(instance, out var derived), Is.True);
                var component = (PrefabAuthoringAlpha_GAC)derived.Components.Single();
                Assert.That(component.Value, Is.EqualTo(5));
                Assert.That(component.Resource.GetEntityId(), Is.EqualTo(AssetDatabase.LoadAssetAtPath<Texture2D>(secondPath).GetEntityId()));
                Assert.That(AssetDatabase.GetAssetPath(component.Resource), Is.EqualTo(secondPath));
                var original = definitions.Assets.Single();
                Assert.That(AssetDatabase.GetAssetPath(((PrefabAuthoringAlpha_GAC)original.Components.Single()).Resource), Is.EqualTo(firstPath));
                Assert.That(derived.GUID, Is.Not.EqualTo(original.GUID));
                var runtime = templates.Materialize(GameAssetInstanceFactory.Create(reference, Hash128.Compute("generated-resource-override-instance")), library);
                Assert.That(runtime.Origin.Asset.ExactKey, Is.EqualTo(derived.Key));
                Assert.That(runtime.Origin.Asset.AssetGuid, Is.EqualTo(derived.GUID));
                Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(2));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(preview);
            }

            var repeated = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(repeated.Overrides.Single().Id, Is.EqualTo(instanceId));
            var next = Prepare(repeated);
            var nextLibrary = GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), next);
            Assert.That(RuntimeSessionAssetCatalog.FromLock(nextLibrary).ManifestEntries.Select(entry => (entry.AssetGuid, entry.ExactKey)), Is.EqualTo(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Select(entry => (entry.AssetGuid, entry.ExactKey))));
            Assert.That(AssetDatabase.DeleteAsset(scenePath), Is.True);
            Assert.That(PrefabGameAssetIndexEditorUtility.Reconcile().Overrides, Is.Empty);
            Assert.That(definitions.NativeContentHash, Is.EqualTo(frozenHash));
            Assert.That(definitions.ResourceAssets, Has.Count.EqualTo(2));
        }

        [Test]
        public void NestedPrefabMetadataSurvivesReimportAndResolvesInstantiatedContainerWithStableDependencyHashes()
        {
            var resourcePath = _assets.Folder + "/nested-resource.asset";
            var resource = CreateTexture(resourcePath, Color.white);
            var source = _assets.CreatePrefab("nested-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var sourceGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            var containerPath = _assets.Folder + "/z-nested-container.prefab";
            var container = SaveNestedContainer(source.gameObject, containerPath, authoring =>
            {
                var component = (PrefabAuthoringAlpha_GAC)authoring.Components.Single();
                component.Value = 25;
                component.Resource = resource;
            });
            var wrapperPath = _assets.Folder + "/a-nested-wrapper.prefab";
            SaveNestedContainer(container, wrapperPath);
            var nested = container.GetComponentInChildren<GameAssetAuthoring>(true);
            Assert.That(EditorUtility.IsPersistent(nested), Is.True);
            Assert.That(PrefabUtility.IsPartOfPrefabInstance(nested), Is.True);
            Assert.That(nested.SetRuntimeDefinitionSource(sourceGuid, "stale-generated-record"), Is.True);
            EditorUtility.SetDirty(nested);
            PrefabUtility.RecordPrefabInstancePropertyModifications(nested);

            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries, Has.Count.EqualTo(1), "Containers without a root marker must not become separate definitions.");
            Assert.That(index.Overrides, Has.Count.EqualTo(2));
            foreach (var path in new[] { wrapperPath, containerPath })
            {
                var assetGuid = AssetDatabase.AssetPathToGUID(path);
                var record = index.Overrides.Single(entry => entry.Id.StartsWith(assetGuid + ":", StringComparison.Ordinal));
                Assert.That(record.ContentHash, Is.EqualTo(PrefabGameAssetContentHashUtility.Compute(path)), "Every dependency hash must be captured after all nested metadata has been saved.");
            }
            var definitions = Prepare(index);
            var templates = Templates();
            var library = GameAssetLibraryLockBuilder.Build(templates, Array.Empty<ModPackage>(), definitions);
            var originalHash = definitions.NativeContentHash;
            foreach (var path in new[] { wrapperPath, containerPath })
            {
                AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var savedContainer = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var savedAuthoring = savedContainer.GetComponentInChildren<GameAssetAuthoring>(true);
                var record = index.Overrides.Single(entry => entry.Id.StartsWith(AssetDatabase.AssetPathToGUID(path) + ":", StringComparison.Ordinal));
                Assert.That(savedAuthoring.SourceDefinitionGuid, Is.EqualTo(sourceGuid));
                Assert.That(savedAuthoring.RuntimeOverrideId, Is.EqualTo(record.Id), "Generated fields must be serialized as nested prefab instance overrides.");
                var instance = UnityEngine.Object.Instantiate(savedContainer);
                try
                {
                    var authoring = instance.GetComponentInChildren<GameAssetAuthoring>(true);
                    Assert.That(definitions.TryResolveReference(authoring, out var reference), Is.True);
                    Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.True);
                    Assert.That(definitions.TryGetAsset(authoring, out var derived), Is.True);
                    var component = (PrefabAuthoringAlpha_GAC)derived.Components.Single();
                    Assert.That(component.Value, Is.EqualTo(25));
                    Assert.That(AssetDatabase.GetAssetPath(component.Resource), Is.EqualTo(resourcePath));
                    var runtime = templates.Materialize(GameAssetInstanceFactory.Create(reference, Hash128.Compute(path)), library);
                    Assert.That(runtime.Origin.Asset.ExactKey, Is.EqualTo(derived.Key));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }
            var repeated = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(Prepare(repeated).NativeContentHash, Is.EqualTo(originalHash), "A second reconciliation must not be needed to stabilize dependency hashes.");
            Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(2), "Equal effective values in nested containers share one derived definition.");
        }

        [Test]
        public void HierarchyCandidatesFollowNestedPrefabsButSkipPrefabReferencesThroughScriptableObjects()
        {
            var source = _assets.CreatePrefab("dependency-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var sourcePath = AssetDatabase.GetAssetPath(source);
            var containerPath = _assets.Folder + "/dependency-container.prefab";
            SaveNestedContainer(source.gameObject, containerPath);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var relatedPath = _assets.Folder + "/related-nested-scene.unity";
            SaveInstanceScene(containerPath, relatedPath, authoring => ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = 25);
            var unrelatedPath = _assets.Folder + "/unrelated-indirect-scene.unity";
            SaveIndirectReferenceScene(PrefabGameAssetIndexConfiguration.IndexAssetPath, unrelatedPath);

            var recursiveDependencies = AssetDatabase.GetDependencies(unrelatedPath, true);
            Assert.That(recursiveDependencies.Contains(sourcePath), Is.True, "The fixture must actually reach the marked prefab through the referenced SO.");
            Assert.That(AssetDatabase.GetDependencies(unrelatedPath, false).Contains(PrefabGameAssetIndexConfiguration.IndexAssetPath), Is.True);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var candidates = PrefabGameAssetOverrideEditorUtility.CollectHierarchyAssetPaths(index.Entries);
            Assert.That(candidates.Contains(sourcePath), Is.True);
            Assert.That(candidates.Contains(containerPath), Is.True, "A nested prefab dependency must remain relevant to hierarchy scanning.");
            Assert.That(candidates.Contains(relatedPath), Is.True, "Relevance must propagate through a nested prefab to its scene.");
            Assert.That(candidates.Contains(unrelatedPath), Is.False, "A ScriptableObject reference to a marked prefab does not place authoring in the scene hierarchy.");
            var reconciled = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(reconciled.Entries.Count, Is.EqualTo(1));
            Assert.That(reconciled.Overrides.Single().Id, Does.StartWith(AssetDatabase.AssetPathToGUID(relatedPath) + ":"));
        }

        [Test]
        public void PrefabCopyAndVariantReceiveOwnBakedSourceIdentityWhileMoveKeepsIdentity()
        {
            var original = _assets.CreatePrefab("metadata-original");
            var originalPath = AssetDatabase.GetAssetPath(original);
            var originalGuid = AssetDatabase.AssetPathToGUID(originalPath);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var copyPath = _assets.Folder + "/metadata-copy.prefab";
            Assert.That(AssetDatabase.CopyAsset(originalPath, copyPath), Is.True);
            var variantPath = _assets.Folder + "/metadata-variant.prefab";
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(originalPath));
            try
            {
                Assert.That(PrefabUtility.SaveAsPrefabAsset(instance, variantPath), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries, Has.Count.EqualTo(3));
            foreach (var path in new[] { originalPath, copyPath, variantPath })
            {
                var authoring = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponent<GameAssetAuthoring>();
                var guid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(guid));
                Assert.That(authoring.RuntimeOverrideId, Is.Null.Or.Empty);
                Assert.That(new PrefabGameAssetReference(authoring).TryResolveAuthoredKey(out var reference), Is.True);
                Assert.That(reference.RequestedKey.Key, Is.EqualTo(guid));
                Assert.That(index.Entries.Single(entry => entry.GUID == guid).Authoring.GetEntityId(), Is.EqualTo(authoring.GetEntityId()));
            }
            Assert.That(new[] { originalGuid, AssetDatabase.AssetPathToGUID(copyPath), AssetDatabase.AssetPathToGUID(variantPath) }.Distinct().Count(), Is.EqualTo(3));
            var movedPath = _assets.Folder + "/metadata-renamed.prefab";
            Assert.That(AssetDatabase.MoveAsset(originalPath, movedPath), Is.Empty);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(movedPath).GetComponent<GameAssetAuthoring>().SourceDefinitionGuid, Is.EqualTo(originalGuid));
        }

        [Test]
        public void SceneMoveKeepsOverrideRecordAndSavedCopyReceivesOwnRecordWithSharedDerivedIdentity()
        {
            var source = _assets.CreatePrefab("scene-metadata-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var prefabPath = AssetDatabase.GetAssetPath(source);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var originalPath = _assets.Folder + "/metadata-scene.unity";
            var originalId = SaveInstanceScene(prefabPath, originalPath, authoring => ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = 25);
            var originalSceneGuid = AssetDatabase.AssetPathToGUID(originalPath);
            var movedPath = _assets.Folder + "/metadata-scene-renamed.unity";
            Assert.That(AssetDatabase.MoveAsset(originalPath, movedPath), Is.Empty);
            Assert.That(PrefabGameAssetIndexEditorUtility.Reconcile().Overrides.Single().Id, Is.EqualTo(originalId));
            var copyPath = _assets.Folder + "/metadata-scene-copy.unity";
            Assert.That(AssetDatabase.CopyAsset(movedPath, copyPath), Is.True);
            var copiedId = SaveExistingScene(copyPath);
            Assert.That(copiedId, Is.Not.EqualTo(originalId));
            Assert.That(copiedId, Does.StartWith(AssetDatabase.AssetPathToGUID(copyPath) + ":"));
            Assert.That(originalId, Does.StartWith(originalSceneGuid + ":"));
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Overrides.Select(entry => entry.Id), Is.EquivalentTo(new[] { originalId, copiedId }));
            var definitions = Prepare(index);
            var library = GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), definitions);
            var references = new List<GameAssetKey>();
            foreach (var path in new[] { movedPath, copyPath })
            {
                var preview = EditorSceneManager.OpenPreviewScene(path);
                try
                {
                    var authoring = preview.GetRootGameObjects().Single().GetComponent<GameAssetAuthoring>();
                    Assert.That(definitions.TryResolveReference(authoring, out var reference), Is.True);
                    references.Add(reference.RequestedKey);
                    Assert.That(definitions.TryGetAsset(authoring, out var asset), Is.True);
                    Assert.That(((PrefabAuthoringAlpha_GAC)asset.Components.Single()).Value, Is.EqualTo(25));
                }
                finally
                {
                    EditorSceneManager.ClosePreviewScene(preview);
                }
            }
            Assert.That(references[0], Is.EqualTo(references[1]), "Scene identity distinguishes instance records; equal effective values share one derived content identity.");
            Assert.That(RuntimeSessionAssetCatalog.FromLock(library).ManifestEntries.Count, Is.EqualTo(2));
        }

        [Test]
        public void ProcessedSceneRootKeepsIndexedBakedIdentityAfterPrefabConnectionIsRemoved()
        {
            var source = _assets.CreatePrefab("processed-root-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var definitions = Prepare(index);
            var sourceGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source.gameObject, scene);
                var authoring = UnpackForSceneProcessing(instance);
                Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(sourceGuid));
                Assert.That(PrefabGameAssetOverrideEditorUtility.ApplyIndexedSceneMetadata(scene, index), Is.False);
                Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(sourceGuid));
                Assert.That(authoring.RuntimeOverrideId, Is.Null.Or.Empty);
                Assert.That(definitions.TryResolveReference(authoring, out var reference), Is.True);
                Assert.That(reference.RequestedKey.Key, Is.EqualTo(sourceGuid));
                Assert.That(definitions.TryGetAsset(authoring, out var asset), Is.True);
                Assert.That(((PrefabAuthoringAlpha_GAC)asset.Components.Single()).Value, Is.EqualTo(5));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [Test]
        public void ProcessedSceneOverrideKeepsIndexedBakedIdentityAfterPrefabConnectionIsRemoved()
        {
            var source = _assets.CreatePrefab("processed-override-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var prefabPath = AssetDatabase.GetAssetPath(source);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var scenePath = _assets.Folder + "/processed-override.unity";
            var overrideId = SaveInstanceScene(prefabPath, scenePath, authoring => ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = 25);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var definitions = Prepare(index);
            GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), definitions);
            var sourceGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            var scene = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                var authoring = UnpackForSceneProcessing(scene.GetRootGameObjects().Single());
                Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(sourceGuid));
                Assert.That(authoring.RuntimeOverrideId, Is.EqualTo(overrideId));
                Assert.That(PrefabGameAssetOverrideEditorUtility.ApplyIndexedSceneMetadata(scene, index), Is.False);
                Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(sourceGuid));
                Assert.That(authoring.RuntimeOverrideId, Is.EqualTo(overrideId));
                Assert.That(definitions.TryResolveReference(authoring, out var reference), Is.True);
                Assert.That(GameAssetDerivedIdentity.IsDerived(reference.RequestedKey), Is.True);
                Assert.That(definitions.TryGetAsset(authoring, out var asset), Is.True);
                Assert.That(((PrefabAuthoringAlpha_GAC)asset.Components.Single()).Value, Is.EqualTo(25));
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        [TestCase("MissingSource")]
        [TestCase("MissingOverride")]
        [TestCase("WrongOverrideSource")]
        public void ProcessedSceneDoesNotPreserveBakedIdentityOutsideItsIndexedSourceAndOverride(string invalidIdentity)
        {
            var source = _assets.CreatePrefab("processed-invalid-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            var otherSource = _assets.CreatePrefab("processed-other-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 10 } });
            var prefabPath = AssetDatabase.GetAssetPath(source);
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var scenePath = _assets.Folder + "/processed-invalid.unity";
            var overrideId = SaveInstanceScene(prefabPath, scenePath, authoring => ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = 25);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            var definitions = Prepare(index);
            GameAssetLibraryLockBuilder.Build(Templates(), Array.Empty<ModPackage>(), definitions);
            var scene = EditorSceneManager.OpenPreviewScene(scenePath);
            try
            {
                var authoring = UnpackForSceneProcessing(scene.GetRootGameObjects().Single());
                var sourceGuid = invalidIdentity == "MissingSource" ? Guid.NewGuid().ToString("N") : invalidIdentity == "WrongOverrideSource" ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(otherSource)) : AssetDatabase.AssetPathToGUID(prefabPath);
                authoring.SetRuntimeDefinitionSource(sourceGuid, invalidIdentity == "MissingOverride" ? "unindexed-override" : overrideId);
                Assert.That(PrefabGameAssetOverrideEditorUtility.ApplyIndexedSceneMetadata(scene, index), Is.True);
                Assert.That(authoring.SourceDefinitionGuid, Is.Null.Or.Empty);
                Assert.That(authoring.RuntimeOverrideId, Is.Null.Or.Empty);
                Assert.That(definitions.TryResolveReference(authoring, out _), Is.False);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static GameAssetAuthoring UnpackForSceneProcessing(GameObject instance)
        {
            var authoring = instance.GetComponent<GameAssetAuthoring>();
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(authoring), Is.Not.Null);
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            Assert.That(PrefabUtility.GetCorrespondingObjectFromSource(authoring), Is.Null);
            Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out _, out _, out _), Is.False);
            return authoring;
        }

        private PrefabGameAssetDefinitions Prepare(DingoGameObjectsCMS.AssetLibrary.Manifest.PrefabGameAssetIndex index)
        {
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out var definitions, out var error), Is.True, error);
            _definitions.Add(definitions);
            return definitions;
        }

        private GameAssetTemplateCache Templates()
        {
            var templates = new GameAssetTemplateCache(new RuntimePatchCodecRegistry("generated-prefab-overrides"), RuntimeTemplatePatchCodecContext.Instance);
            _templates.Add(templates);
            return templates;
        }

        private static Texture2D CreateTexture(string path, Color color)
        {
            var texture = new Texture2D(2, 2);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            AssetDatabase.CreateAsset(texture, path);
            return texture;
        }

        private static GameObject SaveNestedContainer(GameObject prefab, string path, Action<GameAssetAuthoring> configure = null)
        {
            var container = new GameObject("nested-container");
            try
            {
                var nested = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                nested.transform.SetParent(container.transform, false);
                var authoring = nested.GetComponentInChildren<GameAssetAuthoring>(true);
                if (configure != null)
                {
                    configure(authoring);
                    EditorUtility.SetDirty(authoring);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                }
                return PrefabUtility.SaveAsPrefabAsset(container, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(container);
            }
        }

        private static string SaveInstanceScene(string prefabPath, string scenePath, Action<GameAssetAuthoring> configure)
        {
            var originalActive = SceneManager.GetActiveScene();
            // UTF restores user scenes; an untitled runner scene permits only a Single new scene.
            var mode = string.IsNullOrEmpty(originalActive.path) ? NewSceneMode.Single : NewSceneMode.Additive;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            try
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                var authoring = instance.GetComponentInChildren<GameAssetAuthoring>(true);
                configure(authoring);
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

        private static string SaveExistingScene(string path)
        {
            var originalActive = SceneManager.GetActiveScene();
            var mode = string.IsNullOrEmpty(originalActive.path) ? OpenSceneMode.Single : OpenSceneMode.Additive;
            var scene = EditorSceneManager.OpenScene(path, mode);
            try
            {
                Assert.That(EditorSceneManager.SaveScene(scene), Is.True);
                return scene.GetRootGameObjects().Single().GetComponent<GameAssetAuthoring>().RuntimeOverrideId;
            }
            finally
            {
                CloseTestScene(scene, originalActive);
            }
        }

        private static void SaveIndirectReferenceScene(string indexPath, string scenePath)
        {
            var originalActive = SceneManager.GetActiveScene();
            var mode = string.IsNullOrEmpty(originalActive.path) ? NewSceneMode.Single : NewSceneMode.Additive;
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            try
            {
                var root = new GameObject("indirect prefab resource reference");
                SceneManager.MoveGameObjectToScene(root, scene);
                root.AddComponent<PrefabGameAssetIndirectReferenceFixture>().Index = AssetDatabase.LoadAssetAtPath<DingoGameObjectsCMS.AssetLibrary.Manifest.PrefabGameAssetIndex>(indexPath);
                Assert.That(EditorSceneManager.SaveScene(scene, scenePath), Is.True);
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
