using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;

namespace DingoGameObjectsCMS.Editor
{
    public class RuntimeDotsStateSchemaGenerationProfile
    {
        public readonly int CodecVersion;
        public readonly string GeneratedCodePath;
        public readonly RuntimeDotsStateCodeEmissionProfile CodeEmission;
        public readonly Func<RuntimeDotsStateSchemaManifest>
            PreviousManifestFactory;
        public readonly Func<Type, bool> RequiresClassification;

        public RuntimeDotsStateSchemaGenerationProfile(
            int codecVersion,
            string generatedCodePath,
            string generatedNamespace,
            string generatedClassName,
            Func<RuntimeDotsStateSchemaManifest> previousManifestFactory,
            Func<Type, bool> requiresClassification)
        {
            if (codecVersion <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(codecVersion));
            }

            CodecVersion = codecVersion;
            GeneratedCodePath = RequirePath(
                generatedCodePath,
                nameof(generatedCodePath));
            CodeEmission = new RuntimeDotsStateCodeEmissionProfile(
                generatedNamespace,
                generatedClassName);
            PreviousManifestFactory = previousManifestFactory
                                      ?? throw new ArgumentNullException(
                                          nameof(previousManifestFactory));
            RequiresClassification = requiresClassification
                                     ?? throw new ArgumentNullException(
                                         nameof(requiresClassification));
        }

        private static string RequirePath(
            string value,
            string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    "DOTS state generation path is required.",
                    parameterName);
            }

            return Path.GetFullPath(value);
        }
    }

    public class RuntimeDotsStateSchemaGenerationResult
    {
        public RuntimeDotsStateSchemaManifest Manifest;
        public bool GeneratedCodeChanged;
    }

    public static class RuntimeDotsStateSchemaGenerationCore
    {
        private sealed class GeneratedOutputs
        {
            public RuntimeDotsStateSchemaManifest Manifest;
            public string GeneratedCode;
        }

        public static RuntimeDotsStateSchemaGenerationResult GenerateAndWrite(
            RuntimeDotsStateSchemaGenerationProfile profile)
        {
            var outputs = GenerateOutputs(profile);

            return new RuntimeDotsStateSchemaGenerationResult
            {
                Manifest = outputs.Manifest,
                GeneratedCodeChanged = WriteIfChanged(
                    profile.GeneratedCodePath,
                    outputs.GeneratedCode),
            };
        }

        public static bool AreOutputsCurrent(
            RuntimeDotsStateSchemaGenerationProfile profile)
        {
            var outputs = GenerateOutputs(profile);
            return HasExactContent(
                profile.GeneratedCodePath,
                outputs.GeneratedCode);
        }

        public static void BindReconciledSchema(
            IReadOnlyList<RuntimeDotsStateGeneratedComponentDescriptor>
                descriptors,
            RuntimeDotsStateSchemaManifest manifest)
        {
            if (descriptors == null)
            {
                throw new ArgumentNullException(nameof(descriptors));
            }
            if (manifest?.Components == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }

            var activeByType = manifest.Components
                .Where(component => component?.RuntimeType != null)
                .ToDictionary(
                    component => component.RuntimeType,
                    component => component);
            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                if (!activeByType.TryGetValue(
                        descriptor.RuntimeType,
                        out var schema))
                {
                    throw new InvalidOperationException(
                        $"Reconciled DOTS state schema is missing active component type '{descriptor.RuntimeType.FullName}'.");
                }

                descriptor.Schema = schema;
            }
        }

        public static RuntimeDotsStateSchemaManifest
            LoadExistingCompiledManifest(
                RuntimeDotsStateSchemaGenerationProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var manifest = profile.PreviousManifestFactory();
            if (manifest == null)
                throw new InvalidOperationException(
                    "Compiled DOTS state manifest factory returned null.");
            manifest.Components ??=
                new List<RuntimeDotsStateComponentSchema>();
            manifest.ReservedComponentTypeIds ??= new List<int>();
            return manifest;
        }

        public static bool WriteIfChanged(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "Generated output path is required.",
                    nameof(path));
            }
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            var fullPath = Path.GetFullPath(path);
            if (HasExactContent(fullPath, content))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException(
                    $"Generated output path '{fullPath}' has no directory.");
            }
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                fullPath,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }

        private static GeneratedOutputs GenerateOutputs(
            RuntimeDotsStateSchemaGenerationProfile profile)
        {
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }

            var discovery = RuntimeDotsStateSchemaDiscovery.Discover(
                CollectPlayerRuntimeTypes(),
                profile.RequiresClassification);
            var existing = LoadExistingCompiledManifest(profile);
            var discoveredSchemas = discovery.Components
                .Select(component => component.Schema)
                .ToArray();
            var manifest = RuntimeDotsStateSchemaReconciler.Reconcile(
                existing,
                discoveredSchemas,
                profile.CodecVersion);
            BindReconciledSchema(discovery.Components, manifest);
            return new GeneratedOutputs
            {
                Manifest = manifest,
                GeneratedCode = RuntimeDotsStateCodeEmitter.Generate(
                    manifest,
                    discovery.Components,
                    profile.CodeEmission),
            };
        }

        private static bool HasExactContent(string path, string content)
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath)
                   && string.Equals(
                       File.ReadAllText(fullPath),
                       content,
                       StringComparison.Ordinal);
        }

        private static IEnumerable<Type> CollectPlayerRuntimeTypes()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(IsPlayerRuntimeAssembly)
                .OrderBy(
                    assembly => assembly.GetName().Name,
                    StringComparer.Ordinal)
                .SelectMany(TakeLoadableTypes);
        }

        private static bool IsPlayerRuntimeAssembly(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic)
            {
                return false;
            }

            var name = assembly.GetName().Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return !name.EndsWith(
                       "-Editor",
                       StringComparison.OrdinalIgnoreCase)
                   && !name.EndsWith(
                       ".Editor",
                       StringComparison.OrdinalIgnoreCase)
                   && name.IndexOf(
                       "Test",
                       StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static IEnumerable<Type> TakeLoadableTypes(
            Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
        }
    }
}
