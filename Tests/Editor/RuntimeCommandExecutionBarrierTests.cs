using System;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.Stores;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeCommandExecutionBarrierTests
    {
        [TestCase("enqueue")]
        [TestCase("range")]
        [TestCase("dispatch")]
        [TestCase("recorded")]
        [TestCase("drain")]
        public void BlockedEntryDoesNotMutateQueueJournalOrExecute(string entry)
        {
            var bus = new RuntimeCommandsBus(RuntimeCommandsBusMode.ExternalTickBarrier);
            var logic = new RuntimeCommandJournalTestCommand();
            var command = new GameRuntimeCommand();
            command.AddOrReplace(logic);
            bus.Enqueue(command);
            var owner = new object();
            bus.BlockExecution(owner);
            var sequence = bus.JournalSequence;
            Assert.Throws<InvalidOperationException>(() =>
            {
                switch (entry)
                {
                    case "enqueue": bus.Enqueue(command); break;
                    case "range": bus.EnqueueRange(new[] { command, command }); break;
                    case "dispatch":
                        var authority = default(RuntimeExecutionState);
                        bus.Dispatch(command, in authority);
                        break;
                    case "recorded": bus.ExecuteRecordedCommand(command); break;
                    case "drain": bus.Drain(7); break;
                }
            });
            Assert.That(bus.QueuedCount, Is.EqualTo(1));
            Assert.That(bus.JournalSequence, Is.EqualTo(sequence));
            Assert.That(bus.HasDrainedTick, Is.False);
            Assert.That(logic.ExecutionCount, Is.Zero);
            bus.UnblockExecution(owner);
            Assert.That(bus.Drain(7), Is.EqualTo(1));
            Assert.That(logic.ExecutionCount, Is.EqualTo(1));
        }

        [Test]
        public void OwnersCannotReleaseEachOtherAndCoordinatorResetRemainsAvailable()
        {
            var bus = new RuntimeCommandsBus(RuntimeCommandsBusMode.ExternalTickBarrier);
            // Value equality must not let one owner release another owner's barrier.
            var first = new string('x', 1);
            var second = new string('x', 1);
            bus.Enqueue(new GameRuntimeCommand());
            bus.BlockExecution(first);
            bus.BlockExecution(first);
            bus.BlockExecution(second);
            bus.Clear();
            bus.ResetReplayBoundary(11, 0);
            Assert.That(bus.QueuedCount, Is.Zero);
            Assert.That(bus.ExecutionBlocked, Is.True);
            bus.UnblockExecution(new object());
            bus.UnblockExecution(first);
            Assert.That(bus.ExecutionBlocked, Is.True);
            bus.UnblockExecution(second);
            Assert.That(bus.ExecutionBlocked, Is.False);
            Assert.DoesNotThrow(() => bus.Enqueue(new GameRuntimeCommand()));
            Assert.Throws<ArgumentNullException>(() => bus.BlockExecution(null));
            Assert.Throws<ArgumentNullException>(() => bus.UnblockExecution(null));
        }

        [Test]
        public void BlockCannotBeginHalfwayThroughDrain()
        {
            var bus = new RuntimeCommandsBus(RuntimeCommandsBusMode.ExternalTickBarrier);
            bus.BeforeExecute += _ => Assert.Throws<InvalidOperationException>(() => bus.BlockExecution(this));
            bus.Enqueue(new GameRuntimeCommand());
            Assert.That(bus.Drain(1), Is.EqualTo(1));
            Assert.That(bus.ExecutionBlocked, Is.False);
        }
    }
}
