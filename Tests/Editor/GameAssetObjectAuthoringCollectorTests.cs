using System;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Tests.Editor
{
    [Serializable, Preserve, GameAssetSetupOrder(-10)]
    public class AuthoringCollectorEarly_GAC : GameAssetComponent { }

    [Serializable, Preserve]
    public class AuthoringCollectorAlpha_GAC : GameAssetComponent { }

    [Serializable, Preserve]
    public class AuthoringCollectorLate_GAC : GameAssetComponent { }

    public class AuthoringCollectorEarlySource : MonoBehaviour, IGameAssetComponentAuthoring
    {
        public GameAssetComponent BuildGameAssetComponent() => new AuthoringCollectorEarly_GAC();
    }

    public class AuthoringCollectorAlphaSource : MonoBehaviour, IGameAssetComponentAuthoring
    {
        public GameAssetComponent BuildGameAssetComponent() => new AuthoringCollectorAlpha_GAC();
    }

    public class AuthoringCollectorLateSource : MonoBehaviour, IGameAssetComponentAuthoring
    {
        public GameAssetComponent BuildGameAssetComponent() => new AuthoringCollectorLate_GAC();
    }

    public class AuthoringCollectorNullSource : MonoBehaviour, IGameAssetComponentAuthoring
    {
        public GameAssetComponent BuildGameAssetComponent() => null;
    }

    public class GameAssetObjectAuthoringCollectorTests
    {
        private GameObject _rootObject;
        private GameAssetObjectAuthoring _root;

        [SetUp]
        public void SetUp()
        {
            _rootObject = new GameObject("Authoring root");
            _root = _rootObject.AddComponent<GameAssetObjectAuthoring>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_rootObject != null)
                UnityEngine.Object.DestroyImmediate(_rootObject);
        }

        [Test]
        public void CollectsDisabledSourcesInSetupOrderThenTypeName()
        {
            AddChild<AuthoringCollectorLateSource>("late");
            var alpha = AddChild<AuthoringCollectorAlphaSource>("alpha");
            alpha.enabled = false;
            AddChild<AuthoringCollectorEarlySource>("early");

            var success = GameAssetObjectAuthoringCollector.TryCollect(_root, out var components, out var error);

            Assert.That(success, Is.True, error);
            Assert.That(components, Has.Count.EqualTo(3));
            Assert.That(components[0], Is.TypeOf<AuthoringCollectorEarly_GAC>());
            Assert.That(components[1], Is.TypeOf<AuthoringCollectorAlpha_GAC>());
            Assert.That(components[2], Is.TypeOf<AuthoringCollectorLate_GAC>());
        }

        [Test]
        public void SkipsNestedAuthoringRootAndItsWholeSubtree()
        {
            AddChild<AuthoringCollectorAlphaSource>("outer");
            var nestedObject = new GameObject("nested authoring root");
            nestedObject.transform.SetParent(_rootObject.transform);
            nestedObject.AddComponent<GameAssetObjectAuthoring>();
            nestedObject.AddComponent<AuthoringCollectorLateSource>();
            var nestedChild = new GameObject("nested child");
            nestedChild.transform.SetParent(nestedObject.transform);
            nestedChild.AddComponent<AuthoringCollectorEarlySource>();

            var success = GameAssetObjectAuthoringCollector.TryCollect(_root, out var components, out var error);

            Assert.That(success, Is.True, error);
            Assert.That(components, Has.Count.EqualTo(1));
            Assert.That(components[0], Is.TypeOf<AuthoringCollectorAlpha_GAC>());
        }

        [Test]
        public void DuplicateComponentTypesFailWithoutReturningPartialResults()
        {
            AddChild<AuthoringCollectorAlphaSource>("first");
            AddChild<AuthoringCollectorAlphaSource>("second");

            var success = GameAssetObjectAuthoringCollector.TryCollect(_root, out var components, out var error);

            Assert.That(success, Is.False);
            Assert.That(components, Is.Empty);
            Assert.That(error, Does.Contain(nameof(AuthoringCollectorAlpha_GAC)));
        }

        [Test]
        public void MissingAndNullComponentsFail()
        {
            var missing = GameAssetObjectAuthoringCollector.TryCollect(_root, out var missingComponents, out var missingError);
            Assert.That(missing, Is.False);
            Assert.That(missingComponents, Is.Empty);
            Assert.That(missingError, Is.Not.Empty);

            AddChild<AuthoringCollectorNullSource>("null");
            var returnedNull = GameAssetObjectAuthoringCollector.TryCollect(_root, out var nullComponents, out var nullError);
            Assert.That(returnedNull, Is.False);
            Assert.That(nullComponents, Is.Empty);
            Assert.That(nullError, Does.Contain("null"));
        }

        private T AddChild<T>(string name) where T : MonoBehaviour
        {
            var child = new GameObject(name);
            child.transform.SetParent(_rootObject.transform);
            return child.AddComponent<T>();
        }
    }
}
