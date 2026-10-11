using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using NUnit.Framework;
using Unity.Entities;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeStoreFlushStateTestComponent : GameRuntimeComponent
    {
    }

    public class RuntimeStoreFlushStateTests
    {
        private const uint COMPONENT_TYPE_ID = uint.MaxValue;
        private World _world;
        private RuntimeStore _store;
        private GameObject _ownedCoroutineParent;

        [SetUp]
        public void SetUp()
        {
            if (CoroutineParent.GetNoCheck() == null)
            {
                _ownedCoroutineParent = new GameObject(nameof(RuntimeStoreFlushStateTests));
                _ownedCoroutineParent.AddComponent<CoroutineParent>();
            }

            RuntimeStores.ResetState();
            _world = new World(nameof(RuntimeStoreFlushStateTests));
            RuntimeStores.SetupWorld(_world);
            _world.GetOrCreateSystemManaged<EndSimulationEntityCommandBufferSystem>();
            _store = RuntimeStores.GetOrAddRuntimeStore("flush-state-tests");
        }

        [TearDown]
        public void TearDown()
        {
            RuntimeStores.ResetState();
            if (_world != null && _world.IsCreated)
                _world.Dispose();
            if (_ownedCoroutineParent != null)
                Object.DestroyImmediate(_ownedCoroutineParent);
        }

        [Test]
        public void IsFlushInProgress_IsFalseOutsideFlushEvenWhenChangesAreQueued()
        {
            var callbacks = new List<string>();
            SubscribePublishStages(callbacks.Add);

            Assert.That(_store.IsFlushInProgress, Is.False);
            Assert.That(_store.FlushToQuiescence(), Is.Zero);
            Assert.That(_store.IsFlushInProgress, Is.False);
            Assert.That(callbacks, Is.Empty);

            QueueAllPublishStages();

            Assert.That(_store.IsFlushInProgress, Is.False);
            Assert.That(callbacks, Is.Empty);
            Assert.That(_store.FlushToQuiescence(), Is.EqualTo(1));
            Assert.That(_store.IsFlushInProgress, Is.False);
            Assert.That(callbacks, Is.EqualTo(ExpectedPublishStages()));

            callbacks.Clear();
            Assert.That(_store.FlushToQuiescence(), Is.Zero);
            Assert.That(_store.IsFlushInProgress, Is.False);
            Assert.That(callbacks, Is.Empty);
        }

        [Test]
        public void IsFlushInProgress_IsTrueDuringEveryLivePublishStage()
        {
            var callbacks = new List<string>();
            var states = new List<bool>();
            var retiredStates = new List<bool>();
            SubscribePublishStages(stage =>
            {
                callbacks.Add(stage);
                states.Add(_store.IsFlushInProgress);
                retiredStates.Add(_store.Retired);
            });
            QueueAllPublishStages();

            Assert.That(_store.FlushToQuiescence(), Is.EqualTo(1));

            Assert.That(callbacks, Is.EqualTo(ExpectedPublishStages()));
            Assert.That(states, Is.All.True);
            Assert.That(retiredStates, Is.All.False);
            Assert.That(_store.IsFlushInProgress, Is.False);
        }

        [Test]
        public void FlushToQuiescence_RemainsRejectedInsideEveryLivePublishStage()
        {
            var callbacks = new List<string>();
            var statesBefore = new List<bool>();
            var statesAfter = new List<bool>();
            var failures = new List<Exception>();
            SubscribePublishStages(stage =>
            {
                callbacks.Add(stage);
                statesBefore.Add(_store.IsFlushInProgress);
                Exception failure = null;
                try
                {
                    _store.FlushToQuiescence();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                failures.Add(failure);
                statesAfter.Add(_store.IsFlushInProgress);
            });
            QueueAllPublishStages();

            Assert.That(_store.FlushToQuiescence(), Is.EqualTo(1));

            Assert.That(callbacks, Is.EqualTo(ExpectedPublishStages()));
            Assert.That(statesBefore, Is.All.True);
            Assert.That(statesAfter, Is.All.True);
            foreach (var failure in failures)
            {
                Assert.That(failure, Is.TypeOf<InvalidOperationException>());
                Assert.That(failure.Message, Does.Contain("cannot synchronously flush while another flush is in progress"));
            }
            Assert.That(_store.IsFlushInProgress, Is.False);
            Assert.That(_store.FlushToQuiescence(), Is.Zero);
        }

        [TestCase(nameof(RuntimeStore.StructureChanges))]
        [TestCase(nameof(RuntimeStore.ComponentStructureChanges))]
        [TestCase(nameof(RuntimeStore.ComponentChanges))]
        [TestCase(nameof(RuntimeStore.CommittedBatch))]
        [TestCase(nameof(RuntimeStore.DirtyPublishCompleted))]
        public void ThrowingObserver_PreservesFlushStateForLaterObserversAndStages(string throwingStage)
        {
            var callbacks = new List<string>();
            var states = new List<bool>();
            var expectedCallbacks = new List<string>();
            var message = $"Expected flush-state observer failure in {throwingStage}.";
            SubscribePublishStages(stage =>
            {
                callbacks.Add($"{stage}:first");
                states.Add(_store.IsFlushInProgress);
                if (stage == throwingStage)
                    throw new InvalidOperationException(message);
            });
            SubscribePublishStages(stage =>
            {
                callbacks.Add($"{stage}:second");
                states.Add(_store.IsFlushInProgress);
            });
            foreach (var stage in ExpectedPublishStages())
            {
                expectedCallbacks.Add($"{stage}:first");
                expectedCallbacks.Add($"{stage}:second");
            }
            QueueAllPublishStages();
            LogAssert.Expect(LogType.Exception, new Regex(Regex.Escape(message)));

            Assert.That(_store.FlushToQuiescence(), Is.EqualTo(1));

            Assert.That(callbacks, Is.EqualTo(expectedCallbacks));
            Assert.That(states, Is.All.True);
            Assert.That(_store.IsFlushInProgress, Is.False);
            Assert.That(_store.FlushToQuiescence(), Is.Zero);
            LogAssert.NoUnexpectedReceived();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RemoveRuntimeStore_InFinalPublishCallbackClearsFlushStateBeforeReturning(bool matchGeneration)
        {
            var callbackCount = 0;
            var inProgressBeforeRemoval = false;
            var retiredBeforeRemoval = true;
            var removed = false;
            var inProgressAfterRemoval = true;
            var retiredAfterRemoval = false;
            _store.DirtyPublishCompleted += _ =>
            {
                callbackCount++;
                inProgressBeforeRemoval = _store.IsFlushInProgress;
                retiredBeforeRemoval = _store.Retired;
                removed = matchGeneration
                    ? RuntimeStores.RemoveRuntimeStore(_store.Id, _store.StoreGeneration, _store.Realm)
                    : RuntimeStores.RemoveRuntimeStore(_store.Id, _store.Realm);
                inProgressAfterRemoval = _store.IsFlushInProgress;
                retiredAfterRemoval = _store.Retired;
            };
            QueueAllPublishStages();

            Assert.That(_store.FlushToQuiescence(), Is.EqualTo(1));

            Assert.That(callbackCount, Is.EqualTo(1));
            Assert.That(inProgressBeforeRemoval, Is.True);
            Assert.That(retiredBeforeRemoval, Is.False);
            Assert.That(removed, Is.True);
            Assert.That(inProgressAfterRemoval, Is.False);
            Assert.That(retiredAfterRemoval, Is.True);
            Assert.That(_store.Retired, Is.True);
            Assert.That(_store.IsFlushInProgress, Is.False);
            Assert.That(RuntimeStores.GetRuntimeStore(_store.Id, _store.Realm), Is.Null);
        }

        private void QueueAllPublishStages()
        {
            var runtimeObject = _store.Create();
            runtimeObject.AddOrReplaceById(COMPONENT_TYPE_ID, new RuntimeStoreFlushStateTestComponent());
            _store.SetDirty(runtimeObject.InstanceId, COMPONENT_TYPE_ID);
        }

        private void SubscribePublishStages(Action<string> observer)
        {
            _store.StructureChanges += _ => observer(nameof(RuntimeStore.StructureChanges));
            _store.ComponentStructureChanges += _ => observer(nameof(RuntimeStore.ComponentStructureChanges));
            _store.ComponentChanges += _ => observer(nameof(RuntimeStore.ComponentChanges));
            _store.CommittedBatch += _ => observer(nameof(RuntimeStore.CommittedBatch));
            _store.DirtyPublishCompleted += _ => observer(nameof(RuntimeStore.DirtyPublishCompleted));
        }

        private string[] ExpectedPublishStages()
        {
            return new[]
            {
                nameof(RuntimeStore.StructureChanges),
                nameof(RuntimeStore.ComponentStructureChanges),
                nameof(RuntimeStore.ComponentChanges),
                nameof(RuntimeStore.CommittedBatch),
                nameof(RuntimeStore.DirtyPublishCompleted),
            };
        }
    }
}
