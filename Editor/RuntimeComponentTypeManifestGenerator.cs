using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Editor
{
    public sealed class RuntimeComponentTypeGenerationProfile
    {
        public readonly string GeneratedCodePath;
        public readonly string GeneratedNamespace;
        public readonly string GeneratedClassName;
        public readonly Func<Manifest> PreviousManifestFactory;
        public readonly Func<Type, bool> IsInActiveScope;

        public RuntimeComponentTypeGenerationProfile(
            string generatedCodePath,
            string generatedNamespace,
            string generatedClassName,
            Func<Manifest> previousManifestFactory,
            Func<Type, bool> isInActiveScope)
        {
            GeneratedCodePath = Require(generatedCodePath, nameof(generatedCodePath));
            GeneratedNamespace = Require(generatedNamespace, nameof(generatedNamespace));
            GeneratedClassName = Require(generatedClassName, nameof(generatedClassName));
            PreviousManifestFactory = previousManifestFactory
                                      ?? throw new ArgumentNullException(
                                          nameof(previousManifestFactory));
            IsInActiveScope = isInActiveScope
                              ?? throw new ArgumentNullException(
                                  nameof(isInActiveScope));
        }

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("A generated runtime component registry value is required.", parameterName);
            return parameterName == nameof(GeneratedCodePath)
                ? Path.GetFullPath(value)
                : value.Trim();
        }
    }

    public sealed class RuntimeComponentTypeGenerationResult
    {
        public Manifest Manifest;
        public bool GeneratedCodeChanged;
    }

    /// <summary>
    /// Generic GRC registry code generation. The previous generated static
    /// manifest is the reconciliation ledger. Active identity is the direct
    /// CLR Type; removed slots retain only their numeric ids. No string type
    /// lookup or StreamingAssets JSON participates in generation or runtime.
    /// </summary>
    public static class RuntimeComponentTypeManifestGenerator
    {
        public static RuntimeComponentTypeGenerationResult GenerateAndWrite(
            RuntimeComponentTypeGenerationProfile profile)
        {
            var output = Generate(profile);
            return new RuntimeComponentTypeGenerationResult
            {
                Manifest = output.Manifest,
                GeneratedCodeChanged = WriteIfChanged(
                    profile.GeneratedCodePath,
                    output.Source),
            };
        }

        public static bool IsOutputCurrent(
            RuntimeComponentTypeGenerationProfile profile)
        {
            var output = Generate(profile);
            return File.Exists(profile.GeneratedCodePath)
                   && string.Equals(
                       File.ReadAllText(profile.GeneratedCodePath),
                       output.Source,
                       StringComparison.Ordinal);
        }

        private sealed class GeneratedOutput
        {
            public Manifest Manifest;
            public string Source;
        }

        private static GeneratedOutput Generate(
            RuntimeComponentTypeGenerationProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));

            var existing = LoadExistingCompiledManifest(profile)
                           ?? CreateEmptyManifest();
            var manifest = Reconcile(
                existing,
                CollectRuntimeComponentTypes(profile.IsInActiveScope));
            return new GeneratedOutput
            {
                Manifest = manifest,
                Source = Emit(manifest, profile),
            };
        }

        private static Manifest LoadExistingCompiledManifest(
            RuntimeComponentTypeGenerationProfile profile)
        {
            var manifest = profile.PreviousManifestFactory();
            if (manifest == null)
            {
                throw new InvalidOperationException(
                    "Compiled runtime component manifest factory returned null.");
            }
            ValidateManifestHash(manifest);
            return manifest;
        }

        private static Manifest Reconcile(
            Manifest existing,
            IReadOnlyList<Type> discovered)
        {
            var entries = existing.Types?
                              .Where(entry => entry != null)
                              .OrderBy(entry => entry.Id)
                              .Select(Clone)
                              .ToList()
                          ?? new List<Entry>();
            var reservedSource = existing.ReservedIds ?? new List<int>();
            if (reservedSource.Any(id => id < 0)
                || reservedSource.Distinct().Count() != reservedSource.Count)
            {
                throw new InvalidOperationException(
                    "Compiled runtime component ledger contains invalid or duplicate reserved ids.");
            }
            var reserved = new HashSet<int>(reservedSource);
            var remainingTypes = new HashSet<Type>(discovered);
            var usedIds = new HashSet<int>(reserved);
            var existingTypes = new HashSet<Type>();
            var result = new List<Entry>();

            foreach (var entry in entries)
            {
                if (entry.Id < 0 || !usedIds.Add(entry.Id))
                    throw new InvalidOperationException($"Compiled runtime component ledger contains duplicate/invalid id {entry.Id}.");
                var runtimeType = entry.RuntimeType
                                  ?? throw new TypeLoadException(
                                      $"Active compiled runtime component entry id={entry.Id} requires RuntimeType = typeof(T).");
                if (!existingTypes.Add(runtimeType))
                {
                    throw new InvalidOperationException(
                        $"Compiled runtime component ledger contains duplicate type '{runtimeType.FullName}'.");
                }

                if (!remainingTypes.Remove(runtimeType))
                {
                    reserved.Add(entry.Id);
                    continue;
                }

                result.Add(RuntimeComponentTypeRegistry.CreateEntry(entry.Id, runtimeType));
            }

            var nextId = usedIds.Count == 0 ? -1 : usedIds.Max();
            foreach (var type in remainingTypes.OrderBy(TypeSortKey, StringComparer.Ordinal))
            {
                do
                {
                    nextId++;
                } while (!usedIds.Add(nextId));

                result.Add(RuntimeComponentTypeRegistry.CreateEntry(nextId, type));
            }

            var manifest = new Manifest
            {
                Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION,
                Types = result.OrderBy(entry => entry.Id).ToList(),
                ReservedIds = reserved.OrderBy(id => id).ToList(),
            };
            manifest.RegistryHash = RuntimeComponentTypeRegistry
                .CalculateRegistryHash(manifest.Types, manifest.ReservedIds);
            return manifest;
        }

        private static Manifest CreateEmptyManifest() => new()
        {
            Version = RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION,
            Types = new List<Entry>(),
            ReservedIds = new List<int>(),
        };

        private static void ValidateManifestHash(Manifest manifest)
        {
            if (manifest?.Types == null)
                throw new InvalidOperationException("Compiled runtime component ledger is invalid.");
            if (manifest.Version != RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION)
            {
                throw new InvalidOperationException(
                    $"Compiled runtime component ledger version {manifest.Version} does not match required version {RuntimeComponentTypeRegistry.CURRENT_MANIFEST_VERSION}.");
            }
            var calculated = RuntimeComponentTypeRegistry.CalculateRegistryHash(
                manifest.Types,
                manifest.ReservedIds);
            if (!string.Equals(
                    calculated,
                    manifest.RegistryHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Compiled runtime component ledger hash mismatch. Compiled={manifest.RegistryHash}, calculated={calculated}.");
            }
        }

        private static IReadOnlyList<Type> CollectRuntimeComponentTypes(
            Func<Type, bool> isInActiveScope)
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(IsPlayerRuntimeAssembly)
                .SelectMany(TakeLoadableTypes)
                .Where(IsRuntimeComponentType)
                .Where(isInActiveScope)
                .OrderBy(TypeSortKey, StringComparer.Ordinal)
                .ToArray();
        }

        private static bool IsRuntimeComponentType(Type type)
        {
            if (type == null || type.IsAbstract
                             || type == typeof(GameRuntimeComponent))
                return false;
            if (!typeof(GameRuntimeComponent).IsAssignableFrom(type)
                || typeof(ICommandLogic).IsAssignableFrom(type)
                || !IsPlayerRuntimeAssembly(type.Assembly))
                return false;
            return !ContainsNamespaceSegment(type.Namespace, "Editor")
                   && !ContainsNamespaceSegment(type.Namespace, "Tests")
                   && !ContainsNamespaceSegment(type.Namespace, "Examples")
                   && !ContainsNamespaceSegment(type.Namespace, "Samples");
        }

        private static bool IsPlayerRuntimeAssembly(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic)
                return false;
            var name = assembly.GetName().Name;
            return !string.IsNullOrWhiteSpace(name)
                   && !name.EndsWith("-Editor", StringComparison.OrdinalIgnoreCase)
                   && !name.EndsWith(".Editor", StringComparison.OrdinalIgnoreCase)
                   && name.IndexOf("Test", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static IEnumerable<Type> TakeLoadableTypes(Assembly assembly)
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

        private static bool ContainsNamespaceSegment(
            string value,
            string segment)
        {
            return !string.IsNullOrWhiteSpace(value)
                   && value.Split('.').Any(part => string.Equals(
                       part,
                       segment,
                       StringComparison.OrdinalIgnoreCase));
        }

        private static Entry Clone(Entry source) => new()
        {
            Id = source.Id,
            RuntimeType = source.RuntimeType,
        };

        private static string Emit(
            Manifest manifest,
            RuntimeComponentTypeGenerationProfile profile)
        {
            var builder = new StringBuilder();
            builder.AppendLine("// <auto-generated />")
                .AppendLine("using System.Collections.Generic;")
                .AppendLine("using DingoGameObjectsCMS.RuntimeObjects.Stores;")
                .AppendLine("using UnityEngine.Scripting;")
                .AppendLine()
                .Append("namespace ").Append(profile.GeneratedNamespace).AppendLine()
                .AppendLine("{")
                .AppendLine("    [Preserve]")
                .Append("    public static class ").Append(profile.GeneratedClassName).AppendLine()
                .AppendLine("    {")
                .Append("        public const string RegistryHash = ").Append(Quote(manifest.RegistryHash)).AppendLine(";")
                .AppendLine()
                .AppendLine("        public static void InitializeRegistry()")
                .AppendLine("        {")
                .AppendLine("            RuntimeComponentTypeRegistry.InitializeFromManifest(CreateManifest());")
                .AppendLine("        }")
                .AppendLine()
                .AppendLine("        public static Manifest CreateManifest()")
                .AppendLine("        {")
                .AppendLine("            return new Manifest")
                .AppendLine("            {")
                .Append("                Version = ").Append(manifest.Version).AppendLine(",")
                .AppendLine("                RegistryHash = RegistryHash,")
                .AppendLine("                Types = new List<Entry>")
                .AppendLine("                {");

            foreach (var entry in manifest.Types.OrderBy(value => value.Id))
            {
                var type = entry.RuntimeType
                           ?? throw new InvalidOperationException(
                               $"Active compiled runtime component entry id={entry.Id} requires RuntimeType = typeof(T).");
                builder.AppendLine("                    new Entry")
                    .AppendLine("                    {")
                    .Append("                        Id = ").Append(entry.Id).AppendLine(",")
                    .Append("                        RuntimeType = typeof(").Append(TypeName(type)).AppendLine("),")
                    .AppendLine("                    },");
            }

            builder.AppendLine("                },")
                .AppendLine("                ReservedIds = new List<int>")
                .AppendLine("                {");
            foreach (var id in manifest.ReservedIds.OrderBy(value => value))
                builder.Append("                    ").Append(id).AppendLine(",");
            builder.AppendLine("                },")
                .AppendLine("            };")
                .AppendLine("        }")
                .AppendLine("    }")
                .AppendLine("}");
            return builder.ToString().Replace("\r\n", "\n");
        }

        private static string TypeName(Type type) =>
            "global::" + type.FullName.Replace('+', '.');

        private static string TypeSortKey(Type type) =>
            type.Assembly.GetName().Name + ":" + type.FullName;

        private static string Quote(string value)
        {
            if (value == null)
                return "null";
            return "\"" + value.Replace("\\", "\\\\")
                       .Replace("\"", "\\\"")
                       .Replace("\r", "\\r")
                       .Replace("\n", "\\n")
                       .Replace("\t", "\\t") + "\"";
        }

        private static bool WriteIfChanged(string path, string content)
        {
            if (File.Exists(path)
                && string.Equals(
                    File.ReadAllText(path),
                    content,
                    StringComparison.Ordinal))
                return false;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException(
                    $"Generated output path '{path}' has no directory.");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                content,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
    }
}
