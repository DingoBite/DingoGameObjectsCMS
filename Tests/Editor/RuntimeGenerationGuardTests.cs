using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DingoGameObjectsCMS.Editor;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using NUnit.Framework;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeGenerationIncompleteAssembly : Assembly
    {
        public override string FullName => "Incomplete.RuntimeGenerationFixture";

        public override Type[] GetTypes()
        {
            throw new ReflectionTypeLoadException(
                new Type[] { typeof(NetworkOwner_GRC), null },
                new Exception[] { new TypeLoadException("missing generation dependency") });
        }
    }

    public class RuntimeGenerationGuardTests
    {
        private string _tempRoot;
        private string _output;

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")),
                "Library", "RuntimeGenerationGuardTests", Guid.NewGuid().ToString("N"));
            _output = Path.Combine(_tempRoot, "Custom", "Schema.Generated.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(_output));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }

        [Test]
        public void Discovery_RejectsIncompleteAssemblyWithLoaderContext()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RuntimeGenerationTypeDiscovery.GetTypes(new RuntimeGenerationIncompleteAssembly()));
            Assert.That(exception.Message, Does.Contain("Incomplete.RuntimeGenerationFixture"));
            Assert.That(exception.Message, Does.Contain("missing generation dependency"));
            Assert.That(exception.InnerException, Is.TypeOf<ReflectionTypeLoadException>());
        }

        [TestCase("grc")]
        [TestCase("patch")]
        [TestCase("dots")]
        public void Generation_CustomOutputIsRepeatableAndReadOnlyCheckDoesNotRepairIt(string generator)
        {
            var operations = CreateGenerationOperations(generator);
            Assert.That(operations.Generate(), Is.True);
            var bytes = File.ReadAllBytes(_output);
            var retainedTimestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(_output, retainedTimestamp);

            Assert.That(operations.Current(), Is.True);
            Assert.That(operations.Generate(), Is.False);
            Assert.That(File.ReadAllBytes(_output), Is.EqualTo(bytes));
            Assert.That(File.GetLastWriteTimeUtc(_output), Is.EqualTo(retainedTimestamp));

            File.WriteAllText(_output, "stale custom output");
            File.SetLastWriteTimeUtc(_output, retainedTimestamp);
            Assert.That(operations.Current(), Is.False);
            Assert.That(File.ReadAllText(_output), Is.EqualTo("stale custom output"));
            Assert.That(File.GetLastWriteTimeUtc(_output), Is.EqualTo(retainedTimestamp));
        }

        [Test]
        public void ComponentGeneration_PreservesCompiledIdAndNumericReservationAtCustomPath()
        {
            var result = RuntimeComponentTypeManifestGenerator.GenerateAndWrite(CreateComponentProfile());
            Assert.That(result.Manifest.Types.Count, Is.EqualTo(1));
            Assert.That(result.Manifest.Types[0].RuntimeType, Is.EqualTo(typeof(NetworkOwner_GRC)));
            Assert.That(result.Manifest.Types[0].Id, Is.EqualTo(7));
            Assert.That(result.Manifest.ReservedIds, Is.EqualTo(new[] { 3 }));
            Assert.That(result.Manifest.RegistryHash, Is.EqualTo(CreateRuntimeManifest().RegistryHash));
        }

        [TestCase("grc", "Invalid..Namespace", "GeneratedRegistry")]
        [TestCase("grc", "Custom.Generated", "Invalid-Class")]
        [TestCase("grc", "Custom.namespace", "GeneratedRegistry")]
        [TestCase("grc", "Custom.Generated", "class")]
        [TestCase("dots", "Invalid..Namespace", "GeneratedRegistry")]
        [TestCase("dots", "Custom.Generated", "Invalid-Class")]
        [TestCase("dots", "Custom.namespace", "GeneratedRegistry")]
        [TestCase("dots", "Custom.Generated", "class")]
        public void GenerationProfile_RejectsInvalidSymbolsBeforeOutputMutation(string generator, string generatedNamespace, string className)
        {
            File.WriteAllText(_output, "retained output");
            Assert.Throws<ArgumentException>(() =>
            {
                if (generator == "grc")
                {
                    _ = new RuntimeComponentTypeGenerationProfile(_output, generatedNamespace, className,
                        CreateRuntimeManifest, type => type == typeof(NetworkOwner_GRC));
                }
                else
                {
                    _ = new RuntimeDotsStateSchemaGenerationProfile(1, _output, generatedNamespace, className,
                        CreateEmptyDotsManifest, type => false);
                }
            });
            Assert.That(File.ReadAllText(_output), Is.EqualTo("retained output"));
        }

        [TestCase("null-manifest")]
        [TestCase("null-components")]
        [TestCase("null-reservations")]
        [TestCase("missing-hash")]
        [TestCase("mismatched-hash")]
        [TestCase("negative-reservation")]
        [TestCase("duplicate-reservation")]
        [TestCase("active-reserved")]
        [TestCase("null-component")]
        [TestCase("missing-type")]
        [TestCase("duplicate-active-id")]
        [TestCase("duplicate-active-type")]
        public void DotsGeneration_RejectsIncompleteCompiledLedgerBeforeOutputMutation(string defect)
        {
            File.WriteAllText(_output, "retained output");
            var manifest = CreateDefectiveDotsManifest(defect);
            var profile = new RuntimeDotsStateSchemaGenerationProfile(1, _output, "Custom.Generated", "GeneratedRegistry",
                () => manifest, type => false);
            Assert.Throws<InvalidOperationException>(() => RuntimeDotsStateSchemaGenerationCore.GenerateAndWrite(profile));
            Assert.That(File.ReadAllText(_output), Is.EqualTo("retained output"));
            Assert.Throws<InvalidOperationException>(() => RuntimeDotsStateSchemaGenerationCore.AreOutputsCurrent(profile));
            Assert.That(File.ReadAllText(_output), Is.EqualTo("retained output"));
        }

        [Test]
        public void DotsGeneration_CoverageValidatorRunsBeforeWriteAndDuringReadOnlyCheck()
        {
            File.WriteAllText(_output, "retained output");
            var calls = 0;
            var profile = new RuntimeDotsStateSchemaGenerationProfile(1, _output, "Custom.Generated", "GeneratedRegistry",
                CreateEmptyDotsManifest, type => false, manifest =>
                {
                    calls++;
                    Assert.That(manifest.SchemaHash, Is.EqualTo(RuntimeDotsStateSchemaReconciler.CalculateSchemaHash(manifest)));
                    throw new InvalidOperationException("required custom stage missing");
                });

            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                RuntimeDotsStateSchemaGenerationCore.GenerateAndWrite(profile)).Message, Does.Contain("required custom stage"));
            Assert.That(Assert.Throws<InvalidOperationException>(() =>
                RuntimeDotsStateSchemaGenerationCore.AreOutputsCurrent(profile)).Message, Does.Contain("required custom stage"));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(File.ReadAllText(_output), Is.EqualTo("retained output"));
        }

        [Test]
        public void PatchGeneration_RuntimeValidatorRunsBeforeWriteAndDuringReadOnlyCheck()
        {
            File.WriteAllText(_output, "retained output");
            var calls = 0;
            var profile = new RuntimePatchSchemaGenerationProfile(1, CreateRuntimeManifest, _output,
                new RuntimePatchCodeEmissionProfile("Custom.Generated", "GeneratedRegistry"), manifest =>
                {
                    calls++;
                    throw new InvalidOperationException("declared policy missing");
                });
            Assert.Throws<InvalidOperationException>(() => RuntimePatchSchemaGenerationCore.GenerateAndWrite(profile));
            Assert.Throws<InvalidOperationException>(() => RuntimePatchSchemaGenerationCore.AreOutputsCurrent(profile));
            Assert.That(calls, Is.EqualTo(2));
            Assert.That(File.ReadAllText(_output), Is.EqualTo("retained output"));
        }

        [Test]
        public void Fingerprint_RejectsMissingExplicitInputWithoutRewritingOutput()
        {
            var missing = Path.Combine(_tempRoot, "explicit-missing-input.json");
            File.WriteAllText(_output, "retained output");
            var exception = Assert.Throws<FileNotFoundException>(() =>
                RuntimeBuildFingerprintGenerationCore.GenerateAndWrite(CreateFingerprintProfile(new[] { missing })));
            Assert.That(exception.FileName, Is.EqualTo(missing));
            Assert.That(exception.Message, Does.Contain(missing));
            Assert.That(File.ReadAllText(_output), Is.EqualTo("retained output"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   \t\r\n")]
        public void FingerprintProfile_RejectsBlankExplicitInputWithoutRewritingOutput(string input)
        {
            File.WriteAllText(_output, "retained output");
            var exception = Assert.Throws<ArgumentException>(() => CreateFingerprintProfile(new[] { input }));
            Assert.That(exception.ParamName, Is.EqualTo("additionalInputPaths"));
            Assert.That(File.ReadAllText(_output), Is.EqualTo("retained output"));
        }

        [Test]
        public void Fingerprint_CustomInputsAreOrderIndependentAndOutputDoesNotHashItself()
        {
            var first = Path.Combine(_tempRoot, "first.json");
            var second = Path.Combine(_tempRoot, "second.json");
            File.WriteAllText(first, "first input");
            File.WriteAllText(second, "second input");
            var profile = CreateFingerprintProfile(new[] { first, second });
            var initial = RuntimeBuildFingerprintGenerationCore.Calculate(profile);
            var reordered = RuntimeBuildFingerprintGenerationCore.Calculate(CreateFingerprintProfile(new[] { second, first, second }));
            Assert.That(reordered, Is.EqualTo(initial));

            Assert.That(RuntimeBuildFingerprintGenerationCore.GenerateAndWrite(profile), Is.True);
            var retainedTimestamp = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(_output, retainedTimestamp);
            Assert.That(RuntimeBuildFingerprintGenerationCore.GenerateAndWrite(profile), Is.False);
            Assert.That(File.GetLastWriteTimeUtc(_output), Is.EqualTo(retainedTimestamp));
            Assert.That(RuntimeBuildFingerprintGenerationCore.Calculate(profile), Is.EqualTo(initial));
            File.WriteAllText(first, "changed input");
            Assert.That(RuntimeBuildFingerprintGenerationCore.Calculate(profile), Is.Not.EqualTo(initial));
        }

        private (Func<bool> Generate, Func<bool> Current) CreateGenerationOperations(string generator)
        {
            switch (generator)
            {
                case "grc":
                    var componentProfile = CreateComponentProfile();
                    return (() => RuntimeComponentTypeManifestGenerator.GenerateAndWrite(componentProfile).GeneratedCodeChanged,
                        () => RuntimeComponentTypeManifestGenerator.IsOutputCurrent(componentProfile));
                case "patch":
                    var patchProfile = new RuntimePatchSchemaGenerationProfile(1, CreateRuntimeManifest, _output,
                        new RuntimePatchCodeEmissionProfile("Custom.Generated", "GeneratedRegistry"));
                    return (() => RuntimePatchSchemaGenerationCore.GenerateAndWrite(patchProfile).GeneratedCodeChanged,
                        () => RuntimePatchSchemaGenerationCore.AreOutputsCurrent(patchProfile));
                case "dots":
                    var dotsProfile = new RuntimeDotsStateSchemaGenerationProfile(1, _output, "Custom.Generated", "GeneratedRegistry",
                        CreateEmptyDotsManifest, type => false);
                    return (() => RuntimeDotsStateSchemaGenerationCore.GenerateAndWrite(dotsProfile).GeneratedCodeChanged,
                        () => RuntimeDotsStateSchemaGenerationCore.AreOutputsCurrent(dotsProfile));
                default:
                    throw new ArgumentOutOfRangeException(nameof(generator));
            }
        }

        private RuntimeComponentTypeGenerationProfile CreateComponentProfile()
        {
            return new RuntimeComponentTypeGenerationProfile(_output, "Custom.Generated", "GeneratedRegistry",
                CreateRuntimeManifest, type => type == typeof(NetworkOwner_GRC));
        }

        private RuntimeBuildFingerprintGenerationProfile CreateFingerprintProfile(IReadOnlyList<string> inputs)
        {
            return new RuntimeBuildFingerprintGenerationProfile(_tempRoot, "Custom/Schema.Generated.cs", "Custom.Generated",
                "GeneratedFingerprint", "Value", "generation.guard.tests", "1.0.0", inputs);
        }

        private static Manifest CreateRuntimeManifest()
        {
            var manifest = new Manifest
            {
                Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION,
                Types = new List<Entry> { RuntimeComponentTypeRegistry.CreateEntry(7, typeof(NetworkOwner_GRC)) },
                ReservedIds = new List<int> { 3 },
            };
            manifest.RegistryHash = RuntimeComponentTypeRegistry.CalculateRegistryHash(manifest.Types, manifest.ReservedIds);
            return manifest;
        }

        private static RuntimeDotsStateSchemaManifest CreateEmptyDotsManifest()
        {
            var manifest = new RuntimeDotsStateSchemaManifest
            {
                FormatVersion = RuntimeDotsStateSchemaReconciler.FORMAT_VERSION,
                CodecVersion = 1,
            };
            manifest.SchemaHash = RuntimeDotsStateSchemaReconciler.CalculateSchemaHash(manifest);
            return manifest;
        }

        private static RuntimeDotsStateSchemaManifest CreateDefectiveDotsManifest(string defect)
        {
            var manifest = CreateEmptyDotsManifest();
            var component = RuntimeDotsStateSchemaDiscovery.DescribeComponent(typeof(RuntimeDotsPersistedTestComponent)).Schema;
            component.ComponentTypeId = 7;
            component.LayoutHash = RuntimeDotsStateSchemaReconciler.CalculateLayoutHash(component.RuntimeType);
            switch (defect)
            {
                case "null-manifest": return null;
                case "null-components": manifest.Components = null; break;
                case "null-reservations": manifest.ReservedComponentTypeIds = null; break;
                case "missing-hash": manifest.SchemaHash = null; break;
                case "mismatched-hash": manifest.SchemaHash = new string('0', 64); break;
                case "negative-reservation": manifest.ReservedComponentTypeIds.Add(-1); break;
                case "duplicate-reservation": manifest.ReservedComponentTypeIds.AddRange(new[] { 3, 3 }); break;
                case "active-reserved": manifest.Components.Add(component); manifest.ReservedComponentTypeIds.Add(7); break;
                case "null-component": manifest.Components.Add(null); break;
                case "missing-type": component.RuntimeType = null; manifest.Components.Add(component); break;
                case "duplicate-active-id": manifest.Components.AddRange(new[] { component, component }); break;
                case "duplicate-active-type":
                    manifest.Components.Add(component);
                    manifest.Components.Add(new RuntimeDotsStateComponentSchema { RuntimeType = component.RuntimeType, ComponentTypeId = 8 });
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(defect));
            }
            return manifest;
        }
    }
}
