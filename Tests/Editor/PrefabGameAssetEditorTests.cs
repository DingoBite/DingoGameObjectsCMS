using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Editor.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class PrefabGameAssetEditorTests
    {
        private PrefabGameAssetTestAssets _assets;
        private string _previousIndexPath;
        private Func<string, bool> _previousInclude;
        private Func<IReadOnlyList<GameAssetComponent>, IEnumerable<string>> _previousResourceDependencies;

        [SetUp]
        public void SetUp()
        {
            _assets = new PrefabGameAssetTestAssets();
            _previousIndexPath = PrefabGameAssetIndexConfiguration.IndexAssetPath;
            _previousInclude = PrefabGameAssetIndexConfiguration.IncludeAssetPath;
            _previousResourceDependencies = PrefabGameAssetIndexConfiguration.ResourceDependencies;
            PrefabGameAssetIndexConfiguration.Configure(_assets.Folder + "/S_PrefabGameAssetIndex.asset", path => path.StartsWith(_assets.Folder + "/", StringComparison.Ordinal) && !path.Contains("/Playgrounds/"));
        }

        [TearDown]
        public void TearDown()
        {
            PrefabGameAssetIndexConfiguration.Configure(_previousIndexPath, _previousInclude, _previousResourceDependencies);
            _assets.Dispose();
        }

        [Test]
        public void RenameAndMoveKeepIdentityWhileCopyAndVariantHaveOwnGuids()
        {
            var original = _assets.CreatePrefab("original");
            var originalPath = AssetDatabase.GetAssetPath(original);
            Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(original, out var definition, out var guid, out var error), Is.True, error);
            Assert.That(definition, Is.SameAs(original));
            Assert.That(AssetDatabase.RenameAsset(originalPath, "renamed"), Is.Empty);
            var renamedPath = _assets.Folder + "/renamed.prefab";
            AssetDatabase.CreateFolder(_assets.Folder, "Moved");
            var movedPath = _assets.Folder + "/Moved/renamed.prefab";
            Assert.That(AssetDatabase.MoveAsset(renamedPath, movedPath), Is.Empty);
            var moved = AssetDatabase.LoadAssetAtPath<GameObject>(movedPath).GetComponent<GameAssetAuthoring>();
            Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(moved, out _, out var movedGuid, out error), Is.True, error);
            Assert.That(movedGuid, Is.EqualTo(guid));

            var copiedPath = _assets.Folder + "/copy.prefab";
            Assert.That(AssetDatabase.CopyAsset(movedPath, copiedPath), Is.True);
            var copied = AssetDatabase.LoadAssetAtPath<GameObject>(copiedPath).GetComponent<GameAssetAuthoring>();
            Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(copied, out _, out var copiedGuid, out error), Is.True, error);
            Assert.That(copiedGuid, Is.Not.EqualTo(guid));

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(moved.gameObject);
            try
            {
                var variant = PrefabUtility.SaveAsPrefabAsset(instance, _assets.Folder + "/variant.prefab").GetComponent<GameAssetAuthoring>();
                Assert.That(PrefabUtility.GetPrefabAssetType(variant), Is.EqualTo(PrefabAssetType.Variant));
                Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(variant, out var variantDefinition, out var variantGuid, out error), Is.True, error);
                Assert.That(variantDefinition, Is.SameAs(variant));
                Assert.That(variantGuid, Is.Not.EqualTo(guid).And.Not.EqualTo(copiedGuid));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void NestedInheritedAuthoringResolvesItsSourceWithoutRegisteringAnExtraDefinition()
        {
            var nested = _assets.CreatePrefab("nested");
            var parent = new GameObject("parent");
            try
            {
                parent.AddComponent<GameAssetAuthoring>().SetComponents(new[] { new PrefabAuthoringAlpha_GAC() });
                var nestedInstance = (GameObject)PrefabUtility.InstantiatePrefab(nested.gameObject);
                nestedInstance.transform.SetParent(parent.transform);
                var parentPrefab = PrefabUtility.SaveAsPrefabAsset(parent, _assets.Folder + "/parent.prefab");
                var nestedAuthoring = parentPrefab.transform.GetChild(0).GetComponent<GameAssetAuthoring>();
                Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(nestedAuthoring, out var source, out var guid, out var error), Is.True, error);
                Assert.That(source, Is.SameAs(nested));
                Assert.That(guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(nested))));
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(nestedAuthoring, out _, out error), Is.True, error);
                var entries = PrefabGameAssetIndexEditorUtility.CollectEntries();
                Assert.That(entries, Has.Count.EqualTo(2));
                Assert.That(entries.Select(entry => entry.GUID).Distinct().Count(), Is.EqualTo(2));
                Assert.That(PrefabGameAssetBuildValidation.CollectErrors(), Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void VariantAndItsSceneInstancePreserveInheritedConverterSourceBindings()
        {
            var prefab = _assets.CreatePrefab("converter-base", withConverter: true);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            GameAssetAuthoring variant;
            try
            {
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                ((PrefabAuthoringTransformConverter)authoring.Converters.Single().Converter).Offset = 12;
                PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                variant = PrefabUtility.SaveAsPrefabAsset(instance, _assets.Folder + "/converter-variant.prefab").GetComponent<GameAssetAuthoring>();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
            Assert.That(GameAssetAuthoringEditorUtility.TryCollect(variant, out var collection, out var error), Is.True, error);
            Assert.That(collection.Components.OfType<PrefabAuthoringAlpha_GAC>().Single().Value, Is.EqualTo(12));
            var variantInstance = (GameObject)PrefabUtility.InstantiatePrefab(variant.gameObject);
            try
            {
                var authoring = variantInstance.GetComponent<GameAssetAuthoring>();
                variantInstance.transform.localPosition = Vector3.right * 4;
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out collection, out error), Is.True, error);
                Assert.That(collection.Components.OfType<PrefabAuthoringAlpha_GAC>().Single().Value, Is.EqualTo(16));
                Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out var definition, out var guid, out error), Is.True, error);
                Assert.That(definition, Is.SameAs(variant));
                Assert.That(guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(variant))));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(variantInstance);
            }
        }

        [Test]
        public void ManualAuthoringOnAnOrdinaryPrefabChildIsInvalid()
        {
            var root = new GameObject("manual child root");
            try
            {
                var child = new GameObject("manual child");
                child.transform.SetParent(root.transform);
                child.AddComponent<GameAssetAuthoring>().SetComponents(new[] { new PrefabAuthoringBeta_GAC() });
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, _assets.Folder + "/manual-child.prefab");
                var authoring = prefab.transform.GetChild(0).GetComponent<GameAssetAuthoring>();
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out _, out var error), Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(PrefabGameAssetBuildValidation.CollectErrors().Any(message => message.Contains("manual-child.prefab") && message.Contains("manual child")), Is.True);
                LogAssert.Expect(LogType.Error, new Regex(@"\[Prefab GameAsset content\].*manual-child\.prefab.*manual child.*Fallback:.*safe Try\*"));
                Assert.DoesNotThrow(() => PrefabGameAssetBuildValidation.Validate());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ReconcileTracksAuthoringAndPrefabRemovalAndDoesNotResaveUnchangedComposition()
        {
            var authoring = _assets.CreatePrefab("indexed");
            var prefabPath = AssetDatabase.GetAssetPath(authoring);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries, Has.Count.EqualTo(1));
            Assert.That(index.Entries[0].Authoring, Is.SameAs(authoring));
            Assert.That(EditorUtility.IsDirty(index), Is.False);
            Assert.That(PrefabGameAssetIndexEditorUtility.Reconcile(), Is.SameAs(index));
            Assert.That(EditorUtility.IsDirty(index), Is.False);

            var contents = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                UnityEngine.Object.DestroyImmediate(contents.GetComponent<GameAssetAuthoring>());
                PrefabUtility.SaveAsPrefabAsset(contents, prefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            Assert.That(PrefabGameAssetIndexEditorUtility.Reconcile().Entries, Is.Empty);
            _assets.CreatePrefab("indexed");
            Assert.That(PrefabGameAssetIndexEditorUtility.Reconcile().Entries, Has.Count.EqualTo(1));
            AssetDatabase.DeleteAsset(prefabPath);
            Assert.That(PrefabGameAssetIndexEditorUtility.Reconcile().Entries, Is.Empty);
        }

        [Test]
        public void SaveCallbackBakePreparesMetadataAndLeavesTheNativeSaveToItsCaller()
        {
            var authoring = _assets.CreatePrefab("save-callback");
            var path = AssetDatabase.GetAssetPath(authoring);
            var guid = AssetDatabase.AssetPathToGUID(path);
            PrefabGameAssetOverrideEditorUtility.BakeSavedAssets(new[] { path });
            Assert.That(EditorUtility.IsDirty(authoring), Is.False);
            authoring.SetRuntimeDefinitionSource(null);
            EditorUtility.SetDirty(authoring);

            PrefabGameAssetOverrideEditorUtility.BakeSavedAssets(new[] { path }, false);

            Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(guid));
            Assert.That(EditorUtility.IsDirty(authoring), Is.True, "OnWillSaveAssets preparation must not start another native save of its caller's prefab.");
            Assert.That(PrefabGameAssetOverrideEditorUtility.IsBakingSavedAssets, Is.False);
            PrefabGameAssetOverrideEditorUtility.BakeSavedAssets(new[] { path }, false);
            Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(guid));
            Assert.That(EditorUtility.IsDirty(authoring), Is.True);
            Assert.That(PrefabUtility.SavePrefabAsset(authoring.gameObject), Is.Not.Null);
            Assert.That(EditorUtility.IsDirty(authoring), Is.False);
        }

        [Test]
        public void NativePrefabSavePersistsPreparedMetadataAndRepeatedBakeIsIdempotent()
        {
            var authoring = _assets.CreatePrefab("native-save");
            var path = AssetDatabase.GetAssetPath(authoring);
            var guid = AssetDatabase.AssetPathToGUID(path);
            authoring.SetRuntimeDefinitionSource(null);
            EditorUtility.SetDirty(authoring);

            Assert.That(PrefabUtility.SavePrefabAsset(authoring.gameObject), Is.Not.Null);

            Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(guid));
            Assert.That(EditorUtility.IsDirty(authoring), Is.False);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var saved = contents.GetComponent<GameAssetAuthoring>();
                Assert.That(saved.SourceDefinitionGuid, Is.EqualTo(guid));
                Assert.That(string.IsNullOrEmpty(saved.RuntimeOverrideId), Is.True);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            var hash = AssetDatabase.GetAssetDependencyHash(path);
            PrefabGameAssetOverrideEditorUtility.BakeSavedAssets(new[] { path });
            Assert.That(authoring.SourceDefinitionGuid, Is.EqualTo(guid));
            Assert.That(EditorUtility.IsDirty(authoring), Is.False);
            Assert.That(AssetDatabase.GetAssetDependencyHash(path), Is.EqualTo(hash));
            Assert.That(PrefabGameAssetOverrideEditorUtility.IsBakingSavedAssets, Is.False);
        }

        [Test]
        public void IndexExcludesConfiguredAssetsSortsGuidsAndRecreatesDeletedIndex()
        {
            _assets.CreatePrefab("second");
            _assets.CreatePrefab("first");
            AssetDatabase.CreateFolder(_assets.Folder, "Playgrounds");
            var excluded = _assets.CreatePrefab("excluded");
            Assert.That(AssetDatabase.MoveAsset(AssetDatabase.GetAssetPath(excluded), _assets.Folder + "/Playgrounds/excluded.prefab"), Is.Empty);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries, Has.Count.EqualTo(2));
            var guids = index.Entries.Select(entry => entry.GUID).ToArray();
            Assert.That(guids, Is.EqualTo(guids.OrderBy(guid => guid, StringComparer.Ordinal)));
            AssetDatabase.DeleteAsset(PrefabGameAssetIndexConfiguration.IndexAssetPath);
            var restored = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(restored != null, Is.True);
            Assert.That(restored.Entries, Has.Count.EqualTo(2));
        }

        [UnityTest]
        public IEnumerator ImportMoveSaveAndDeleteMaintainIndexAutomatically()
        {
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries, Is.Empty);
            var authoring = _assets.CreatePrefab("automatic");
            var path = AssetDatabase.GetAssetPath(authoring);
            yield return WaitFor(() => index.Entries.Count == 1, "Prefab import must schedule index reconciliation.");
            AssetDatabase.CreateFolder(_assets.Folder, "Playgrounds");
            var excludedPath = _assets.Folder + "/Playgrounds/automatic.prefab";
            Assert.That(AssetDatabase.MoveAsset(path, excludedPath), Is.Empty);
            yield return WaitFor(() => index.Entries.Count == 0, "Moving a prefab out of the configured scope must remove it.");
            Assert.That(AssetDatabase.MoveAsset(excludedPath, path), Is.Empty);
            yield return WaitFor(() => index.Entries.Count == 1, "Moving a prefab back into scope must register it.");
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                UnityEngine.Object.DestroyImmediate(contents.GetComponent<GameAssetAuthoring>());
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            yield return WaitFor(() => index.Entries.Count == 0, "Saving a prefab without authoring must remove its definition.");
            _assets.CreatePrefab("automatic");
            yield return WaitFor(() => index.Entries.Count == 1, "Saving authoring back into the prefab must restore its definition.");
            AssetDatabase.DeleteAsset(path);
            yield return WaitFor(() => index.Entries.Count == 0, "Deleting a prefab must remove its index entry.");
        }

        [Test]
        public void AdditionalProductionIndexIsRejected()
        {
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var additional = ScriptableObject.CreateInstance<PrefabGameAssetIndex>();
            var additionalPath = _assets.Folder + "/additional-index.asset";
            AssetDatabase.CreateAsset(additional, additionalPath);
            Assert.That(PrefabGameAssetBuildValidation.CollectErrors().Any(error => error.Contains(additionalPath)), Is.True);
            Assert.Throws<BuildFailedException>(() => PrefabGameAssetBuildValidation.Validate());
        }

        [Test]
        public void CandidatesSkipUnrelatedSavedSceneButFindPlainAuthoringWithoutIndexedDefinitions()
        {
            var originalActiveScene = SceneManager.GetActiveScene();
            var mode = string.IsNullOrEmpty(originalActiveScene.path) ? NewSceneMode.Single : NewSceneMode.Additive;
            var testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            var unrelatedPath = _assets.Folder + "/unrelated.unity";
            var invalidPath = _assets.Folder + "/plain-authoring.unity";
            try
            {
                var root = new GameObject("plain-authoring");
                SceneManager.MoveGameObjectToScene(root, testScene);
                Assert.That(EditorSceneManager.SaveScene(testScene, unrelatedPath), Is.True);
                root.AddComponent<GameAssetAuthoring>().SetComponents(new[] { new PrefabAuthoringAlpha_GAC() });
                Assert.That(EditorSceneManager.SaveScene(testScene, invalidPath), Is.True);
            }
            finally
            {
                if (SceneManager.sceneCount > 1)
                {
                    EditorSceneManager.CloseScene(testScene, true);
                    if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
                        SceneManager.SetActiveScene(originalActiveScene);
                }
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            var entries = PrefabGameAssetIndexEditorUtility.CollectEntries();
            Assert.That(entries, Is.Empty);
            var candidates = PrefabGameAssetOverrideEditorUtility.CollectHierarchyAssetPaths(entries);
            Assert.That(candidates, Does.Not.Contain(unrelatedPath));
            Assert.That(candidates, Does.Contain(invalidPath));
            LogAssert.Expect(LogType.Error, new Regex(@"\[Prefab GameAsset content\].*plain-authoring\.unity.*source GUID 'unresolved'.*plain scene objects.*Fallback:.*safe Try\*"));
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries, Is.Empty);
            Assert.That(index.Overrides, Is.Empty);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void CandidatesFindInvalidChildOnlyPrefabWithoutIndexedDefinitionsAndClearStaleMetadata()
        {
            var root = new GameObject("child-only");
            var path = _assets.Folder + "/child-only.prefab";
            try
            {
                var child = new GameObject("invalid-child");
                child.transform.SetParent(root.transform);
                var authoring = child.AddComponent<GameAssetAuthoring>();
                authoring.SetComponents(new[] { new PrefabAuthoringAlpha_GAC() });
                authoring.SetRuntimeDefinitionSource("obsolete-source-guid", "obsolete-instance-id");
                PrefabUtility.SaveAsPrefabAsset(root, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
            var entries = PrefabGameAssetIndexEditorUtility.CollectEntries();
            Assert.That(entries, Is.Empty);
            Assert.That(PrefabGameAssetOverrideEditorUtility.CollectHierarchyAssetPaths(entries), Does.Contain(path));
            LogAssert.Expect(LogType.Error, new Regex(@"\[Prefab GameAsset content\].*child-only\.prefab.*invalid-child.*source GUID 'unresolved'.*Fallback:.*safe Try\*"));
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries, Is.Empty);
            Assert.That(index.Overrides, Is.Empty);
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(path).GetComponentInChildren<GameAssetAuthoring>(true);
            Assert.That(saved.SourceDefinitionGuid, Is.Null.Or.Empty);
            Assert.That(saved.RuntimeOverrideId, Is.Null.Or.Empty);
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void InvalidRootDefinitionRemainsIndexedAndReportsInertFallbackWithoutBlockingPreparation()
        {
            var authoring = _assets.CreatePrefab("invalid-root", new GameAssetComponent[] { new PrefabAuthoringBeta_GAC(), new PrefabAuthoringBeta_GAC() });
            var diagnostic = new Regex(@"\[Prefab GameAsset content\].*invalid-root\.prefab.*source GUID '[a-f0-9]{32}'.*PrefabAuthoringBeta_GAC.*Fallback: an empty inert definition");
            LogAssert.Expect(LogType.Error, diagnostic);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            Assert.That(index.Entries.Single().Authoring, Is.SameAs(authoring));
            Assert.That(PrefabGameAssetBuildValidation.CollectErrors().Any(error => error.Contains("invalid-root.prefab") && error.Contains(nameof(PrefabAuthoringBeta_GAC))), Is.True);
            LogAssert.Expect(LogType.Error, diagnostic);
            Assert.DoesNotThrow(() => new PrefabGameAssetBuildProcessor().PrepareForBuild(null));
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void RejectedSavedInstanceUsesSourceDefinitionAndOneDiagnosticInReconcileAndBuild()
        {
            var source = _assets.CreatePrefab("fallback-source", new[] { new PrefabAuthoringAlpha_GAC { Value = 5 } });
            PrefabGameAssetIndexEditorUtility.Reconcile();
            var sourceGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source));
            var container = new GameObject("rejected-instance");
            var containerPath = _assets.Folder + "/rejected-instance.prefab";
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source.gameObject);
                instance.transform.SetParent(container.transform);
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                authoring.SetComponents(new GameAssetComponent[] { new PrefabAuthoringAlpha_GAC { Value = 25 }, new PrefabAuthoringBeta_GAC() });
                authoring.SetRuntimeDefinitionSource(sourceGuid, "obsolete-valid-instance-id");
                PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                PrefabUtility.SaveAsPrefabAsset(container, containerPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(container);
            }
            var diagnostic = new Regex(@"\[Prefab GameAsset content\].*rejected-instance\.prefab.*source GUID '" + sourceGuid + @"'.*cannot add or remove manual GAC.*Fallback: the source prefab definition");
            LogAssert.Expect(LogType.Error, diagnostic);
            var index = PrefabGameAssetIndexEditorUtility.Reconcile();
            LogAssert.NoUnexpectedReceived();
            Assert.That(index.Overrides, Is.Empty);
            var saved = AssetDatabase.LoadAssetAtPath<GameObject>(containerPath).GetComponentInChildren<GameAssetAuthoring>(true);
            Assert.That(saved.SourceDefinitionGuid, Is.EqualTo(sourceGuid));
            Assert.That(saved.RuntimeOverrideId, Is.Null.Or.Empty);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(index, out var definitions, out var error), Is.True, error);
            using (definitions)
            {
                Assert.That(definitions.TryGetAsset(saved, out var fallback), Is.True);
                Assert.That(((PrefabAuthoringAlpha_GAC)fallback.Components.Single()).Value, Is.EqualTo(5));
                Assert.That(fallback.GUID, Is.EqualTo(Hash128.Parse(sourceGuid)));
            }
            var hash = index.Entries.Single().ContentHash;
            LogAssert.Expect(LogType.Error, diagnostic);
            Assert.DoesNotThrow(() => new PrefabGameAssetBuildProcessor().PrepareForBuild(null));
            LogAssert.NoUnexpectedReceived();
            Assert.That(index.Entries.Single().ContentHash, Is.EqualTo(hash));
            Assert.That(index.Overrides, Is.Empty);
        }

        [Test]
        public void ReferencePickerAcceptsPrefabAssetsAndRejectsSceneAndNestedObjects()
        {
            var prefab = _assets.CreatePrefab("reference");
            Assert.That(GameAssetAuthoringEditorUtility.TryGetReferenceAuthoring(prefab.gameObject, out var resolved, out var error), Is.True, error);
            Assert.That(resolved, Is.SameAs(prefab));
            Assert.That(GameAssetAuthoringEditorUtility.TryGetReferenceAuthoring(prefab, out resolved, out error), Is.True, error);
            Assert.That(resolved, Is.SameAs(prefab));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            try
            {
                Assert.That(GameAssetAuthoringEditorUtility.TryGetReferenceAuthoring(instance, out resolved, out error), Is.False);
                Assert.That(resolved, Is.Null);
                Assert.That(error, Is.Not.Empty);
                Assert.That(GameAssetAuthoringEditorUtility.TryGetReferenceAuthoring(null, out resolved, out error), Is.True, error);
                Assert.That(resolved, Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void SceneInstanceAllowsValuesButRejectsManualAndConverterCompositionChanges()
        {
            var prefab = _assets.CreatePrefab("instance", new[] { new PrefabAuthoringBeta_GAC() }, true);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            try
            {
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                var converter = (PrefabAuthoringTransformConverter)authoring.Converters.Single().Converter;
                converter.Offset = 20;
                instance.transform.localPosition = Vector3.right * 3;
                Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out var definition, out var guid, out var error), Is.True, error);
                Assert.That(definition, Is.SameAs(prefab));
                Assert.That(guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab))));
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out var collection, out error), Is.True, error);
                Assert.That(collection.Components.OfType<PrefabAuthoringAlpha_GAC>().Single().Value, Is.EqualTo(23));
                Assert.That(PrefabGameAssetIndexEditorUtility.CollectEntries(), Has.Count.EqualTo(1));

                converter.IncludeEarly = false;
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out _, out error), Is.False, "A value that changes converted output types is a structural override.");
                converter.IncludeEarly = true;
                authoring.SetComponents(new GameAssetComponent[] { new PrefabAuthoringBeta_GAC(), new PrefabAuthoringDerivedAlpha_GAC() });
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out _, out error), Is.False);
                authoring.SetComponents(new GameAssetComponent[] { new PrefabAuthoringEarly_GAC() });
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out _, out error), Is.False);
                authoring.SetComponents(new[] { new PrefabAuthoringBeta_GAC() });
                authoring.SetConverters(Array.Empty<GameAssetAuthoringConverterBinding>());
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out _, out error), Is.False);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void InstanceValueOverridesSupportUndoAndRedo()
        {
            var prefab = _assets.CreatePrefab("undo", new[] { new PrefabAuthoringAlpha_GAC { Value = 1 } });
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            try
            {
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                Undo.IncrementCurrentGroup();
                Undo.RecordObject(authoring, "Change prefab authoring test value");
                ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = 2;
                PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out var collection, out var error), Is.True, error);
                Assert.That(collection.Components.OfType<PrefabAuthoringAlpha_GAC>().Single().Value, Is.EqualTo(1));
                Undo.PerformRedo();
                Assert.That(GameAssetAuthoringEditorUtility.TryCollect(authoring, out collection, out error), Is.True, error);
                Assert.That(collection.Components.OfType<PrefabAuthoringAlpha_GAC>().Single().Value, Is.EqualTo(2));
                Undo.ClearUndo(authoring);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void BuildValidationFindsInvalidInstancesInScenesOutsideBuildSettingsWithoutChangingLoadedScenes()
        {
            var prefab = _assets.CreatePrefab("scene-definition", new[] { new PrefabAuthoringAlpha_GAC() });
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            var originalActiveScene = SceneManager.GetActiveScene();
            var scenePath = _assets.Folder + "/outside-build-settings.unity";
            // Unity Test Framework backs up and restores user scenes. Its untitled test scene
            // cannot accept NewScene(Additive), so replace only that runner-owned scene.
            var mode = string.IsNullOrEmpty(originalActiveScene.path) ? NewSceneMode.Single : NewSceneMode.Additive;
            var testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            try
            {
                // NewScene can unload unused asset wrappers; resolve the persistent prefab again.
                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefabAsset, testScene);
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                authoring.SetComponents(new GameAssetComponent[] { new PrefabAuthoringAlpha_GAC(), new PrefabAuthoringBeta_GAC() });
                PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                Assert.That(EditorSceneManager.SaveScene(testScene, scenePath), Is.True);
            }
            finally
            {
                if (SceneManager.sceneCount > 1)
                {
                    EditorSceneManager.CloseScene(testScene, true);
                    if (originalActiveScene.IsValid() && originalActiveScene.isLoaded)
                        SceneManager.SetActiveScene(originalActiveScene);
                }
                else
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            }
            Assert.That(EditorBuildSettings.scenes.Any(scene => scene.path == scenePath), Is.False);
            Assert.That(PrefabGameAssetOverrideEditorUtility.CollectHierarchyAssetPaths(Array.Empty<PrefabGameAssetIndexEntry>()), Does.Contain(scenePath));
            var before = EditorSceneManager.GetSceneManagerSetup();
            var dirtyBefore = Enumerable.Range(0, SceneManager.sceneCount).Select(index => SceneManager.GetSceneAt(index).isDirty).ToArray();
            var errors = PrefabGameAssetBuildValidation.CollectErrors();
            Assert.That(errors.Any(error => error.Contains(scenePath) && error.Contains("scene-definition")), Is.True);
            LogAssert.Expect(LogType.Error, new Regex(@"\[Prefab GameAsset content\].*outside-build-settings\.unity.*scene-definition.*Fallback: the source prefab definition"));
            Assert.DoesNotThrow(() => PrefabGameAssetBuildValidation.Validate());
            var after = EditorSceneManager.GetSceneManagerSetup();
            Assert.That(after.Select(scene => (scene.path, scene.isLoaded, scene.isActive)), Is.EqualTo(before.Select(scene => (scene.path, scene.isLoaded, scene.isActive))));
            Assert.That(Enumerable.Range(0, SceneManager.sceneCount).Select(index => SceneManager.GetSceneAt(index).isDirty), Is.EqualTo(dirtyBefore));
        }

        [Test]
        public void PlainSceneAuthoringIsInvalidAndHiddenConvertersAreNotOffered()
        {
            var root = new GameObject("plain scene authoring");
            try
            {
                var authoring = root.AddComponent<GameAssetAuthoring>();
                authoring.SetComponents(new[] { new PrefabAuthoringAlpha_GAC() });
                Assert.That(GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out _, out _, out var error), Is.False);
                Assert.That(error, Is.Not.Empty);
                Assert.That(GameAssetAuthoringEditorUtility.GetConverterTypes(null), Is.Empty);
                var types = GameAssetAuthoringEditorUtility.GetConverterTypes(root.transform);
                Assert.That(types.Contains(typeof(PrefabAuthoringTransformConverter)), Is.False);
                foreach (var type in types)
                {
                    var converter = (GameAssetAuthoringConverter)Activator.CreateInstance(type);
                    Assert.That(converter.SourceComponentType.IsAssignableFrom(typeof(Transform)), Is.True);
                    Assert.That(type.IsAbstract, Is.False);
                    Assert.That(type.IsSerializable, Is.True);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
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
