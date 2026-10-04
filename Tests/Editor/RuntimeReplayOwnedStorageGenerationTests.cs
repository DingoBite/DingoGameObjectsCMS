using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DingoGameObjectsCMS.Editor;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using Microsoft.CSharp;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeReplayOwnedStorageGenerationTests
    {
        private readonly Dictionary<string, Type> _compiledOwners = new();
        private string _tempRoot;
        private string _output;

        [SetUp]
        public void SetUp()
        {
            OwnedStorageProbeControl.Reset();
            _tempRoot = Path.Combine(Path.GetTempPath(), nameof(RuntimeReplayOwnedStorageGenerationTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
            _output = Path.Combine(_tempRoot, "Owner.Lifecycle.g.cs");
        }

        [TearDown]
        public void TearDown()
        {
            OwnedStorageProbeControl.ReleaseRemaining();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)]
        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.FailFast)]
        public void EmittedLifecycleUsesDeclaredOrderAndKeepsOwnersIndependent(RuntimeReplayOwnedStorageCleanupPolicy policy)
        {
            var type = CompileOwner(typeof(OwnedStorageProbeOwner), policy);
            var first = Activator.CreateInstance(type);
            Assert.That(OwnedStorageProbeControl.Events, Is.EqualTo(new[] { "allocate:1", "allocate:2", "allocate:3" }));
            var second = Activator.CreateInstance(type);
            var firstRows = ProbeRows(type, first);
            var secondRows = ProbeRows(type, second);
            foreach (var row in firstRows)
            {
                row.Value = 17;
            }
            foreach (var row in secondRows)
            {
                row.Value = 23;
            }
            Invoke(type, first, "Reset");
            Assert.That(firstRows.Select(row => row.Value), Is.EqualTo(new[] { 0, 0, 0 }));
            Assert.That(secondRows.Select(row => row.Value), Is.EqualTo(new[] { 23, 23, 23 }));
            for (var index = 0; index < firstRows.Length; index++)
            {
                Assert.That(firstRows[index], Is.Not.SameAs(secondRows[index]));
            }
            Invoke(type, first, "Dispose");
            Assert.That(firstRows.All(row => row.Disposed), Is.True);
            Assert.That(secondRows.All(row => !row.Disposed), Is.True);
            Assert.DoesNotThrow(() => Invoke(type, first, "Dispose"));
            Assert.That(OwnedStorageProbeControl.LiveCount, Is.EqualTo(3));
            Invoke(type, second, "Dispose");
            Assert.That(OwnedStorageProbeControl.LiveCount, Is.Zero);
        }

        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures, 1)]
        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures, 2)]
        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures, 3)]
        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.FailFast, 1)]
        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.FailFast, 2)]
        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.FailFast, 3)]
        public void EmittedConstructorFailureReleasesPreviouslyAllocatedRowsAndPreservesOriginalError(
            RuntimeReplayOwnedStorageCleanupPolicy policy, int failedAllocation)
        {
            var type = CompileOwner(typeof(OwnedStorageProbeOwner), policy);
            var original = new InvalidOperationException("allocation failed " + failedAllocation);
            OwnedStorageProbeControl.AllocationFailure = original;
            OwnedStorageProbeControl.FailAllocation = failedAllocation;
            var failure = Assert.Catch(() => Activator.CreateInstance(type));
            Assert.That(Unwrap(failure), Is.SameAs(original));
            Assert.That(OwnedStorageProbeControl.LiveCount, Is.Zero);
            var expected = Enumerable.Range(1, failedAllocation).Select(index => "allocate:" + index)
                .Concat(Enumerable.Range(1, failedAllocation - 1).Select(index => "dispose:" + index));
            Assert.That(OwnedStorageProbeControl.Events, Is.EqualTo(expected));
        }

        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)]
        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.FailFast)]
        public void EmittedDisposeRetainsTheSelectedFailureAndContinuationPolicy(RuntimeReplayOwnedStorageCleanupPolicy policy)
        {
            var type = CompileOwner(typeof(OwnedStorageProbeOwner), policy);
            var owner = Activator.CreateInstance(type);
            var first = new InvalidOperationException("first cleanup failed");
            var last = new InvalidOperationException("last cleanup failed");
            OwnedStorageProbeControl.CleanupFailures.Add(1, first);
            OwnedStorageProbeControl.CleanupFailures.Add(3, last);
            OwnedStorageProbeControl.Events.Clear();
            var failure = Assert.Catch(() => Invoke(type, owner, "Dispose"));
            if (policy == RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)
            {
                Assert.That(OwnedStorageProbeControl.Events, Is.EqualTo(new[] { "dispose:1", "dispose:2", "dispose:3" }));
                Assert.That(OwnedStorageProbeControl.LiveCount, Is.Zero);
                Assert.That(((AggregateException)failure).Flatten().InnerExceptions, Is.EquivalentTo(new[] { first, last }));
            }
            else
            {
                Assert.That(failure, Is.SameAs(first));
                Assert.That(OwnedStorageProbeControl.Events, Is.EqualTo(new[] { "dispose:1" }));
                Assert.That(OwnedStorageProbeControl.LiveCount, Is.EqualTo(2));
            }
        }

        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)]
        [TestCase(RuntimeReplayOwnedStorageCleanupPolicy.FailFast)]
        public void EmittedConstructorCleanupFailurePreservesEachExistingOwnerPolicy(RuntimeReplayOwnedStorageCleanupPolicy policy)
        {
            var type = CompileOwner(typeof(OwnedStorageProbeOwner), policy);
            var allocation = new InvalidOperationException("last allocation failed");
            var cleanup = new InvalidOperationException("first cleanup failed");
            OwnedStorageProbeControl.AllocationFailure = allocation;
            OwnedStorageProbeControl.FailAllocation = 3;
            OwnedStorageProbeControl.CleanupFailures.Add(1, cleanup);
            var failure = Unwrap(Assert.Catch(() => Activator.CreateInstance(type)));
            if (policy == RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures)
            {
                Assert.That(((AggregateException)failure).Flatten().InnerExceptions, Is.EquivalentTo(new[] { allocation, cleanup }));
                Assert.That(OwnedStorageProbeControl.LiveCount, Is.Zero);
                Assert.That(OwnedStorageProbeControl.Events, Is.EqualTo(new[]
                {
                    "allocate:1", "allocate:2", "allocate:3", "dispose:1", "dispose:2",
                }));
            }
            else
            {
                Assert.That(failure, Is.SameAs(cleanup), "Projectile's existing FailFast cleanup replaces the initialization error.");
                Assert.That(OwnedStorageProbeControl.LiveCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void EmittedNativeRowsLifecycleRetainsCapacityAndReleasesBothRealAllocations()
        {
            var type = CompileOwner(typeof(OwnedStorageNativeOwner), RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures);
            var owner = Activator.CreateInstance(type);
            var rows = (RuntimeReplayNativeRows<int>)type.GetField("Rows").GetValue(owner);
            var other = (RuntimeReplayNativeRows<int>)type.GetField("Other").GetValue(owner);
            try
            {
                rows.EnsureCapacity(257);
                other.EnsureCapacity(33);
                rows.Records.Add(17);
                other.Records.Add(23);
                var capacity = rows.Records.Capacity;
                var otherCapacity = other.Records.Capacity;
                Invoke(type, owner, "Reset");
                Assert.That(rows.Records.Length, Is.Zero);
                Assert.That(other.Records.Length, Is.Zero);
                Assert.That(rows.Records.Capacity, Is.EqualTo(capacity));
                Assert.That(other.Records.Capacity, Is.EqualTo(otherCapacity));
                rows.Records.Add(29);
                other.Records.Add(31);
                Invoke(type, owner, "Dispose");
                Assert.That(rows.Records.IsCreated, Is.False);
                Assert.That(other.Records.IsCreated, Is.False);
                Assert.DoesNotThrow(() => Invoke(type, owner, "Dispose"));
            }
            finally
            {
                rows.Dispose();
                other.Dispose();
            }
        }

        [TestCase(typeof(OwnedStorageMutableOwner))]
        [TestCase(typeof(OwnedStorageStaticOwner))]
        [TestCase(typeof(OwnedStoragePrivateOwner))]
        [TestCase(typeof(OwnedStorageDuplicateOrderOwner))]
        [TestCase(typeof(OwnedStorageMissingMarkerOwner))]
        [TestCase(typeof(OwnedStorageInvalidRowOwner))]
        [TestCase(typeof(OwnedStorageGenericResetOwner))]
        [TestCase(typeof(OwnedStorageNegativeOrderOwner))]
        [TestCase(typeof(OwnedStorageInheritedMethodsOwner))]
        [TestCase(typeof(OwnedStorageNoRowsOwner))]
        [TestCase(typeof(OwnedStorageGenericOwner<>))]
        [TestCase(typeof(OwnedStorageNestedOwner.Inner))]
        public void InvalidOwnerInBatchDoesNotWriteAnyOutputOrCreateOutputDirectory(Type invalidOwner)
        {
            var retained = new byte[] { 0, 17, 255, 23 };
            File.WriteAllBytes(_output, retained);
            var timestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(_output, timestamp);
            var invalidPath = Path.Combine(_tempRoot, "MustNotExist", "Invalid.g.cs");
            var first = Profile(typeof(OwnedStorageProbeOwner), _output);
            var invalid = Profile(invalidOwner, invalidPath);

            var failure = Assert.Catch(() => RuntimeReplayOwnedStorageLifecycleGenerator.GenerateAndWrite(first, invalid));
            Assert.That(failure.Message, Does.Contain(invalidOwner.Name));
            Assert.That(File.ReadAllBytes(_output), Is.EqualTo(retained));
            Assert.That(File.GetLastWriteTimeUtc(_output), Is.EqualTo(timestamp));
            Assert.That(Directory.Exists(Path.GetDirectoryName(invalidPath)), Is.False);
            Assert.Catch(() => RuntimeReplayOwnedStorageLifecycleGenerator.AreOutputsCurrent(first, invalid));
            Assert.That(File.ReadAllBytes(_output), Is.EqualTo(retained));
            Assert.That(File.GetLastWriteTimeUtc(_output), Is.EqualTo(timestamp));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DuplicateBatchOwnerOrOutputPathIsRejectedBeforeWriting(bool duplicateOwner)
        {
            File.WriteAllText(_output, "retained");
            var first = Profile(typeof(OwnedStorageProbeOwner), _output);
            var second = Profile(duplicateOwner ? typeof(OwnedStorageProbeOwner) : typeof(OwnedStorageNativeOwner),
                duplicateOwner ? Path.Combine(_tempRoot, "Other.g.cs") : _output);
            Assert.Catch(() => RuntimeReplayOwnedStorageLifecycleGenerator.GenerateAndWrite(first, second));
            Assert.That(File.ReadAllText(_output), Is.EqualTo("retained"));
            Assert.That(File.Exists(Path.Combine(_tempRoot, "Other.g.cs")), Is.False);
        }

        [Test]
        public void CustomOutputRepeatedGenerationDoesNotWriteAndReadOnlyCheckDoesNotRepairStaleBytes()
        {
            var profile = Profile(typeof(OwnedStorageProbeOwner), _output);
            Assert.That(RuntimeReplayOwnedStorageLifecycleGenerator.AreOutputsCurrent(profile), Is.False);
            Assert.That(File.Exists(_output), Is.False);
            Assert.That(RuntimeReplayOwnedStorageLifecycleGenerator.GenerateAndWrite(profile), Is.True);
            var bytes = File.ReadAllBytes(_output);
            var timestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(_output, timestamp);
            Assert.That(RuntimeReplayOwnedStorageLifecycleGenerator.AreOutputsCurrent(profile), Is.True);
            Assert.That(RuntimeReplayOwnedStorageLifecycleGenerator.GenerateAndWrite(profile), Is.False);
            Assert.That(File.ReadAllBytes(_output), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(_output), Is.EqualTo(timestamp));
            File.WriteAllText(_output, "stale");
            File.SetLastWriteTimeUtc(_output, timestamp);
            Assert.That(RuntimeReplayOwnedStorageLifecycleGenerator.AreOutputsCurrent(profile), Is.False);
            Assert.That(File.ReadAllText(_output), Is.EqualTo("stale"));
            Assert.That(File.GetLastWriteTimeUtc(_output), Is.EqualTo(timestamp));
        }

        private static RuntimeReplayOwnedStorageGenerationProfile Profile(Type owner, string output) =>
            new(owner, output, RuntimeReplayOwnedStorageCleanupPolicy.AggregateFailures);

        private Type CompileOwner(Type owner, RuntimeReplayOwnedStorageCleanupPolicy policy)
        {
            var key = owner.FullName + ":" + policy;
            if (_compiledOwners.TryGetValue(key, out var retained))
            {
                return retained;
            }
            var profile = new RuntimeReplayOwnedStorageGenerationProfile(owner, _output, policy);
            var emitted = RuntimeReplayOwnedStorageLifecycleGenerator.Generate(profile);
            var fields = owner == typeof(OwnedStorageNativeOwner)
                ? "public readonly global::DingoGameObjectsCMS.RuntimeObjects.Replay.RuntimeReplayNativeRows<int> Rows;\n"
                    + "public readonly global::DingoGameObjectsCMS.RuntimeObjects.Replay.RuntimeReplayNativeRows<int> Other;\n"
                : "public readonly global::DingoGameObjectsCMS.Tests.Editor.OwnedStorageProbeLast Last;\n"
                    + "public readonly global::DingoGameObjectsCMS.Tests.Editor.OwnedStorageProbeFirst First;\n"
                    + "public readonly global::DingoGameObjectsCMS.Tests.Editor.OwnedStorageProbeMiddle Middle;\n";
            var declarations = "namespace " + owner.Namespace + " { public partial class " + owner.Name
                + " : global::System.IDisposable { " + fields + " } }";
            using var compiler = new CSharpCodeProvider();
            var parameters = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                TempFiles = new TempFileCollection(_tempRoot, false),
            };
            var responsePath = Path.Combine(_tempRoot, "References-" + Guid.NewGuid().ToString("N") + ".rsp");
            CompilerResults result;
            try
            {
                using (var writer = new StreamWriter(responsePath, false, Encoding.UTF8))
                {
                    foreach (var reference in CodeDomReferences())
                    {
                        writer.WriteLine("/r:\"" + reference + "\"");
                    }
                }
                parameters.CompilerOptions = "/langversion:6 @\"" + responsePath + "\"";
                result = compiler.CompileAssemblyFromSource(parameters, declarations, emitted);
            }
            finally
            {
                if (File.Exists(responsePath))
                {
                    File.Delete(responsePath);
                }
            }
            Assert.That(result.Errors.HasErrors, Is.False,
                string.Join(Environment.NewLine, result.Errors.Cast<CompilerError>().Select(error => error.ToString())));
            var type = result.CompiledAssembly.GetType(owner.FullName, throwOnError: true);
            _compiledOwners.Add(key, type);
            return type;
        }

        private static IEnumerable<string> CodeDomReferences()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }
                try
                {
                    var location = assembly.Location;
                    if (!string.IsNullOrEmpty(location) && File.Exists(location))
                    {
                        paths.Add(location);
                    }
                }
                catch (NotSupportedException)
                {
                    // An in-memory test assembly may not expose a physical reference.
                }
            }
            var hasNetstandard = paths.Any(path => string.Equals(Path.GetFileNameWithoutExtension(path), "netstandard", StringComparison.OrdinalIgnoreCase));
            var duplicates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mscorlib", "System.Runtime", "System.Private.CoreLib", "System.Collections",
            };
            return hasNetstandard ? paths.Where(path => !duplicates.Contains(Path.GetFileNameWithoutExtension(path))) : paths;
        }

        private static OwnedStorageProbe[] ProbeRows(Type type, object owner) => new[]
        {
            (OwnedStorageProbe)type.GetField("First").GetValue(owner),
            (OwnedStorageProbe)type.GetField("Middle").GetValue(owner),
            (OwnedStorageProbe)type.GetField("Last").GetValue(owner),
        };

        private static void Invoke(Type type, object owner, string method)
        {
            try
            {
                type.GetMethod(method).Invoke(owner, null);
            }
            catch (TargetInvocationException failure)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw();
                throw;
            }
        }

        private static Exception Unwrap(Exception failure) => failure is TargetInvocationException wrapper
            ? Unwrap(wrapper.InnerException) : failure;
    }

    public static class OwnedStorageProbeControl
    {
        public static readonly List<string> Events = new();
        public static readonly List<OwnedStorageProbe> Instances = new();
        public static readonly Dictionary<int, Exception> CleanupFailures = new();
        public static int FailAllocation;
        public static Exception AllocationFailure;
        public static int LiveCount => Instances.Count(row => !row.Disposed);

        public static void Reset()
        {
            ReleaseRemaining();
            Events.Clear();
            Instances.Clear();
            FailAllocation = 0;
            AllocationFailure = null;
        }

        public static void ReleaseRemaining()
        {
            CleanupFailures.Clear();
            foreach (var row in Instances.Where(row => !row.Disposed))
            {
                row.Dispose();
            }
        }
    }

    public abstract class OwnedStorageProbe : IDisposable
    {
        private readonly int _ordinal;
        public bool Disposed;
        public int Value;

        protected OwnedStorageProbe(int ordinal)
        {
            _ordinal = ordinal;
            OwnedStorageProbeControl.Events.Add("allocate:" + ordinal);
            if (ordinal == OwnedStorageProbeControl.FailAllocation)
            {
                throw OwnedStorageProbeControl.AllocationFailure;
            }
            OwnedStorageProbeControl.Instances.Add(this);
        }

        public void Reset() => Value = 0;

        public void Dispose()
        {
            if (Disposed)
            {
                return;
            }
            Disposed = true;
            OwnedStorageProbeControl.Events.Add("dispose:" + _ordinal);
            if (OwnedStorageProbeControl.CleanupFailures.TryGetValue(_ordinal, out var failure))
            {
                throw failure;
            }
        }
    }

    public class OwnedStorageProbeFirst : OwnedStorageProbe { public OwnedStorageProbeFirst() : base(1) { } }
    public class OwnedStorageProbeMiddle : OwnedStorageProbe { public OwnedStorageProbeMiddle() : base(2) { } }
    public class OwnedStorageProbeLast : OwnedStorageProbe { public OwnedStorageProbeLast() : base(3) { } }

    public class OwnedStorageProbeOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(30)] public readonly OwnedStorageProbeLast Last;
        [RuntimeReplayOwnedStorage(10)] public readonly OwnedStorageProbeFirst First;
        [RuntimeReplayOwnedStorage(20)] public readonly OwnedStorageProbeMiddle Middle;
        public void Reset() { }
        public void Dispose() { }
    }

    public class OwnedStorageNativeOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(10)] public readonly RuntimeReplayNativeRows<int> Rows;
        [RuntimeReplayOwnedStorage(20)] public readonly RuntimeReplayNativeRows<int> Other;
        public void Reset() { }
        public void Dispose() { }
    }

    public class OwnedStorageMutableOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(0)] public OwnedStorageProbeFirst First;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageStaticOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(0)] public static readonly OwnedStorageProbeFirst First;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStoragePrivateOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(0)] private readonly OwnedStorageProbeFirst First;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageDuplicateOrderOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(0)] public readonly OwnedStorageProbeFirst First;
        [RuntimeReplayOwnedStorage(0)] public readonly OwnedStorageProbeMiddle Middle;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageMissingMarkerOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(0)] public readonly OwnedStorageProbeFirst First;
        public readonly RuntimeReplayNativeRows<int> Other;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageInvalidRowOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(0)] public readonly string First;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageGenericResetRow : IDisposable
    {
        public void Reset<T>() { }
        public void Dispose() { }
    }
    public class OwnedStorageGenericResetOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(0)] public readonly OwnedStorageGenericResetRow First;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageNegativeOrderOwner : IDisposable
    {
        [RuntimeReplayOwnedStorage(-1)] public readonly OwnedStorageProbeFirst First;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageNoRowsOwner : IDisposable
    {
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageInheritedMethodsBase : IDisposable
    {
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageInheritedMethodsOwner : OwnedStorageInheritedMethodsBase
    {
        [RuntimeReplayOwnedStorage(0)] public readonly RuntimeReplayNativeRows<int> Rows;
    }
    public class OwnedStorageGenericOwner<T> : IDisposable
    {
        [RuntimeReplayOwnedStorage(0)] public readonly OwnedStorageProbeFirst First;
        public void Reset() { }
        public void Dispose() { }
    }
    public class OwnedStorageNestedOwner
    {
        public class Inner : IDisposable
        {
            [RuntimeReplayOwnedStorage(0)] public readonly OwnedStorageProbeFirst First;
            public void Reset() { }
            public void Dispose() { }
        }
    }
}
