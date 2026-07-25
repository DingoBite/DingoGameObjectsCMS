using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplayCmsValueCommand20260725 : GameRuntimeComponent, ICommandLogic
    {
        public int Value;
        public string Marker;
        public bool ThrowAfterMutation;

        public void Execute(GameRuntimeCommand command)
        {
            Value += 100;
            if (ThrowAfterMutation)
                throw new InvalidOperationException("runtime replay command failure");
        }
    }

    public class RuntimeReplayCmsAlternateCommand20260725 : GameRuntimeComponent, ICommandLogic
    {
        public int ExecutionCount;

        public void Execute(GameRuntimeCommand command)
        {
            ExecutionCount++;
        }
    }

    public class RuntimeReplayCmsScopedCommand20260725 :
        GameRuntimeComponent,
        ICommandLogic,
        IRuntimeReplayStoreScopedCommand
    {
        public FixedString32Bytes StoreId;
        public int ExecutionCount;

        public RuntimeReplayStoreScopeDisposition ClassifyReplayStoreScope(
            RuntimeReplayStoreScope storeScope)
        {
            return storeScope.Contains(StoreId)
                ? RuntimeReplayStoreScopeDisposition.Included
                : RuntimeReplayStoreScopeDisposition.OutsideScope;
        }

        public void Execute(GameRuntimeCommand command)
        {
            ExecutionCount++;
        }
    }

    public class RuntimeReplayCmsFoundation20260725Tests
    {
        private const uint VALUE_TYPE_ID = 71;
        private const uint ALTERNATE_TYPE_ID = 72;

        [Test]
        public void StoreScope_IsExplicitUniqueAndDeterministicallyOrdered()
        {
            var scope = new RuntimeReplayStoreScope(
                "snake",
                "map",
                "mob");

            Assert.That(scope.Count, Is.EqualTo(3));
            Assert.That(scope.TakeStoreId(0).ToString(), Is.EqualTo("map"));
            Assert.That(scope.TakeStoreId(1).ToString(), Is.EqualTo("mob"));
            Assert.That(scope.TakeStoreId(2).ToString(), Is.EqualTo("snake"));
            Assert.That(
                scope.Contains(new FixedString32Bytes("mob")),
                Is.True);
            Assert.Throws<ArgumentException>(
                () => new RuntimeReplayStoreScope("map", "map"));
            Assert.Throws<ArgumentException>(
                () => new RuntimeReplayStoreScope(Array.Empty<string>()));
        }

        [Test]
        public void Registry_SealIsOrderIndependentAndLocksCatalog()
        {
            var first = CreateRegistry(registerValueFirst: true);
            var second = CreateRegistry(registerValueFirst: false);

            var firstHash = first.Seal();
            var secondHash = second.Seal();

            Assert.That(firstHash, Is.EqualTo(secondHash));
            Assert.That(firstHash, Does.Match("^[0-9a-f]{64}$"));
            Assert.That(first.Count, Is.EqualTo(2));
            Assert.That(first.IsSealed, Is.True);
            Assert.Throws<InvalidOperationException>(() =>
                first.RegisterPayloadMigration(
                    VALUE_TYPE_ID,
                    1,
                    "tests.value.v1-to-v2.again",
                    MigrateValueV1ToV2));
        }

        [Test]
        public void Registry_DecodeAppliesEveryPayloadMigrationToCurrentCodec()
        {
            var registry = new RuntimeReplayCommandRegistry();
            registry.Register<RuntimeReplayCmsValueCommand20260725>(
                VALUE_TYPE_ID,
                "tests.value",
                2,
                EncodeValue,
                DecodeValue);
            registry.RegisterPayloadMigration(
                VALUE_TYPE_ID,
                1,
                "tests.value.v1-to-v2",
                MigrateValueV1ToV2);
            registry.Seal();

            var oldWriter = new CanonicalPatchBinaryWriter();
            oldWriter.WriteInt32(17);
            var decoded = registry.Decode(
                new RuntimeEncodedCommand(
                    VALUE_TYPE_ID,
                    1,
                    oldWriter.ToArray()));

            Assert.That(
                decoded.TryGet<RuntimeReplayCmsValueCommand20260725>(
                    out var value),
                Is.True);
            Assert.That(value.Value, Is.EqualTo(17));
            Assert.That(value.Marker, Is.EqualTo("migrated-v1"));
        }

        [Test]
        public void RuntimeReplayObjectRef_RoundTripsStableStoreAndGuid()
        {
            var source = new RuntimeInstance
            {
                StoreId = new FixedString32Bytes("replay-test"),
                Id = 41,
                Epoch = 9,
            };
            var objectGuid = Hash128.Compute("runtime-replay-object-ref-20260725");
            var context = new RuntimePersistentPatchCodecContext(
                instance =>
                {
                    Assert.That(instance.Id, Is.EqualTo(source.Id));
                    Assert.That(instance.Epoch, Is.EqualTo(source.Epoch));
                    return new RuntimePatchObjectReference(
                        instance.StoreId,
                        objectGuid);
                },
                reference =>
                {
                    Assert.That(reference.StoreId, Is.EqualTo(source.StoreId));
                    Assert.That(reference.ObjectGuid, Is.EqualTo(objectGuid));
                    return source;
                });

            var stableReference =
                RuntimeReplayObjectRef.FromRuntimeInstance(source, context);
            var writer = new CanonicalPatchBinaryWriter();
            stableReference.Write(writer);
            var reader = new CanonicalPatchBinaryReader(writer.ToArray());
            var restoredReference = RuntimeReplayObjectRef.Read(reader);
            reader.RequireEnd();
            var restoredInstance = restoredReference.Resolve(context);

            Assert.That(restoredReference, Is.EqualTo(stableReference));
            Assert.That(restoredReference.InstanceGuid, Is.EqualTo(objectGuid));
            Assert.That(restoredInstance.StoreId, Is.EqualTo(source.StoreId));
            Assert.That(restoredInstance.Id, Is.EqualTo(source.Id));
            Assert.That(restoredInstance.Epoch, Is.EqualTo(source.Epoch));
        }

        [Test]
        public void ExternalDrain_RecordsSnapshotBeforeExecutionAndSuccessOnly()
        {
            var registry = CreateValueRegistry();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                registry);
            var logic = new RuntimeReplayCmsValueCommand20260725
            {
                Value = 7,
                Marker = "before",
            };
            var command = new GameRuntimeCommand();
            command.AddOrReplace(logic);
            var journalCount = 0;
            var afterExecuteCount = 0;
            var completedCount = 0;
            var journalEntry = default(RuntimeCommandJournalEntry);
            var result = default(RuntimeCommandExecutionResult);
            bus.JournalEntryRecorded += value =>
            {
                journalCount++;
                journalEntry = value;
            };
            bus.AfterExecute += _ => afterExecuteCount++;
            bus.CommandCompleted += value =>
            {
                completedCount++;
                result = value;
            };

            bus.Enqueue(command);

            Assert.That(logic.Value, Is.EqualTo(7));
            Assert.That(bus.QueuedCount, Is.EqualTo(1));
            Assert.That(bus.Drain(applyBeforeTick: 42), Is.EqualTo(1));

            var replayed = registry.Decode(journalEntry.EncodedCommand);
            Assert.That(
                replayed.TryGet<RuntimeReplayCmsValueCommand20260725>(
                    out var replayedLogic),
                Is.True);
            Assert.That(logic.Value, Is.EqualTo(107));
            Assert.That(replayedLogic.Value, Is.EqualTo(7));
            Assert.That(replayedLogic.Marker, Is.EqualTo("before"));
            Assert.That(journalEntry.ApplyBeforeTick, Is.EqualTo(42));
            Assert.That(journalEntry.Sequence, Is.EqualTo(1));
            Assert.That(journalCount, Is.EqualTo(1));
            Assert.That(afterExecuteCount, Is.EqualTo(1));
            Assert.That(completedCount, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(RuntimeCommandExecutionStatus.Succeeded));
            Assert.That(result.HasJournalEntry, Is.True);
            Assert.That(result.ApplyBeforeTick, Is.EqualTo(42));
            Assert.That(bus.LastApplyBeforeTick, Is.EqualTo(42));
        }

        [Test]
        public void ExternalDrain_FailedExecutionProducesNoJournalEntry()
        {
            var registry = CreateValueRegistry();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                registry);
            var logic = new RuntimeReplayCmsValueCommand20260725
            {
                Value = 3,
                Marker = "failure",
                ThrowAfterMutation = true,
            };
            var command = new GameRuntimeCommand();
            command.AddOrReplace(logic);
            var journalCount = 0;
            var afterExecuteCount = 0;
            var failedCount = 0;
            var result = default(RuntimeCommandExecutionResult);
            bus.JournalEntryRecorded += _ => journalCount++;
            bus.AfterExecute += _ => afterExecuteCount++;
            bus.ExecuteFailed += (_, _) => failedCount++;
            bus.CommandCompleted += value => result = value;

            bus.Enqueue(command);
            bus.Drain(applyBeforeTick: 5);

            Assert.That(logic.Value, Is.EqualTo(103));
            Assert.That(journalCount, Is.Zero);
            Assert.That(afterExecuteCount, Is.Zero);
            Assert.That(failedCount, Is.EqualTo(1));
            Assert.That(result.Status, Is.EqualTo(RuntimeCommandExecutionStatus.Failed));
            Assert.That(result.HasEncodedCommand, Is.True);
            Assert.That(result.HasJournalEntry, Is.False);
            Assert.That(result.Exception, Is.TypeOf<InvalidOperationException>());
        }

        [Test]
        public void ExternalDrain_UnregisteredLogicalCommandExecutesLiveButIsNotJournaled()
        {
            var registry = CreateValueRegistry();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                registry);
            var logic = new RuntimeReplayCmsAlternateCommand20260725();
            var command = new GameRuntimeCommand();
            command.AddOrReplace(logic);
            var beforeExecuteCount = 0;
            var journalCount = 0;
            var result = default(RuntimeCommandExecutionResult);
            bus.BeforeExecute += _ => beforeExecuteCount++;
            bus.JournalEntryRecorded += _ => journalCount++;
            bus.CommandCompleted += value => result = value;

            bus.Enqueue(command);
            bus.Drain(applyBeforeTick: 11);

            Assert.That(beforeExecuteCount, Is.EqualTo(1));
            Assert.That(logic.ExecutionCount, Is.EqualTo(1));
            Assert.That(journalCount, Is.Zero);
            Assert.That(result.Status, Is.EqualTo(RuntimeCommandExecutionStatus.Unsupported));
            Assert.That(result.HasEncodedCommand, Is.False);
            Assert.That(result.HasJournalEntry, Is.False);
            Assert.That(result.Exception, Is.TypeOf<NotSupportedException>());
        }

        [Test]
        public void ExternalDrain_OutOfScopeUnregisteredCommandExecutesWithoutReplayFault()
        {
            var registry = CreateValueRegistry();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                registry);
            var storeScope =
                new RuntimeReplayStoreScope("session");
            bus.SetReplayStoreScope(storeScope);
            var logic = new RuntimeReplayCmsScopedCommand20260725
            {
                StoreId = new FixedString32Bytes("meta"),
            };
            var command = new GameRuntimeCommand();
            command.AddOrReplace(logic);
            var journalCount = 0;
            var result = default(RuntimeCommandExecutionResult);
            bus.JournalEntryRecorded += _ => journalCount++;
            bus.CommandCompleted += value => result = value;

            bus.Enqueue(command);
            bus.Drain(applyBeforeTick: 12);

            Assert.That(logic.ExecutionCount, Is.EqualTo(1));
            Assert.That(journalCount, Is.Zero);
            Assert.That(result.Status,
                Is.EqualTo(RuntimeCommandExecutionStatus.Succeeded));
            Assert.That(result.ReplayJournalExcluded, Is.True);
            Assert.That(result.HasEncodedCommand, Is.False);
            Assert.That(result.HasJournalEntry, Is.False);
            Assert.That(result.Exception, Is.Null);
            Assert.That(bus.JournalSequence, Is.Zero);
            bus.ClearReplayStoreScope(storeScope);
        }

        [Test]
        public void ExternalDrain_ActiveScopeRequiresExplicitCommandClassification()
        {
            var registry = CreateValueRegistry();
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                registry);
            var storeScope =
                new RuntimeReplayStoreScope("session");
            bus.SetReplayStoreScope(storeScope);
            var logic = new RuntimeReplayCmsValueCommand20260725
            {
                Value = 9,
                Marker = "unclassified",
            };
            var command = new GameRuntimeCommand();
            command.AddOrReplace(logic);
            var journalCount = 0;
            var result = default(RuntimeCommandExecutionResult);
            bus.JournalEntryRecorded += _ => journalCount++;
            bus.CommandCompleted += value => result = value;

            bus.Enqueue(command);
            bus.Drain(applyBeforeTick: 13);

            Assert.That(logic.Value, Is.EqualTo(109));
            Assert.That(journalCount, Is.Zero);
            Assert.That(result.Status,
                Is.EqualTo(RuntimeCommandExecutionStatus.Unsupported));
            Assert.That(result.ReplayJournalExcluded, Is.False);
            Assert.That(result.Exception, Is.TypeOf<NotSupportedException>());
            Assert.That(
                result.Exception.Message,
                Does.Contain("scope classifier"));
            bus.ClearReplayStoreScope(storeScope);
        }

        private static RuntimeReplayCommandRegistry CreateRegistry(
            bool registerValueFirst)
        {
            var registry = new RuntimeReplayCommandRegistry();
            if (registerValueFirst)
            {
                RegisterValue(registry);
                RegisterAlternate(registry);
            }
            else
            {
                RegisterAlternate(registry);
                RegisterValue(registry);
            }

            return registry;
        }

        private static RuntimeReplayCommandRegistry CreateValueRegistry()
        {
            var registry = new RuntimeReplayCommandRegistry();
            RegisterValue(registry);
            registry.Seal();
            return registry;
        }

        private static void RegisterValue(
            RuntimeReplayCommandRegistry registry)
        {
            registry.Register<RuntimeReplayCmsValueCommand20260725>(
                VALUE_TYPE_ID,
                "tests.value",
                2,
                EncodeValue,
                DecodeValue);
            registry.RegisterPayloadMigration(
                VALUE_TYPE_ID,
                1,
                "tests.value.v1-to-v2",
                MigrateValueV1ToV2);
        }

        private static void RegisterAlternate(
            RuntimeReplayCommandRegistry registry)
        {
            registry.Register<RuntimeReplayCmsAlternateCommand20260725>(
                ALTERNATE_TYPE_ID,
                "tests.alternate",
                1,
                (_, _) => Array.Empty<byte>(),
                (_, _) =>
                {
                    var command = new GameRuntimeCommand();
                    command.AddOrReplace(
                        new RuntimeReplayCmsAlternateCommand20260725());
                    return command;
                });
        }

        private static byte[] EncodeValue(
            GameRuntimeCommand command,
            RuntimePersistentPatchCodecContext context)
        {
            if (!command.TryGet<RuntimeReplayCmsValueCommand20260725>(
                    out var value))
            {
                throw new InvalidOperationException(
                    "Value command component is missing.");
            }

            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteInt32(value.Value);
            writer.WriteString(value.Marker);
            writer.WriteBoolean(value.ThrowAfterMutation);
            return writer.ToArray();
        }

        private static GameRuntimeCommand DecodeValue(
            byte[] payload,
            RuntimePersistentPatchCodecContext context)
        {
            var reader = new CanonicalPatchBinaryReader(payload);
            var value = new RuntimeReplayCmsValueCommand20260725
            {
                Value = reader.ReadInt32(),
                Marker = reader.ReadString(),
                ThrowAfterMutation = reader.ReadBoolean(),
            };
            reader.RequireEnd();
            var command = new GameRuntimeCommand();
            command.AddOrReplace(value);
            return command;
        }

        private static byte[] MigrateValueV1ToV2(
            byte[] payload,
            RuntimePersistentPatchCodecContext context)
        {
            var reader = new CanonicalPatchBinaryReader(payload);
            var value = reader.ReadInt32();
            reader.RequireEnd();
            var writer = new CanonicalPatchBinaryWriter();
            writer.WriteInt32(value);
            writer.WriteString("migrated-v1");
            writer.WriteBoolean(false);
            return writer.ToArray();
        }
    }
}
