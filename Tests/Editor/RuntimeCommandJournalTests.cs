using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeCommandJournalTestCommand : GameRuntimeComponent, ICommandLogic, IRuntimeCommandJournalScopeProvider
    {
        public int Value;
        public int ExecutionCount;
        public string StoreId;
        public string[] StoreIds;

        public RuntimeCommandJournalScope GetRuntimeCommandJournalScope(GameRuntimeCommand command)
        {
            return StoreIds != null
                ? new RuntimeCommandJournalScope(StoreIds)
                : new RuntimeCommandJournalScope(StoreId);
        }

        public void Execute(GameRuntimeCommand command)
        {
            ExecutionCount++;
        }
    }

    public class RuntimeCommandJournalTests
    {
        private const uint COMMAND_TYPE_ID = 4201;

        [Test]
        public void Scope_IsSortedAndRequiresCompleteStoreSetCoverage()
        {
            var scope = new RuntimeCommandJournalScope("snake", "map", "units");
            var partial = new RuntimeCommandJournalScope("map", "units");

            Assert.That(scope.StoreIds[0].ToString(), Is.EqualTo("map"));
            Assert.That(scope.StoreIds[1].ToString(), Is.EqualTo("snake"));
            Assert.That(scope.StoreIds[2].ToString(), Is.EqualTo("units"));
            Assert.That(scope.Covers(partial), Is.True);
            Assert.That(partial.Covers(scope), Is.False);
            Assert.That(scope.Covers(RuntimeCommandJournalScope.Session), Is.False);
            Assert.That(RuntimeCommandJournalScope.Session.Covers(scope), Is.True);
            Assert.Throws<ArgumentException>(() => new RuntimeCommandJournalScope("map", "map"));
            Assert.Throws<ArgumentException>(() => new RuntimeCommandJournalScope(Array.Empty<string>()));
        }

        [Test]
        public void Journal_InputsAndOutcomesShareSequenceAndFilterByCompleteScope()
        {
            var journal = new RuntimeCommandJournal(() => 10d);
            var policy = new RuntimeCommandJournalRetentionPolicy(100, 10_000, 60d);
            journal.ConfigureWindow("session", RuntimeCommandJournalScope.Session, policy);
            journal.ConfigureWindow("map", new RuntimeCommandJournalScope("map"), policy);
            var map = new RuntimeCommandJournalScope("map");
            var input = new RuntimeEncodedCommand(1, 1, new byte[] { 1 });
            var outcome = new RuntimeEncodedCommand(2, 1, new byte[] { 2 });

            var first = journal.AppendInput(12, input, map);
            var second = journal.AppendOutcomeBatch(
                12,
                outcome,
                new RuntimeCommandJournalScope("profile"));

            Assert.That(first.Sequence, Is.EqualTo(1));
            Assert.That(second.Sequence, Is.EqualTo(2));
            Assert.That(journal.Cursor, Is.EqualTo(2));

            var sessionRead = journal.ReadAfter("session", 0);
            Assert.That(sessionRead.Succeeded, Is.True);
            Assert.That(sessionRead.Entries.Count, Is.EqualTo(2));
            Assert.That(sessionRead.ScannedThroughCursor, Is.EqualTo(2));

            var mapRead = journal.ReadAfter("map", 0);
            Assert.That(mapRead.Succeeded, Is.True);
            Assert.That(mapRead.Entries.Count, Is.EqualTo(1));
            Assert.That(mapRead.Entries[0].Sequence, Is.EqualTo(1));
            Assert.That(mapRead.ScannedThroughCursor, Is.EqualTo(2));
        }

        [Test]
        public void Journal_PartialScopeInvalidatesEveryOverlappingRecoveryWindow()
        {
            var journal = new RuntimeCommandJournal(() => 10d);
            var policy =
                new RuntimeCommandJournalRetentionPolicy(
                    100,
                    10_000,
                    60d);
            journal.ConfigureWindow(
                "session",
                RuntimeCommandJournalScope.Session,
                policy);
            journal.ConfigureWindow(
                "map",
                new RuntimeCommandJournalScope("map"),
                policy);

            Assert.Throws<RuntimeCommandJournalScopeCoverageException>(
                () => journal.AppendOutcomeBatch(
                    12,
                    new RuntimeEncodedCommand(
                        2,
                        1,
                        new byte[] { 2 }),
                    new RuntimeCommandJournalScope(
                        "map",
                        "profile")));

            Assert.That(journal.Cursor, Is.Zero);
            Assert.That(
                journal.TryGetWindow(
                    "map",
                    out var mapWindow),
                Is.True);
            Assert.That(mapWindow.NeedsCheckpoint, Is.True);
            Assert.That(mapWindow.RecoveryAvailable, Is.False);
            Assert.That(
                journal.TryGetWindow(
                    "session",
                    out var sessionWindow),
                Is.True);
            Assert.That(sessionWindow.NeedsCheckpoint, Is.True);
            Assert.That(
                sessionWindow.RecoveryAvailable,
                Is.False);
        }

        [Test]
        public void CommandBus_RejectsPartialScopeBeforeExecutingInput()
        {
            var registry = CreateRegistry();
            var journal = new RuntimeCommandJournal(() => 1d);
            journal.ConfigureWindow(
                "map",
                new RuntimeCommandJournalScope("map"),
                new RuntimeCommandJournalRetentionPolicy(
                    10,
                    1_000,
                    60d));
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                registry,
                journal: journal);
            var logic = new RuntimeCommandJournalTestCommand
            {
                StoreId = "map",
                StoreIds = new[] { "map", "profile" },
                Value = 10,
            };
            var input = new GameRuntimeCommand();
            input.AddOrReplace(logic);
            RuntimeCommandExecutionResult result = default;
            bus.CommandCompleted += value => result = value;

            bus.Enqueue(input);
            bus.Drain(5);

            Assert.That(logic.ExecutionCount, Is.Zero);
            Assert.That(result.Failed, Is.True);
            Assert.That(
                result.Exception,
                Is.TypeOf<
                    RuntimeCommandJournalScopeCoverageException>());
            Assert.That(journal.Cursor, Is.Zero);
            Assert.That(
                journal.TryGetWindow(
                    "map",
                    out var window),
                Is.True);
            Assert.That(window.RecoveryAvailable, Is.True);
        }

        [Test]
        public void Journal_WarnsBeforeLimitAndInvalidatesRecoveryAfterHardOverflow()
        {
            var now = 0d;
            var journal = new RuntimeCommandJournal(() => now);
            journal.ConfigureWindow(
                "session",
                RuntimeCommandJournalScope.Session,
                new RuntimeCommandJournalRetentionPolicy(
                    maxEntries: 4,
                    maxPayloadBytes: 100,
                    maxAgeSeconds: 100d,
                    checkpointWarningRatio: 0.75d));
            var needsCheckpointCount = 0;
            var overflowCount = 0;
            journal.WindowNeedsCheckpoint += _ => needsCheckpointCount++;
            journal.WindowOverflowed += _ => overflowCount++;
            var encoded = new RuntimeEncodedCommand(1, 1, new byte[] { 1 });

            journal.AppendInput(1, encoded, RuntimeCommandJournalScope.Session);
            journal.AppendInput(1, encoded, RuntimeCommandJournalScope.Session);
            journal.AppendInput(1, encoded, RuntimeCommandJournalScope.Session);

            Assert.That(journal.TryGetWindow("session", out var warning), Is.True);
            Assert.That(warning.NeedsCheckpoint, Is.True);
            Assert.That(warning.RecoveryAvailable, Is.True);
            Assert.That(needsCheckpointCount, Is.EqualTo(1));

            journal.AppendInput(1, encoded, RuntimeCommandJournalScope.Session);
            journal.AppendInput(1, encoded, RuntimeCommandJournalScope.Session);

            Assert.That(journal.TryGetWindow("session", out var overflow), Is.True);
            Assert.That(overflow.RecoveryAvailable, Is.False);
            Assert.That(overflow.NeedsCheckpoint, Is.True);
            Assert.That(overflowCount, Is.EqualTo(1));
            Assert.That(
                journal.ReadAfter("session", 0).Status,
                Is.EqualTo(RuntimeCommandJournalReadStatus.RecoveryUnavailable));

            journal.CommitCheckpoint("session", journal.Cursor);

            Assert.That(journal.TryGetWindow("session", out var recovered), Is.True);
            Assert.That(recovered.RecoveryAvailable, Is.True);
            Assert.That(recovered.NeedsCheckpoint, Is.False);
            Assert.That(recovered.CheckpointCursor, Is.EqualTo(journal.Cursor));
        }

        [Test]
        public void Journal_AgeLimitIsEvaluatedWithoutAnotherAppend()
        {
            var now = 1d;
            var journal = new RuntimeCommandJournal(() => now);
            journal.ConfigureWindow(
                "session",
                RuntimeCommandJournalScope.Session,
                new RuntimeCommandJournalRetentionPolicy(10, 100, 10d, 0.5d));
            journal.AppendInput(
                1,
                new RuntimeEncodedCommand(1, 1, new byte[] { 1 }),
                RuntimeCommandJournalScope.Session);

            now = 7d;
            Assert.That(journal.TryGetWindow("session", out var warning), Is.True);
            Assert.That(warning.NeedsCheckpoint, Is.True);
            Assert.That(warning.RecoveryAvailable, Is.True);

            now = 12d;
            Assert.That(journal.TryGetWindow("session", out var overflow), Is.True);
            Assert.That(overflow.RecoveryAvailable, Is.False);
        }

        [Test]
        public void CommandBus_OutcomeBatchIsRecordOnlyAndSharesInputSequence()
        {
            var registry = CreateRegistry();
            var journal = new RuntimeCommandJournal(() => 1d);
            journal.ConfigureWindow(
                "map",
                new RuntimeCommandJournalScope("map"),
                new RuntimeCommandJournalRetentionPolicy(10, 1_000, 60d));
            var bus = new RuntimeCommandsBus(
                RuntimeCommandsBusMode.ExternalTickBarrier,
                registry,
                journal: journal);
            var inputLogic = new RuntimeCommandJournalTestCommand
            {
                StoreId = "map",
                Value = 10,
            };
            var input = new GameRuntimeCommand();
            input.AddOrReplace(inputLogic);

            bus.Enqueue(input);
            bus.Drain(5);

            var outcomeLogic = new RuntimeCommandJournalTestCommand
            {
                StoreId = "map",
                Value = 20,
            };
            var outcome = new GameRuntimeCommand();
            outcome.AddOrReplace(outcomeLogic);
            var outcomeEntry = bus.AppendOutcomeBatch(5, outcome);

            Assert.That(inputLogic.ExecutionCount, Is.EqualTo(1));
            Assert.That(outcomeLogic.ExecutionCount, Is.Zero);
            Assert.That(outcomeEntry.Sequence, Is.EqualTo(2));
            Assert.That(bus.JournalSequence, Is.EqualTo(2));
            Assert.That(journal.ReadAfter("map", 0).Entries.Count, Is.EqualTo(2));
        }

        [Test]
        public void ConfigureWindow_RejectsDefaultPolicyBeforeMutation()
        {
            var journal = new RuntimeCommandJournal();

            Assert.Throws<ArgumentException>(() =>
                journal.ConfigureWindow(
                    "invalid",
                    RuntimeCommandJournalScope.Session,
                    default));
            Assert.That(journal.TryGetWindow("invalid", out _), Is.False);
        }

        private static RuntimeReplayCommandRegistry CreateRegistry()
        {
            var registry = new RuntimeReplayCommandRegistry();
            registry.Register<RuntimeCommandJournalTestCommand>(
                COMMAND_TYPE_ID,
                "tests.journal.command",
                1,
                (command, _) =>
                {
                    var logic = command.Get<RuntimeCommandJournalTestCommand>();
                    var store = System.Text.Encoding.UTF8.GetBytes(logic.StoreId);
                    var payload = new byte[8 + store.Length];
                    Buffer.BlockCopy(BitConverter.GetBytes(logic.Value), 0, payload, 0, 4);
                    Buffer.BlockCopy(BitConverter.GetBytes(store.Length), 0, payload, 4, 4);
                    Buffer.BlockCopy(store, 0, payload, 8, store.Length);
                    return payload;
                },
                (payload, _) =>
                {
                    var value = BitConverter.ToInt32(payload, 0);
                    var storeLength = BitConverter.ToInt32(payload, 4);
                    var store = System.Text.Encoding.UTF8.GetString(payload, 8, storeLength);
                    var command = new GameRuntimeCommand();
                    command.AddOrReplace(new RuntimeCommandJournalTestCommand
                    {
                        Value = value,
                        StoreId = store,
                    });
                    return command;
                });
            registry.Seal();
            return registry;
        }
    }
}
