using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor
{
    public sealed class RuntimeBuildFingerprintGenerationProfile
    {
        public string ProjectRoot { get; }
        public string GeneratedAssetPath { get; }
        public string GeneratedNamespace { get; }
        public string GeneratedClassName { get; }
        public string GeneratedValueMemberName { get; }
        public string ApplicationIdentifier { get; }
        public string ApplicationVersion { get; }
        public IReadOnlyList<string> AdditionalInputPaths { get; }

        public string GeneratedOutputPath => Path.Combine(ProjectRoot, GeneratedAssetPath);

        public RuntimeBuildFingerprintGenerationProfile(
            string projectRoot,
            string generatedAssetPath,
            string generatedNamespace,
            string generatedClassName,
            string generatedValueMemberName,
            string applicationIdentifier,
            string applicationVersion,
            IReadOnlyList<string> additionalInputPaths = null)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("Unity project root is required.", nameof(projectRoot));
            ProjectRoot = Path.GetFullPath(projectRoot);
            if (string.IsNullOrWhiteSpace(generatedAssetPath) || Path.IsPathRooted(generatedAssetPath))
                throw new ArgumentException("Generated fingerprint path must be project-relative.", nameof(generatedAssetPath));
            GeneratedAssetPath = NormalizePath(generatedAssetPath.Trim());
            RequireUnderProject(Path.Combine(ProjectRoot, GeneratedAssetPath), nameof(generatedAssetPath));
            GeneratedNamespace = RequireNamespace(generatedNamespace, nameof(generatedNamespace));
            GeneratedClassName = RequireIdentifier(generatedClassName, nameof(generatedClassName));
            GeneratedValueMemberName = RequireIdentifier(generatedValueMemberName, nameof(generatedValueMemberName));
            ApplicationIdentifier = RequireValue(applicationIdentifier, nameof(applicationIdentifier));
            ApplicationVersion = RequireValue(applicationVersion, nameof(applicationVersion));

            var inputs = new List<string>();
            if (additionalInputPaths != null)
            {
                for (var i = 0; i < additionalInputPaths.Count; i++)
                {
                    var input = additionalInputPaths[i];
                    if (string.IsNullOrWhiteSpace(input))
                        throw new ArgumentException("Additional fingerprint inputs cannot contain empty paths.", nameof(additionalInputPaths));
                    var fullPath = Path.IsPathRooted(input)
                        ? Path.GetFullPath(input)
                        : Path.GetFullPath(Path.Combine(ProjectRoot, input));
                    RequireUnderProject(fullPath, nameof(additionalInputPaths));
                    inputs.Add(fullPath);
                }
            }
            AdditionalInputPaths = inputs.AsReadOnly();
        }

        private void RequireUnderProject(string path, string parameterName)
        {
            var fullPath = Path.GetFullPath(path);
            var rootWithSeparator = ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                    + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Fingerprint path '{path}' must be inside '{ProjectRoot}'.", parameterName);
        }

        private static string RequireNamespace(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Generated namespace is required.", parameterName);
            var result = value.Trim();
            var segments = result.Split('.');
            for (var i = 0; i < segments.Length; i++)
                RequireIdentifier(segments[i], parameterName);
            return result;
        }

        private static string RequireValue(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"{parameterName} is required.", parameterName);
            return value.Trim();
        }

        private static string RequireIdentifier(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Generated C# identifier is required.", parameterName);
            var result = value.Trim();
            if (!(char.IsLetter(result[0]) || result[0] == '_'))
                throw new ArgumentException($"Generated C# identifier '{result}' is invalid.", parameterName);
            for (var i = 1; i < result.Length; i++)
            {
                if (!(char.IsLetterOrDigit(result[i]) || result[i] == '_'))
                    throw new ArgumentException($"Generated C# identifier '{result}' is invalid.", parameterName);
            }
            return result;
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/');
    }

    /// <summary>
    /// Generates one deterministic player-content fingerprint shared by the
    /// client and dedicated-server builds produced from the same checkout.
    /// Runtime code never derives protocol identity from a mutable filesystem.
    /// </summary>
    public static class RuntimeBuildFingerprintGenerationCore
    {
        private static readonly string[] TransientPerformanceTestResources =
        {
            "Assets/Resources/PerformanceTestRunInfo.json",
            "Assets/Resources/PerformanceTestRunSettings.json",
        };

        public static bool GenerateAndWrite(RuntimeBuildFingerprintGenerationProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            var value = Calculate(profile);
            var content = EmitSource(profile, value);
            var path = profile.GeneratedOutputPath;
            if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
                return false;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return true;
        }

        public static string EmitSource(
            RuntimeBuildFingerprintGenerationProfile profile,
            string value)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Runtime build fingerprint value is required.", nameof(value));
            return
                $"namespace {profile.GeneratedNamespace}\n"
                + "{\n"
                + $"    public static class {profile.GeneratedClassName}\n"
                + "    {\n"
                + $"        public static readonly string {profile.GeneratedValueMemberName} = \"{value}\";\n"
                + "    }\n"
                + "}\n";
        }

        public static string Calculate(RuntimeBuildFingerprintGenerationProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(hash, "application.identifier", profile.ApplicationIdentifier);
            Append(hash, "application.version", profile.ApplicationVersion);

            foreach (var path in EnumerateInputs(profile))
            {
                var relative = NormalizePath(Path.GetRelativePath(profile.ProjectRoot, path));
                Append(hash, relative, ReadCanonicalContent(path));
            }

            return ToHex(hash.GetHashAndReset());
        }

        public static IEnumerable<string> EnumerateInputs(RuntimeBuildFingerprintGenerationProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPlayerCode(result, Path.Combine(profile.ProjectRoot, "Assets"), profile);
            AddPlayerCode(result, Path.Combine(profile.ProjectRoot, "Packages"), profile);
            AddEnabledSceneDependencies(result, profile);
            AddResources(result, Path.Combine(profile.ProjectRoot, "Assets"), profile);
            AddResources(result, Path.Combine(profile.ProjectRoot, "Packages"), profile);
            AddDirectoryContents(result, Path.Combine(profile.ProjectRoot, "Assets", "StreamingAssets"), includeMeta: false, profile);
            AddPlayerProjectSettings(result, Path.Combine(profile.ProjectRoot, "ProjectSettings"), profile);

            AddIfExists(result, Path.Combine(profile.ProjectRoot, "Packages", "manifest.json"), profile);
            AddIfExists(result, Path.Combine(profile.ProjectRoot, "Packages", "packages-lock.json"), profile);

            for (var i = 0; i < profile.AdditionalInputPaths.Count; i++)
            {
                var input = profile.AdditionalInputPaths[i];
                if (Directory.Exists(input))
                    AddDirectoryContents(result, input, includeMeta: true, profile);
                else
                    AddFileAndMeta(result, input, profile);
            }

            return result.OrderBy(path => NormalizePath(Path.GetRelativePath(profile.ProjectRoot, path)), StringComparer.Ordinal);
        }

        private static void AddPlayerCode(
            ISet<string> result,
            string root,
            RuntimeBuildFingerprintGenerationProfile profile)
        {
            if (!Directory.Exists(root))
                return;

            foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
            {
                var extension = Path.GetExtension(path);
                var isSource = string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(extension, ".asmdef", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(extension, ".asmref", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(extension, ".rsp", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
                               || string.Equals(Path.GetFileName(path), "link.xml", StringComparison.OrdinalIgnoreCase);
                if (!isSource)
                    continue;

                var relative = NormalizePath(Path.GetRelativePath(profile.ProjectRoot, path));
                if (ContainsSegment(relative, "Editor")
                    || ContainsSegment(relative, "Tests")
                    || ContainsSegment(relative, "Test")
                    || ContainsSegment(relative, "Examples")
                    || ContainsSegment(relative, "Example")
                    || ContainsSegment(relative, "Samples")
                    || ContainsSegment(relative, "Sample"))
                    continue;
                AddFileAndMeta(result, path, profile);
            }
        }

        private static void AddEnabledSceneDependencies(
            ISet<string> result,
            RuntimeBuildFingerprintGenerationProfile profile)
        {
            var scenes = EditorBuildSettings.scenes;
            for (var i = 0; i < scenes.Length; i++)
            {
                var scene = scenes[i];
                if (scene == null || !scene.enabled || string.IsNullOrWhiteSpace(scene.path))
                    continue;

                AddAssetDatabasePath(result, scene.path, profile);
                var dependencies = AssetDatabase.GetDependencies(scene.path, true);
                for (var dependencyIndex = 0; dependencyIndex < dependencies.Length; dependencyIndex++)
                    AddAssetDatabasePath(result, dependencies[dependencyIndex], profile);
            }
        }

        private static void AddResources(
            ISet<string> result,
            string assetsRoot,
            RuntimeBuildFingerprintGenerationProfile profile)
        {
            if (!Directory.Exists(assetsRoot))
                return;

            foreach (var path in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
            {
                var relative = NormalizePath(Path.GetRelativePath(profile.ProjectRoot, path));
                if (!ContainsSegment(relative, "Resources")
                    || ContainsSegment(relative, "Editor")
                    || ContainsSegment(relative, "Tests")
                    || ContainsSegment(relative, "Test")
                    || ContainsSegment(relative, "Samples")
                    || ContainsSegment(relative, "Sample")
                    || string.Equals(Path.GetExtension(path), ".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddFileAndMeta(result, path, profile);
            }
        }

        private static void AddDirectoryContents(
            ISet<string> result,
            string root,
            bool includeMeta,
            RuntimeBuildFingerprintGenerationProfile profile)
        {
            if (!Directory.Exists(root))
                return;

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!includeMeta && string.Equals(Path.GetExtension(path), ".meta", StringComparison.OrdinalIgnoreCase))
                    continue;
                AddIfExists(result, path, profile);
            }
        }

        private static void AddPlayerProjectSettings(
            ISet<string> result,
            string root,
            RuntimeBuildFingerprintGenerationProfile profile)
        {
            if (!Directory.Exists(root))
                return;

            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var fileName = Path.GetFileName(path);
                if (string.Equals(fileName, "EditorSettings.asset", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "PackageManagerSettings.asset", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "PresetManager.asset", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "SceneTemplateSettings.json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "VersionControlSettings.asset", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(fileName, "VirtualProjectsConfig.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                AddIfExists(result, path, profile);
            }
        }

        private static void AddAssetDatabasePath(
            ISet<string> result,
            string assetPath,
            RuntimeBuildFingerprintGenerationProfile profile)
        {
            var normalized = NormalizePath(assetPath);
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal)
                && !normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return;
            }

            AddFileAndMeta(result, Path.Combine(profile.ProjectRoot, normalized), profile);
        }

        private static void AddFileAndMeta(
            ISet<string> result,
            string path,
            RuntimeBuildFingerprintGenerationProfile profile)
        {
            AddIfExists(result, path, profile);
            AddIfExists(result, path + ".meta", profile);
        }

        private static bool ContainsSegment(string path, string segment)
        {
            var parts = path.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], segment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static void AddIfExists(
            ISet<string> result,
            string path,
            RuntimeBuildFingerprintGenerationProfile profile)
        {
            if (!File.Exists(path))
                return;

            var fullPath = Path.GetFullPath(path);
            var relative = NormalizePath(Path.GetRelativePath(profile.ProjectRoot, fullPath));
            if (string.Equals(relative, profile.GeneratedAssetPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, profile.GeneratedAssetPath + ".meta", StringComparison.OrdinalIgnoreCase)
                || IsTransientBuildInput(relative))
            {
                return;
            }

            result.Add(fullPath);
        }

        private static bool IsTransientBuildInput(string relativePath)
        {
            // The Performance Testing package creates these in its build
            // preprocessor and deletes them in postprocess. They describe the
            // current test invocation, not immutable player content.
            for (var i = 0; i < TransientPerformanceTestResources.Length; i++)
            {
                var resourcePath = TransientPerformanceTestResources[i];
                if (string.Equals(relativePath, resourcePath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(relativePath, resourcePath + ".meta", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Append(IncrementalHash hash, string key, string value)
        {
            Append(hash, key, Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static void Append(IncrementalHash hash, string key, byte[] value)
        {
            var keyBytes = Encoding.UTF8.GetBytes(key);
            AppendLength(hash, keyBytes.Length);
            hash.AppendData(keyBytes);
            AppendLength(hash, value.LongLength);
            hash.AppendData(value);
        }

        private static void AppendLength(IncrementalHash hash, long value)
        {
            var bytes = new byte[sizeof(long)];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = (byte)((ulong)value >> (i * 8));
            hash.AppendData(bytes);
        }

        private static byte[] ReadCanonicalContent(string path)
        {
            if (!IsText(path))
                return File.ReadAllBytes(path);

            var text = File.ReadAllText(path)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            if (string.Equals(Path.GetExtension(path), ".asset", StringComparison.OrdinalIgnoreCase))
                text = CanonicalizeUnityAssetYaml(text);
            return Encoding.UTF8.GetBytes(text);
        }

        private static string CanonicalizeUnityAssetYaml(string text)
        {
            // URP serializes two caches into authored assets while a player is
            // being built. Their values depend on the current client/server
            // subtarget, so they are neither authored content nor a stable
            // protocol identity input.
            var containsUrpPrefilterCache = text.IndexOf(
                "m_PrefilteringModeMainLightShadows:",
                StringComparison.Ordinal) >= 0;
            var containsRenderPipelineSettingsCache = text.IndexOf(
                                                          "m_SettingsList:",
                                                          StringComparison.Ordinal) >= 0
                                                      && text.IndexOf(
                                                          "m_RuntimeSettings:",
                                                          StringComparison.Ordinal) >= 0;
            if (!containsUrpPrefilterCache && !containsRenderPipelineSettingsCache)
                return text;

            var lines = text.Split('\n');
            var result = new StringBuilder(text.Length);
            var skippedRuntimeSettingsIndent = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                var indent = line.Length - trimmed.Length;

                if (skippedRuntimeSettingsIndent >= 0)
                {
                    if (trimmed.Length == 0 || indent > skippedRuntimeSettingsIndent)
                        continue;
                    skippedRuntimeSettingsIndent = -1;
                }

                if (containsUrpPrefilterCache
                    && trimmed.StartsWith("m_Prefilter", StringComparison.Ordinal)
                    && trimmed.IndexOf(':') >= 0)
                {
                    continue;
                }

                if (containsRenderPipelineSettingsCache
                    && trimmed.StartsWith("m_RuntimeSettings:", StringComparison.Ordinal))
                {
                    result.Append(' ', indent);
                    result.Append("m_RuntimeSettings: <build-derived>\n");
                    skippedRuntimeSettingsIndent = indent;
                    continue;
                }

                result.Append(line);
                if (i < lines.Length - 1)
                    result.Append('\n');
            }
            return result.ToString();
        }

        private static bool IsText(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".asset":
                case ".asmdef":
                case ".asmref":
                case ".cginc":
                case ".cs":
                case ".hlsl":
                case ".inputactions":
                case ".json":
                case ".meta":
                case ".prefab":
                case ".rsp":
                case ".shader":
                case ".txt":
                case ".unity":
                case ".uss":
                case ".uxml":
                case ".xml":
                case ".yaml":
                case ".yml":
                    return true;
                default:
                    return false;
            }
        }

        private static string ToHex(byte[] value)
        {
            var builder = new StringBuilder(value.Length * 2);
            for (var i = 0; i < value.Length; i++)
                builder.Append(value[i].ToString("x2"));
            return builder.ToString();
        }

        private static string NormalizePath(string path) => path.Replace('\\', '/');

    }
}
