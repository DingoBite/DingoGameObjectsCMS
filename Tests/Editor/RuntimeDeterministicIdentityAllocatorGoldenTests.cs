using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeDeterministicIdentityAllocatorGoldenTests
    {
        [Test]
        public void Next_PreservesSha256GuidBytes()
        {
            using var first = new RuntimeDeterministicIdentityAllocator(0, "scope");
            Assert.That(first.Next().ToString(), Is.EqualTo("b6fd666e27f7255a3353cb2f23a26221"));
            Assert.That(first.Next().ToString(), Is.EqualTo("54a6599aa75d80a9f5102699ba585dba"));

            using var utf8 = new RuntimeDeterministicIdentityAllocator(-7, "\u00e9", 9);
            Assert.That(utf8.Next().ToString(), Is.EqualTo("c985273f0818ca16e6e7f7e0b97bf030"));

            using var highSequence = new RuntimeDeterministicIdentityAllocator(42, "players", ulong.MaxValue - 1);
            Assert.That(highSequence.Next().ToString(), Is.EqualTo("a52967140887a120565227b2e9057fe3"));
        }

        [Test]
        public void SessionReplacementAndRestore_KeepTheExpectedSequence()
        {
            try
            {
                RuntimeInstanceIdentity.BeginDeterministicSession(0, "scope");
                Assert.That(RuntimeInstanceIdentity.Next().ToString(), Is.EqualTo("b6fd666e27f7255a3353cb2f23a26221"));
                var state = RuntimeInstanceIdentity.State;

                RuntimeInstanceIdentity.BeginDeterministicSession(42, "players", ulong.MaxValue - 1);
                Assert.That(RuntimeInstanceIdentity.Next().ToString(), Is.EqualTo("a52967140887a120565227b2e9057fe3"));

                RuntimeInstanceIdentity.Restore(state);
                Assert.That(RuntimeInstanceIdentity.Next().ToString(), Is.EqualTo("54a6599aa75d80a9f5102699ba585dba"));
            }
            finally
            {
                RuntimeInstanceIdentity.EndDeterministicSession();
            }

            Assert.That(RuntimeInstanceIdentity.HasDeterministicSession, Is.False);
        }
    }
}
