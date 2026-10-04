using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Scripting;
using UnityEngine.TestTools;

namespace DingoGameObjectsCMS.Tests.Editor
{
    [Serializable, Preserve, HideInTypeMenu, GameAssetSetupOrder(-10)]
    public class PrefabAuthoringEarly_GAC : GameAssetComponent { }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringAlpha_GAC : GameAssetComponent
    {
        public int Value;
        public List<int> Values = new();
        public Texture Resource;
    }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringBeta_GAC : GameAssetComponent { }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringDerivedAlpha_GAC : PrefabAuthoringAlpha_GAC { }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringTransformConverter : GameAssetAuthoringConverter
    {
        public int Offset;
        public bool IncludeEarly = true;
        public Texture Resource;

        public override Type SourceComponentType => typeof(Transform);

        public override IReadOnlyList<GameAssetComponent> Convert(Component source)
        {
            var transform = (Transform)source;
            var components = new List<GameAssetComponent> { new PrefabAuthoringAlpha_GAC { Value = Mathf.RoundToInt(transform.localPosition.x) + Offset, Values = new List<int> { Offset }, Resource = Resource } };
            if (IncludeEarly)
                components.Add(new PrefabAuthoringEarly_GAC());
            return components;
        }
    }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringDuplicateConverter : GameAssetAuthoringConverter
    {
        public override Type SourceComponentType => typeof(Transform);

        public override IReadOnlyList<GameAssetComponent> Convert(Component source)
        {
            return new GameAssetComponent[] { new PrefabAuthoringBeta_GAC(), new PrefabAuthoringBeta_GAC() };
        }
    }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringNullConverter : GameAssetAuthoringConverter
    {
        public bool NullList;

        public override Type SourceComponentType => typeof(Transform);

        public override IReadOnlyList<GameAssetComponent> Convert(Component source)
        {
            return NullList ? null : new GameAssetComponent[] { null };
        }
    }

    [Serializable, Preserve, HideInTypeMenu]
    public class PrefabAuthoringThrowingConverter : GameAssetAuthoringConverter
    {
        public override Type SourceComponentType => typeof(Transform);

        public override IReadOnlyList<GameAssetComponent> Convert(Component source)
        {
            throw new InvalidOperationException("test conversion failure");
        }
    }

    public class GameAssetAuthoringCollectorTests
    {
        private GameObject _object;
        private GameAssetAuthoring _authoring;

        [SetUp]
        public void SetUp()
        {
            _object = new GameObject("Prefab authoring collector test");
            _authoring = _object.AddComponent<GameAssetAuthoring>();
        }

        [TearDown]
        public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(_object);
        }

