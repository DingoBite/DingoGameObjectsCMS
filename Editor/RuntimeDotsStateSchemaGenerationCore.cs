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
        public readonly Action<RuntimeDotsStateSchemaManifest> ValidateManifest;

        public RuntimeDotsStateSchemaGenerationProfile(
            int codecVersion,
            string generatedCodePath,
            string generatedNamespace,
            string generatedClassName,
            Func<RuntimeDotsStateSchemaManifest> previousManifestFactory,
            Func<Type, bool> requiresClassification,
            Action<RuntimeDotsStateSchemaManifest> validateManifest = null)
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
            ValidateManifest = validateManifest;
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
            ValidateCompiledManifest(manifest);
            return manifest;
        }

        private static void ValidateCompiledManifest(RuntimeDotsStateSchemaManifest manifest)
        {
            if (manifest.Components == null || manifest.ReservedComponentTypeIds == null)
            {
                throw new InvalidOperationException("Compiled DOTS state manifest requires component and reserved-id collections.");
            }
            if (manifest.FormatVersion != RuntimeDotsStateSchemaReconciler.FORMAT_VERSION || manifest.CodecVersion <= 0)
            {
                throw new InvalidOperationException($"Compiled DOTS state manifest has unsupported format={manifest.FormatVersion} or codec={manifest.CodecVersion}.");
            }

            var reserved = new HashSet<int>();
            foreach (var id in manifest.ReservedComponentTypeIds)
            {
                if (id < 0 || !reserved.Add(id))
                {
                    throw new InvalidOperationException($"Compiled DOTS state manifest has invalid or duplicate reserved id {id}.");
                }
            }
            var activeIds = new HashSet<int>();
            var activeTypes = new HashSet<Type>();
            for (var i = 0; i < manifest.Components.Count; i++)
            {
                var component = manifest.Components[i];
                if (component == null || component.RuntimeType == null)
                {
                    throw new InvalidOperationException($"Compiled DOTS state manifest active entry at index {i} requires RuntimeType = typeof(T).");
                }
                if (component.ComponentTypeId < 0 || !activeIds.Add(component.ComponentTypeId) || reserved.Contains(component.ComponentTypeId))
                {
                    throw new InvalidOperationException($"Compiled DOTS state manifest active id {component.ComponentTypeId} is invalid, duplicate, or reserved.");
                }
                if (!activeTypes.Add(component.RuntimeType))
                {
                    throw new InvalidOperationException($"Compiled DOTS state manifest repeats active type '{component.RuntimeType.FullName}'.");
                }
            }

            var calculatedHash = RuntimeDotsStateSchemaReconciler.CalculateSchemaHash(manifest);
            if (!string.Equals(manifest.SchemaHash, calculatedHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Compiled DOTS state manifest hash mismatch. Compiled={manifest.SchemaHash}, calculated={calculatedHash}.");
            }
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

            var existing = LoadExistingCompiledManifest(profile);
            var discovery = RuntimeDotsStateSchemaDiscovery.Discover(
                CollectPlayerRuntimeTypes(),
                profile.RequiresClassification);
            var discoveredSchemas = discovery.Components
                .Select(component => component.Schema)
                .ToArray();
            var manifest = RuntimeDotsStateSchemaReconciler.Reconcile(
                existing,
                discoveredSchemas,
                profile.CodecVersion);
            profile.ValidateManifest?.Invoke(manifest);
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
                .SelectMany(RuntimeGenerationTypeDiscovery.GetTypes);
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

    }
}
