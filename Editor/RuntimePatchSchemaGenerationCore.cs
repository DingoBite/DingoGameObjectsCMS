using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Editor
{
    public sealed class RuntimePatchSchemaGenerationProfile
    {
        public int CodecVersion { get; }
        public Func<Manifest> RuntimeComponentManifestFactory { get; }
        public string GeneratedCodePath { get; }
        public RuntimePatchCodeEmissionProfile CodeEmission { get; }
        public Action<Manifest> RuntimeManifestValidator { get; }

        public RuntimePatchSchemaGenerationProfile(
            int codecVersion,
            Func<Manifest> runtimeComponentManifestFactory,
            string generatedCodePath,
            RuntimePatchCodeEmissionProfile codeEmission,
            Action<Manifest> runtimeManifestValidator = null)
        {
            if (codecVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(codecVersion));
            CodecVersion = codecVersion;
            RuntimeComponentManifestFactory =
                runtimeComponentManifestFactory
                ?? throw new ArgumentNullException(
                    nameof(runtimeComponentManifestFactory));
            GeneratedCodePath = RequirePath(generatedCodePath, nameof(generatedCodePath));
            CodeEmission = codeEmission ?? throw new ArgumentNullException(nameof(codeEmission));
            RuntimeManifestValidator = runtimeManifestValidator;
        }

        private static string RequirePath(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Runtime patch generation path is required.", parameterName);
            return Path.GetFullPath(value);
        }
    }

    public sealed class RuntimePatchSchemaGenerationResult
    {
        public RuntimePatchSchemaManifest Manifest;
        public bool GeneratedCodeChanged;

        public bool AnyOutputChanged => GeneratedCodeChanged;
    }

    /// <summary>
    /// Generic deterministic runtime patch generator. Its complete component
    /// universe comes from the generated compiled RuntimeComponentTypeRegistry
    /// manifest factory. Component ids come directly from that typed ledger;
    /// field ids are emitted by the current typed codec layout. No string type
    /// identity, tombstone ledger, or mutable JSON participates in generation.
    /// </summary>
    public static class RuntimePatchSchemaGenerationCore
    {
        public static RuntimePatchSchemaGenerationResult GenerateAndWrite(
            RuntimePatchSchemaGenerationProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var runtimeManifest = profile.RuntimeComponentManifestFactory();
            if (runtimeManifest?.Types == null)
                throw new InvalidOperationException(
                    "Compiled runtime component manifest factory returned an invalid ledger.");
            profile.RuntimeManifestValidator?.Invoke(runtimeManifest);
            var discovery = RuntimePatchSchemaDiscovery.Discover(runtimeManifest);
            var discoveredSchemas = new List<RuntimePatchComponentSchema>(discovery.Components.Count);
            for (var i = 0; i < discovery.Components.Count; i++)
                discoveredSchemas.Add(discovery.Components[i].Schema);

            var manifest = RuntimePatchSchemaReconciler.Reconcile(
                discoveredSchemas,
                discovery.ComponentRegistryHash,
                profile.CodecVersion);
            BindReconciledSchema(discovery.Components, manifest);
            var generatedCode = RuntimePatchCodeEmitter.Generate(
                manifest,
                discovery.Components,
                profile.CodeEmission);
            return new RuntimePatchSchemaGenerationResult
            {
                Manifest = manifest,
                GeneratedCodeChanged = WriteIfChanged(profile.GeneratedCodePath, generatedCode),
            };
        }

        public static void BindReconciledSchema(
            IReadOnlyList<RuntimePatchGeneratedComponentDescriptor> descriptors,
            RuntimePatchSchemaManifest manifest)
        {
            if (descriptors == null)
                throw new ArgumentNullException(nameof(descriptors));
            if (manifest?.Components == null)
                throw new ArgumentNullException(nameof(manifest));

            var componentByType = manifest.Components
                .Where(component => component != null)
                .ToDictionary(component => component.RuntimeType, component => component);
            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                if (!componentByType.TryGetValue(descriptor.RuntimeType, out var componentSchema))
                {
                    throw new InvalidOperationException(
                        $"Runtime patch schema is missing component type '{descriptor.RuntimeType.FullName}'.");
                }

                var fieldById = componentSchema.Fields
                    .Where(field => field != null)
                    .ToDictionary(field => field.FieldId, field => field);
                for (var fieldIndex = 0; fieldIndex < descriptor.Fields.Count; fieldIndex++)
                {
                    var fieldDescriptor = descriptor.Fields[fieldIndex];
                    if (!fieldById.TryGetValue(fieldDescriptor.Schema.FieldId, out var fieldSchema))
                    {
                        throw new InvalidOperationException(
                            $"Runtime patch schema is missing field id {fieldDescriptor.Schema.FieldId} on '{descriptor.RuntimeType.FullName}'.");
                    }
                    fieldDescriptor.Schema = fieldSchema;
                }

                descriptor.Schema = componentSchema;
                descriptor.Fields.Sort((first, second) => first.Schema.FieldId.CompareTo(second.Schema.FieldId));
            }
        }

        public static bool WriteIfChanged(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Generated output path is required.", nameof(path));
            if (content == null)
                throw new ArgumentNullException(nameof(content));
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath)
                && string.Equals(File.ReadAllText(fullPath), content, StringComparison.Ordinal))
            {
                return false;
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException($"Generated output path '{fullPath}' has no directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
            return true;
        }

    }
}