        [Test]
        public void CombinesManualAndMultipleConvertedComponentsInDeterministicOrder()
        {
            _authoring.SetComponents(new[] { new PrefabAuthoringBeta_GAC() });
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(_object.transform, new PrefabAuthoringTransformConverter { Offset = 7 }) });

            Assert.That(GameAssetAuthoringCollector.TryCollect(_authoring, out var collection, out var error), Is.True, error);
            Assert.That(collection.Components, Has.Count.EqualTo(3));
            Assert.That(collection.Components[0], Is.TypeOf<PrefabAuthoringEarly_GAC>());
            Assert.That(collection.Components[1], Is.TypeOf<PrefabAuthoringAlpha_GAC>());
            Assert.That(collection.Components[2], Is.TypeOf<PrefabAuthoringBeta_GAC>());
            Assert.That(((PrefabAuthoringAlpha_GAC)collection.Components[1]).Value, Is.EqualTo(7));
            Assert.That(collection.Sources, Has.Count.EqualTo(collection.Components.Count));
            Assert.That(collection.Sources[0].Kind, Is.EqualTo(GameAssetAuthoringSourceKind.Converted));
            Assert.That(collection.Sources[0].Component, Is.SameAs(_object.transform));
            Assert.That(collection.Sources[1].Kind, Is.EqualTo(GameAssetAuthoringSourceKind.Converted));
            Assert.That(collection.Sources[2].Kind, Is.EqualTo(GameAssetAuthoringSourceKind.Manual));
            Assert.That(collection.Sources[2].Index, Is.EqualTo(0));
        }

        [Test]
        public void DuplicateExactTypeFailsWithoutPartialCollection()
        {
            _authoring.SetComponents(new GameAssetComponent[] { new PrefabAuthoringBeta_GAC(), new PrefabAuthoringBeta_GAC() });
            AssertFailure(nameof(PrefabAuthoringBeta_GAC));
        }

        [Test]
        public void DistinctDerivedTypesRemainDistinctComponents()
        {
            _authoring.SetComponents(new GameAssetComponent[] { new PrefabAuthoringAlpha_GAC(), new PrefabAuthoringDerivedAlpha_GAC() });
            Assert.That(GameAssetAuthoringCollector.TryCollect(_authoring, out var collection, out var error), Is.True, error);
            Assert.That(collection.Components, Has.Count.EqualTo(2));
        }

        [Test]
        public void ManualAndConvertedConflictFailsWithoutReplacement()
        {
            _authoring.SetComponents(new[] { new PrefabAuthoringAlpha_GAC { Value = 100 } });
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(_object.transform, new PrefabAuthoringTransformConverter()) });
            AssertFailure(nameof(PrefabAuthoringAlpha_GAC));
        }

        [Test]
        public void DuplicateConverterOutputFails()
        {
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(_object.transform, new PrefabAuthoringDuplicateConverter()) });
            AssertFailure(nameof(PrefabAuthoringBeta_GAC));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void OwnedDescendantSourcesPreserveTheirIdentityAndNativeValues(bool inactiveOwner, bool inactiveParent)
        {
            var parent = new GameObject("native hierarchy");
            parent.transform.SetParent(_object.transform, false);
            var child = new GameObject("native source");
            child.transform.SetParent(parent.transform, false);
            child.transform.localPosition = new Vector3(11, 0, 0);
            parent.SetActive(!inactiveParent);
            _object.SetActive(!inactiveOwner);
            var converter = new PrefabAuthoringTransformConverter { Offset = 7 };
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(child.transform, converter) });

            Assert.That(GameAssetAuthoringCollector.TryCollect(_authoring, out var collection, out var error), Is.True, error);
            Assert.That(collection.Components, Has.Count.EqualTo(2));
            Assert.That(((PrefabAuthoringAlpha_GAC)collection.Components[1]).Value, Is.EqualTo(18));
            Assert.That(collection.Sources[1].Kind, Is.EqualTo(GameAssetAuthoringSourceKind.Converted));
            Assert.That(collection.Sources[1].Component, Is.SameAs(child.transform));
            Assert.That(collection.Sources[1].Converter, Is.SameAs(converter));
            Assert.That(child.transform.parent, Is.SameAs(parent.transform), "Conversion must not rebuild or move the native hierarchy.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SourcesOutsideTheOwnedHierarchyAreRejected(bool inactiveSource)
        {
            var outside = new GameObject("outside source");
            try
            {
                outside.SetActive(!inactiveSource);
                _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(outside.transform, new PrefabAuthoringTransformConverter()) });
                AssertFailure(_object.name, outside.name, "owned hierarchy");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(outside);
            }
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void AnotherAuthoringOwnsItsRootAndDescendantSources(bool descendantSource, bool inactiveNestedOwner)
        {
            var nested = new GameObject("nested definition");
            nested.transform.SetParent(_object.transform, false);
            nested.AddComponent<GameAssetAuthoring>();
            var child = new GameObject("nested source");
            child.transform.SetParent(nested.transform, false);
            nested.SetActive(!inactiveNestedOwner);
            var source = descendantSource ? child.transform : nested.transform;
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(source, new PrefabAuthoringTransformConverter()) });
            AssertFailure(_object.name, source.name, "another GameAssetAuthoring");
        }

        [Test]
        public void SameSourceCannotBeAssignedTwice()
        {
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(_object.transform, new PrefabAuthoringTransformConverter()), new GameAssetAuthoringConverterBinding(_object.transform, new PrefabAuthoringDuplicateConverter()) });
            AssertFailure();
        }

        [Test]
        public void IncompatibleSourceIsRejectedBeforeConversion()
        {
            var collider = _object.AddComponent<BoxCollider>();
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(collider, new PrefabAuthoringTransformConverter()) });
            AssertFailure();
        }

        [Test]
        public void MissingSourceOrConverterIsRejected()
        {
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(null, new PrefabAuthoringTransformConverter()) });
            AssertFailure();
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(_object.transform, null) });
            AssertFailure();
        }

        [Test]
        public void NullManualOrConvertedDataIsRejected()
        {
            _authoring.SetComponents(new GameAssetComponent[] { null });
            AssertFailure();
            _authoring.SetComponents(Array.Empty<GameAssetComponent>());
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(_object.transform, new PrefabAuthoringNullConverter { NullList = true }) });
            AssertFailure();
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(_object.transform, new PrefabAuthoringNullConverter()) });
            AssertFailure();
        }

        [Test]
        public void ConverterExceptionHasAuthoringAndConverterContext()
        {
            _authoring.SetConverters(new[] { new GameAssetAuthoringConverterBinding(_object.transform, new PrefabAuthoringThrowingConverter()) });
            LogAssert.Expect(LogType.Exception, new Regex("test conversion failure"));
            AssertFailure("test conversion failure", nameof(PrefabAuthoringThrowingConverter), _object.name);
        }

        private void AssertFailure(params string[] diagnostics)
        {
            Assert.That(GameAssetAuthoringCollector.TryCollect(_authoring, out var collection, out var error), Is.False);
            Assert.That(collection == null || collection.Components.Count == 0, Is.True, "A failed collection must not expose partial components.");
            Assert.That(error, Is.Not.Empty);
            foreach (var diagnostic in diagnostics)
            {
                Assert.That(error, Does.Contain(diagnostic));
            }
        }
    }
}
