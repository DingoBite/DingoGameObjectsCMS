using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor
{
    public sealed class RuntimePatchSchemaGenerationProfile
    {
        public int CodecVersion { get; }
        public string RuntimeComponentManifestPath { get; }
        public string PatchSchemaPath { get; }
        public string GeneratedCodePath { get; }
        public RuntimePatchCodeEmissionProfile CodeEmission { get; }
        public Action<Manifest> RuntimeManifestValidator { get; }

        public RuntimePatchSchemaGenerationProfile(
            int codecVersion,
            string runtimeComponentManifestPath,
            string patchSchemaPath,
            string generatedCodePath,
            RuntimePatchCodeEmissionProfile codeEmission,
            Action<Manifest> runtimeManifestValidator = null)
        {
            if (codecVersion <= 0)
                throw new ArgumentOutOfRangeException(nameof(codecVersion));
            CodecVersion = codecVersion;
            RuntimeComponentManifestPath = RequirePath(runtimeComponentManifestPath, nameof(runtimeComponentManifestPath));
            PatchSchemaPath = RequirePath(patchSchemaPath, nameof(patchSchemaPath));
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
        public bool ManifestChanged;
        public bool GeneratedCodeChanged;

        public bool AnyOutputChanged => ManifestChanged || GeneratedCodeChanged;
    }

    /// <summary>
    /// Generic deterministic runtime patch generator. Its complete component
    /// universe comes from the checked-in RuntimeComponentTypeRegistry manifest;
    /// project bindings provide only paths and generated C# identity.
    /// </summary>
    public static class RuntimePatchSchemaGenerationCore
    {
        public static RuntimePatchSchemaGenerationResult GenerateAndWrite(
            RuntimePatchSchemaGenerationProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var runtimeManifest = LoadRuntimeComponentManifest(profile.RuntimeComponentManifestPath);
            profile.RuntimeManifestValidator?.Invoke(runtimeManifest);
            var discovery = RuntimePatchSchemaDiscovery.Discover(runtimeManifest);
            var existing = LoadExistingPatchManifest(profile.PatchSchemaPath);
            ValidateExistingManifestHash(existing);

            var discoveredSchemas = new List<RuntimePatchComponentSchema>(discovery.Components.Count);
            for (var i = 0; i < discovery.Components.Count; i++)
                discoveredSchemas.Add(discovery.Components[i].Schema);

            var manifest = RuntimePatchSchemaReconciler.Reconcile(
                existing,
                discoveredSchemas,
                discovery.ComponentRegistryHash,
                profile.CodecVersion);
            BindReconciledSchema(discovery.Components, manifest);
            var generatedCode = RuntimePatchCodeEmitter.Generate(
                manifest,
                discovery.Components,
                profile.CodeEmission);
            var manifestJson = JsonUtility.ToJson(manifest, true) + Environment.NewLine;

            return new RuntimePatchSchemaGenerationResult
            {
                Manifest = manifest,
                ManifestChanged = WriteIfChanged(profile.PatchSchemaPath, manifestJson),
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

            var componentByKey = manifest.Components
                .Where(component => component != null && !component.Tombstone)
                .ToDictionary(component => component.ComponentTypeKey, component => component, StringComparer.Ordinal);
            for (var i = 0; i < descriptors.Count; i++)
            {
                var descriptor = descriptors[i];
                if (!componentByKey.TryGetValue(descriptor.Schema.ComponentTypeKey, out var componentSchema))
                {
                    throw new InvalidOperationException(
                        $"Reconciled runtime patch schema is missing active component '{descriptor.Schema.ComponentTypeKey}'.");
                }

                var fieldByKey = componentSchema.Fields
                    .Where(field => field != null && !field.Tombstone)
                    .ToDictionary(field => field.FieldKey, field => field, StringComparer.Ordinal);
                for (var fieldIndex = 0; fieldIndex < descriptor.Fields.Count; fieldIndex++)
                {
                    var fieldDescriptor = descriptor.Fields[fieldIndex];
                    if (!fieldByKey.TryGetValue(fieldDescriptor.Schema.FieldKey, out var fieldSchema))
                    {
                        throw new InvalidOperationException(
                            $"Reconciled runtime patch schema is missing active field '{fieldDescriptor.Schema.FieldKey}'.");
                    }
                    fieldDescriptor.Schema = fieldSchema;
                }

                descriptor.Schema = componentSchema;
                descriptor.Fields.Sort((first, second) => first.Schema.FieldId.CompareTo(second.Schema.FieldId));
            }
        }

        public static Manifest LoadRuntimeComponentManifest(string path)
        {
            var fullPath = RequireExistingFile(path, "Runtime component manifest");
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(fullPath));
            if (manifest?.Types == null)
                throw new InvalidOperationException($"Runtime component manifest '{fullPath}' is invalid.");
            return manifest;
        }

        public static RuntimePatchSchemaManifest LoadExistingPatchManifest(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Runtime patch schema path is required.", nameof(path));
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                return null;
            var manifest = JsonUtility.FromJson<RuntimePatchSchemaManifest>(File.ReadAllText(fullPath));
            if (manifest == null)
                throw new InvalidOperationException($"Runtime patch schema manifest '{fullPath}' is invalid.");
            manifest.Components ??= new List<RuntimePatchComponentSchema>();
            return manifest;
        }

        public static void ValidateExistingManifestHash(RuntimePatchSchemaManifest existing)
        {
            if (existing == null || string.IsNullOrWhiteSpace(existing.SchemaHash))
                return;
            var calculated = RuntimePatchSchemaReconciler.CalculateSchemaHash(existing);
            if (!string.Equals(existing.SchemaHash, calculated, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Runtime patch schema hash mismatch. Manifest={existing.SchemaHash}, calculated={calculated}.");
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

        private static string RequireExistingFile(string path, string description)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"{description} path is required.", nameof(path));
            var fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"{description} is missing.", fullPath);
            return fullPath;
        }
    }
}
