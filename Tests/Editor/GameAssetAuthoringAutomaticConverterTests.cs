using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Editor.Authoring;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Tests.Editor
{
    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringAutoBoxAConverter : GameAssetAuthoringConverter
    {
        public int Value = 13;
        public override Type SourceComponentType => typeof(BoxCollider);
        public override IReadOnlyList<GameAssetComponent> Convert(Component source) => new GameAssetComponent[] { new PrefabAuthoringAlpha_GAC { Value = Value } };
    }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringAutoBoxZConverter : GameAssetAuthoringConverter
    {
        public override Type SourceComponentType => typeof(BoxCollider);
        public override IReadOnlyList<GameAssetComponent> Convert(Component source) => new GameAssetComponent[] { new PrefabAuthoringBeta_GAC() };
    }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringSelfReferenceConverter : GameAssetAuthoringConverter
    {
        public string Guid;
        public override Type SourceComponentType => typeof(Transform);

        public override bool Prepare(Component source, Func<UnityEngine.Object, string> resourceGuid)
        {
            var guid = resourceGuid(source.GetComponentInParent<GameAssetAuthoring>(true).gameObject);
            if (Guid == guid)
                return false;
            Guid = guid;
            return true;
        }

        public override IReadOnlyList<GameAssetComponent> Convert(Component source) => Array.Empty<GameAssetComponent>();
    }

    public class GameAssetAuthoringAutomaticConverterTests
    {
        private PrefabGameAssetTestAssets _assets;

        [SetUp]
        public void SetUp() => _assets = new PrefabGameAssetTestAssets();

        [TearDown]
        public void TearDown() => _assets.Dispose();

        [Test]
        public void AddsFirstStableMatchWithoutChangingManualDataAndRemainsIdempotent()
        {
            var prefab = CreatePrefab("automatic");
            prefab.SetComponents(new[] { new PrefabAuthoringBeta_GAC() });
            Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(prefab, ConverterTypes), Is.True);
            Assert.That(prefab.Converters, Has.Count.EqualTo(1));
            Assert.That(prefab.Converters[0].Converter, Is.TypeOf<PrefabAuthoringAutoBoxAConverter>());
            Assert.That(prefab.Converters[0].Source, Is.SameAs(prefab.GetComponent<BoxCollider>()));
            Assert.That(prefab.Components, Has.Count.EqualTo(1));
            Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(prefab, ConverterTypes), Is.False);
            Assert.That(GameAssetAuthoringCollector.TryCollect(prefab, out var collection, out var error), Is.True, error);
            Assert.That(collection.Components, Has.Count.EqualTo(2));
            Assert.That(collection.Components.OfType<PrefabAuthoringAlpha_GAC>().Single().Value, Is.EqualTo(13));
        }

        [Test]
        public void ExistingConfiguredConverterAndNoneArePreservedWithoutRecreation()
        {
            var prefab = CreatePrefab("preserved", true);
            var existing = new PrefabAuthoringAutoBoxZConverter();
            var child = prefab.transform.GetChild(0).GetComponent<BoxCollider>();
            prefab.SetConverters(new[] { new GameAssetAuthoringConverterBinding(prefab.GetComponent<BoxCollider>(), existing), new GameAssetAuthoringConverterBinding(child, null) });
            Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(prefab, ConverterTypes), Is.False);
            Assert.That(prefab.Converters[0].Converter, Is.SameAs(existing));
            Assert.That(prefab.Converters[1].Converter, Is.Null);
            Assert.That(prefab.Converters[1].Source, Is.SameAs(child));
        }

        [Test]
        public void AppendingAnotherOwnedSourcePreservesConfiguredValuesAndBindingOrder()
        {
            var prefab = CreatePrefab("append", true);
            var configured = new PrefabAuthoringAutoBoxAConverter { Value = 47 };
            var original = new GameAssetAuthoringConverterBinding(prefab.GetComponent<BoxCollider>(), configured);
            prefab.SetConverters(new[] { original });
            Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(prefab, ConverterTypes), Is.True);
            Assert.That(prefab.Converters[0], Is.SameAs(original));
            Assert.That(prefab.Converters[0].Converter, Is.SameAs(configured));
            Assert.That(configured.Value, Is.EqualTo(47));
            Assert.That(prefab.Converters[1].Source, Is.SameAs(prefab.transform.GetChild(0).GetComponent<BoxCollider>()));
        }

        [Test]
        public void AddedBindingsParticipateInUndoAndRedo()
        {
            var prefab = CreatePrefab("undo");
            Undo.IncrementCurrentGroup();
            Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(prefab, ConverterTypes), Is.True);
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(prefab.Converters, Is.Empty);
            Undo.PerformRedo();
            Assert.That(prefab.Converters.Single().Converter, Is.TypeOf<PrefabAuthoringAutoBoxAConverter>());
            Undo.ClearUndo(prefab);
        }

        [Test]
        public void InactiveOwnedSourcesAreIncludedAndNestedAuthoringSourcesAreExcluded()
        {
            var prefab = CreatePrefab("hierarchy", true, true);
            prefab.SetConverters(new[] { new GameAssetAuthoringConverterBinding(prefab.GetComponent<BoxCollider>(), null) });
            Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(prefab, ConverterTypes), Is.True);
            Assert.That(prefab.Converters, Has.Count.EqualTo(2));
            Assert.That(prefab.Converters[1].Source, Is.SameAs(prefab.transform.GetChild(0).GetComponent<BoxCollider>()));
            Assert.That(prefab.Converters.All(binding => binding.Source.GetComponentInParent<GameAssetAuthoring>(true) == prefab), Is.True);
        }

        [Test]
        public void NoneSurvivesPrefabSerializationAndVariantInheritance()
        {
            var prefab = CreatePrefab("none");
            prefab.SetConverters(new[] { new GameAssetAuthoringConverterBinding(prefab.GetComponent<BoxCollider>(), null) });
            PrefabUtility.SavePrefabAsset(prefab.gameObject);
            var contents = PrefabUtility.LoadPrefabContents(AssetDatabase.GetAssetPath(prefab));
            try
            {
                var restored = contents.GetComponent<GameAssetAuthoring>();
                Assert.That(restored.Converters, Has.Count.EqualTo(1));
                Assert.That(restored.Converters[0].Source, Is.SameAs(contents.GetComponent<BoxCollider>()));
                Assert.That(restored.Converters[0].Converter, Is.Null);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(contents);
            }
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            try
            {
                var variant = PrefabUtility.SaveAsPrefabAsset(instance, _assets.Folder + "/none-variant.prefab").GetComponent<GameAssetAuthoring>();
                Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(variant, ConverterTypes), Is.False);
                Assert.That(variant.Converters.Single().Converter, Is.Null);
                Assert.That(GameAssetAuthoringCollector.TryCollect(variant, out var collection, out var error), Is.True, error);
                Assert.That(collection.Components, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void SceneInstancesNeverDiscoverOrAddComposition()
        {
            var prefab = CreatePrefab("instance");
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            try
            {
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                var modifications = PrefabUtility.GetPropertyModifications(authoring);
                var discovered = false;
                Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(authoring, source => { discovered = true; return ConverterTypes(source); }), Is.False);
                Assert.That(discovered, Is.False);
                Assert.That(authoring.Converters, Is.Empty);
                Assert.That(PrefabUtility.GetPropertyModifications(authoring)?.Length ?? 0, Is.EqualTo(modifications?.Length ?? 0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void SavedDefinitionsMayAddBindingsButInstancesMayNotDisableInheritedConverters()
        {
            var prefab = CreatePrefab("structure");
            Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(prefab, ConverterTypes), Is.True);
            PrefabUtility.SavePrefabAsset(prefab.gameObject);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            try
            {
                var authoring = instance.GetComponent<GameAssetAuthoring>();
                Assert.That(GameAssetAuthoringEditorUtility.TryValidateInstanceStructure(authoring, prefab, out var error), Is.True, error);
                authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(instance.GetComponent<BoxCollider>(), null) });
                Assert.That(GameAssetAuthoringEditorUtility.TryValidateInstanceStructure(authoring, prefab, out error), Is.False);
                Assert.That(error, Does.Contain("converter type"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        [Test]
        public void DefinitionWithoutSupportedSourcesDoesNotChange()
        {
            var prefab = _assets.CreatePrefab("unsupported");
            Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(prefab, ConverterTypes), Is.False);
            Assert.That(prefab.Converters, Is.Empty);
        }

        [Test]
        public void ResourcePreparationUsesTheVariantDefinitionGuidAlsoOnItsSceneInstance()
        {
            var prefab = CreatePrefab("resource");
            prefab.SetConverters(new[] { new GameAssetAuthoringConverterBinding(prefab.transform, new PrefabAuthoringSelfReferenceConverter()) });
            Assert.That(GameAssetAuthoringEditorUtility.TryPrepareConverters(prefab, out _, out var error), Is.True, error);
            var baseGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab));
            Assert.That(((PrefabAuthoringSelfReferenceConverter)prefab.Converters[0].Converter).Guid, Is.EqualTo(baseGuid));
            PrefabUtility.SavePrefabAsset(prefab.gameObject);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            GameObject variantInstance = null;
            try
            {
                var path = _assets.Folder + "/resource-variant.prefab";
                var variant = PrefabUtility.SaveAsPrefabAsset(instance, path).GetComponent<GameAssetAuthoring>();
                var variantGuid = AssetDatabase.AssetPathToGUID(path);
                Assert.That(GameAssetAuthoringEditorUtility.TryPrepareConverters(variant, out _, out error), Is.True, error);
                Assert.That(((PrefabAuthoringSelfReferenceConverter)variant.Converters[0].Converter).Guid, Is.EqualTo(variantGuid).And.Not.EqualTo(baseGuid));
                PrefabUtility.SavePrefabAsset(variant.gameObject);
                variantInstance = (GameObject)PrefabUtility.InstantiatePrefab(variant.gameObject);
                var authoring = variantInstance.GetComponent<GameAssetAuthoring>();
                ((PrefabAuthoringSelfReferenceConverter)authoring.Converters[0].Converter).Guid = null;
                Assert.That(GameAssetAuthoringEditorUtility.TryPrepareConverters(authoring, out _, out error), Is.True, error);
                Assert.That(((PrefabAuthoringSelfReferenceConverter)authoring.Converters[0].Converter).Guid, Is.EqualTo(variantGuid));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
                if (variantInstance != null)
                    UnityEngine.Object.DestroyImmediate(variantInstance);
            }
        }

        [Test]
        public void PrefabStageAppendsOwnedSourcesPreservesNoneAndResolvesItsVariantGuid()
        {
            Assert.That(PrefabStageUtility.GetCurrentPrefabStage(), Is.Null, "Run this fixture from the main stage so an unrelated open prefab is preserved.");
            var scenes = EditorSceneManager.GetSceneManagerSetup();
            var prefab = CreatePrefab("stage-source");
            prefab.SetConverters(new[] { new GameAssetAuthoringConverterBinding(prefab.transform, new PrefabAuthoringSelfReferenceConverter()), new GameAssetAuthoringConverterBinding(prefab.GetComponent<BoxCollider>(), null) });
            Assert.That(GameAssetAuthoringEditorUtility.TryPrepareConverters(prefab, out _, out var error), Is.True, error);
            PrefabUtility.SavePrefabAsset(prefab.gameObject);
            var path = _assets.Folder + "/stage-variant.prefab";
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab.gameObject);
            try
            {
                PrefabUtility.SaveAsPrefabAsset(instance, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
            PrefabStage stage = null;
            try
            {
                stage = PrefabStageUtility.OpenPrefab(path);
                Assert.That(stage, Is.Not.Null);
                var authoring = stage.prefabContentsRoot.GetComponent<GameAssetAuthoring>();
                Assert.That(GameAssetAuthoringEditorUtility.IsDefinitionAuthoring(authoring), Is.True);
                var child = new GameObject("new inactive native source");
                StageUtility.PlaceGameObjectInCurrentStage(child);
                child.transform.SetParent(authoring.transform, false);
                var source = child.AddComponent<BoxCollider>();
                child.SetActive(false);
                Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(authoring, ConverterTypes), Is.True);
                Assert.That(authoring.Converters, Has.Count.EqualTo(3));
                Assert.That(authoring.Converters[1].Source, Is.SameAs(authoring.GetComponent<BoxCollider>()));
                Assert.That(authoring.Converters[1].Converter, Is.Null);
                Assert.That(authoring.Converters[2].Source, Is.SameAs(source));
                Assert.That(authoring.Converters[2].Converter, Is.TypeOf<PrefabAuthoringAutoBoxAConverter>());
                Assert.That(GameAssetAuthoringEditorUtility.TryPrepareConverters(authoring, out _, out error), Is.True, error);
                Assert.That(((PrefabAuthoringSelfReferenceConverter)authoring.Converters[0].Converter).Guid, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));
                Assert.That(GameAssetAuthoringEditorUtility.SynchronizeConverters(authoring, ConverterTypes), Is.False);
            }
            finally
            {
                if (stage != null)
                {
                    stage.ClearDirtiness();
                    StageUtility.GoToMainStage();
                }
            }
            Assert.That(EditorSceneManager.GetSceneManagerSetup(), Is.EqualTo(scenes));
        }

        private GameAssetAuthoring CreatePrefab(string name, bool child = false, bool nested = false)
        {
            var root = new GameObject(name);
            try
            {
                root.AddComponent<BoxCollider>();
                root.AddComponent<GameAssetAuthoring>();
                if (child)
                {
                    var owned = new GameObject("inactive native source");
                    owned.transform.SetParent(root.transform, false);
                    owned.AddComponent<BoxCollider>();
                    owned.SetActive(false);
                }
                if (nested)
                {
                    var boundary = new GameObject("nested authoring");
                    boundary.transform.SetParent(root.transform, false);
                    boundary.AddComponent<BoxCollider>();
                    boundary.AddComponent<GameAssetAuthoring>();
                    boundary.SetActive(false);
                }
                return PrefabUtility.SaveAsPrefabAsset(root, _assets.Folder + "/" + name + ".prefab").GetComponent<GameAssetAuthoring>();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static IReadOnlyList<Type> ConverterTypes(Component source) => source is BoxCollider ? new[] { typeof(PrefabAuthoringAutoBoxZConverter), typeof(PrefabAuthoringAutoBoxAConverter) } : Array.Empty<Type>();
    }
}
