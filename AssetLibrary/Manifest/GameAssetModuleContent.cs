using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    [Serializable, Preserve]
    public sealed class GameAssetModuleContentFile
    {
        private readonly byte[] _content;

        public string RelativePath { get; }
        public string Kind { get; }
        public long Size { get; }
        public string Sha256 { get; }

        public GameAssetModuleContentFile(
            string relativePath,
            string kind,
            byte[] content)
        {
            RelativePath = GameAssetModuleContentScanner
                .RequireCanonicalRelativePath(relativePath);
            Kind = string.IsNullOrWhiteSpace(kind)
                ? throw new ArgumentException(
                    "A GameAsset module file kind is required.",
                    nameof(kind))
                : kind;
            _content = content == null
                ? throw new ArgumentNullException(nameof(content))
                : (byte[])content.Clone();
            Size = _content.LongLength;
            Sha256 = GameAssetModuleContentScanner.CalculateBytesHash(_content);
        }

        public byte[] CopyBytes()
        {
            return (byte[])_content.Clone();
        }

        internal string ReadAllText()
        {
            using var stream = new MemoryStream(
                _content,
                writable: false);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
    }

    public readonly struct GameAssetModuleAssetEntry
    {
        public readonly GameAssetKey Key;
        public readonly Hash128 GUID;
        public readonly string RelativeJsonPath;

        public GameAssetModuleAssetEntry(
            GameAssetKey key,
            Hash128 guid,
            string relativeJsonPath)
        {
            Key = key;
            GUID = guid;
            RelativeJsonPath = GameAssetModuleContentScanner
                .RequireCanonicalRelativePath(relativeJsonPath);
        }
    }

    public sealed class GameAssetModuleContentSnapshot
    {
        private readonly Dictionary<string, GameAssetModuleContentFile> _files;

        public string SourceRootPath { get; }
        public string ModuleId { get; }
        public int ManifestVersion { get; }
        public string GeneratedUtc { get; }
        public IReadOnlyList<GameAssetModuleAssetEntry> Assets { get; }
        public IReadOnlyList<GameAssetModuleContentFile> Files { get; }
        public IReadOnlyList<ModDependency> DependsOn { get; }
        public string ContentHash { get; }

        public string AssetContentHash { get; }

        public GameAssetModuleContentSnapshot(
            string sourceRootPath,
            string moduleId,
            ModManifest manifest,
            IReadOnlyList<GameAssetModuleContentFile> files,
            IReadOnlyList<ModDependency> dependsOn = null)
        {
            DependsOn = dependsOn ?? Array.Empty<ModDependency>();
            SourceRootPath = Path.GetFullPath(
                sourceRootPath
                ?? throw new ArgumentNullException(nameof(sourceRootPath)));
            ModuleId = GameAssetModuleContentScanner
                .RequireCanonicalModuleId(moduleId);
            if (manifest == null)
            {
                throw new ArgumentNullException(nameof(manifest));
            }
            if (!string.Equals(
                    manifest.Mod,
                    ModuleId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"GameAsset manifest module '{manifest.Mod}' does not match snapshot module '{ModuleId}'.");
            }

            ManifestVersion = manifest.ManifestVersion;
            GeneratedUtc = manifest.GeneratedUtc;
            var sourceAssets = manifest.Assets
                               ?? new List<ModManifestEntry>();
            var assets = new GameAssetModuleAssetEntry[sourceAssets.Count];
            for (var index = 0; index < sourceAssets.Count; index++)
            {
                var entry = sourceAssets[index]
                            ?? throw new InvalidDataException(
                                $"GameAsset manifest entry {index} is null.");
                assets[index] = new GameAssetModuleAssetEntry(
                    entry.Key,
                    entry.GUID,
                    entry.RelativeJsonPath);
            }
            Assets = Array.AsReadOnly(assets);

            if (files == null)
            {
                throw new ArgumentNullException(nameof(files));
            }
            var copiedFiles = files.ToArray();
            Files = Array.AsReadOnly(copiedFiles);

            _files = new Dictionary<string, GameAssetModuleContentFile>(
                StringComparer.Ordinal);
            var caseInsensitivePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < copiedFiles.Length; index++)
            {
                var file = copiedFiles[index]
                           ?? throw new InvalidDataException(
                               $"GameAsset module '{ModuleId}' content entry {index} is null.");
                if (!caseInsensitivePaths.Add(file.RelativePath)
                    || !_files.TryAdd(file.RelativePath, file))
                {
                    throw new InvalidDataException(
                        $"GameAsset module '{ModuleId}' contains duplicate or case-colliding path '{file.RelativePath}'.");
                }
            }
            ContentHash = GameAssetModuleContentScanner.CalculateContentHash(
                ModuleId,
                copiedFiles);
            AssetContentHash = GameAssetModuleContentScanner.CalculateAssetContentHash(
                ModuleId,
                manifest,
                _files);
        }

        public bool Contains(string relativePath)
        {
            var canonical = GameAssetModuleContentScanner
                .RequireCanonicalRelativePath(relativePath);
            return _files.ContainsKey(canonical);
        }

        public GameAssetModuleContentFile RequireFile(string relativePath)
        {
            var canonical = GameAssetModuleContentScanner
                .RequireCanonicalRelativePath(relativePath);
            if (_files.TryGetValue(canonical, out var file))
            {
                return file;
            }

            throw new FileNotFoundException(
                $"GameAsset module '{ModuleId}' does not contain exact path '{canonical}'.",
                canonical);
        }

        public byte[] ReadAllBytes(string relativePath)
        {
            return RequireFile(relativePath).CopyBytes();
        }

        public string ReadAllText(string relativePath)
        {
            return RequireFile(relativePath).ReadAllText();
        }
    }

    public static class GameAssetModuleContentScanner
    {
        private const int CONTENT_HASH_FORMAT_VERSION = 1;
        private static StringComparison FileSystemPathComparison =>
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        public static GameAssetModuleContentSnapshot Scan(
            string moduleRoot,
            string expectedModuleId,
            params string[] excludedRelativePaths)
        {
            var root = RequireModuleRoot(moduleRoot);
            expectedModuleId = RequireCanonicalModuleId(expectedModuleId);
            var exclusions = BuildExclusions(excludedRelativePaths);

            var firstCapture = CaptureFiles(root, exclusions);
            var files = CaptureFiles(root, exclusions);
            if (!HaveIdenticalContent(firstCapture, files))
            {
                throw new IOException(
                    $"GameAsset module '{expectedModuleId}' changed while its session snapshot was being captured. Retry after the edit transaction completes.");
            }
            if (files.Count == 0)
            {
                throw new InvalidDataException(
                    $"GameAsset module '{expectedModuleId}' at '{root}' contains no files.");
            }

            var manifest = LoadAndValidateManifest(
                root,
                expectedModuleId,
                files);
            PromoteManifestAssets(files, manifest);
            return new GameAssetModuleContentSnapshot(
                root,
                expectedModuleId,
                manifest,
                Array.AsReadOnly(files.ToArray()),
                LoadDependencies(root, expectedModuleId, files));
        }

        public static string RequireCanonicalModuleId(string moduleId)
        {
            RequireCanonicalToken(moduleId, nameof(moduleId));
            if (string.Equals(moduleId, ".", StringComparison.Ordinal)
                || string.Equals(moduleId, "..", StringComparison.Ordinal)
                || moduleId.IndexOf('/') >= 0
                || moduleId.IndexOf('\\') >= 0
                || moduleId.IndexOf(':') >= 0)
            {
                throw new InvalidDataException(
                    $"GameAsset module id '{moduleId}' must be one canonical path segment.");
            }

            return moduleId;
        }

        public static string RequireCanonicalRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || !string.Equals(
                    relativePath,
                    relativePath.Trim(),
                    StringComparison.Ordinal)
                || Path.IsPathRooted(relativePath)
                || relativePath.IndexOf('\\') >= 0
                || relativePath.IndexOf(':') >= 0
                || relativePath.IndexOf('|') >= 0
                || relativePath.StartsWith("/", StringComparison.Ordinal)
                || relativePath.EndsWith("/", StringComparison.Ordinal)
                || relativePath.Any(character => character < ' '))
            {
                throw new InvalidDataException(
                    $"GameAsset module path '{relativePath}' is not canonical and relative.");
            }

            var parts = relativePath.Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                if (string.IsNullOrEmpty(parts[index])
                    || string.Equals(parts[index], ".", StringComparison.Ordinal)
                    || string.Equals(parts[index], "..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"GameAsset module path '{relativePath}' is not canonical and relative.");
                }
            }

            return relativePath;
        }

        public static string ResolveInsideRoot(
            string moduleRoot,
            string relativePath)
        {
            var root = RequireModuleRoot(moduleRoot);
            var canonical = RequireCanonicalRelativePath(relativePath);
            var resolved = Path.GetFullPath(
                Path.Combine(
                    root,
                    canonical.Replace(
                        '/',
                        Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(
                Path.DirectorySeparatorChar.ToString(),
                StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, FileSystemPathComparison))
            {
                throw new InvalidDataException(
                    $"GameAsset module path '{relativePath}' escapes '{root}'.");
            }

            return resolved;
        }

        public static string CalculateContentHash(
            string moduleId,
            IReadOnlyList<GameAssetModuleContentFile> files)
        {
            moduleId = RequireCanonicalModuleId(moduleId);
            if (files == null)
            {
                throw new ArgumentNullException(nameof(files));
            }

            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(
                       buffer,
                       Encoding.UTF8,
                       leaveOpen: true))
            {
                writer.Write(CONTENT_HASH_FORMAT_VERSION);
                writer.Write(moduleId);
                var ordered = files
                    .OrderBy(
                        file => file.RelativePath,
                        StringComparer.Ordinal)
                    .ToArray();
                writer.Write(ordered.Length);
                for (var index = 0; index < ordered.Length; index++)
                {
                    var file = ordered[index]
                               ?? throw new InvalidDataException(
                                   $"GameAsset module '{moduleId}' content entry {index} is null.");
                    writer.Write(file.RelativePath);
                    writer.Write(file.Kind);
                    writer.Write(file.Size);
                    writer.Write(file.Sha256);
                }
            }

            return CalculateBytesHash(buffer.ToArray());
        }

        public static string CalculateBytesHash(byte[] bytes)
        {
            if (bytes == null)
            {
                throw new ArgumentNullException(nameof(bytes));
            }
            using var sha = SHA256.Create();
            return ToLowerHex(sha.ComputeHash(bytes));
        }

        private static HashSet<string> BuildExclusions(
            IReadOnlyList<string> additionalExclusions)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (additionalExclusions == null)
            {
                return result;
            }
            for (var index = 0; index < additionalExclusions.Count; index++)
            {
                result.Add(RequireCanonicalRelativePath(
                    additionalExclusions[index]));
            }
            return result;
        }

        private static List<GameAssetModuleContentFile> CaptureFiles(
            string root,
            HashSet<string> exclusions)
        {
            var result = new List<GameAssetModuleContentFile>();
            var directories = new Stack<string>();
            directories.Push(root);
            while (directories.Count > 0)
            {
                var directory = directories.Pop();
                var childDirectories = Directory.GetDirectories(directory);
                Array.Sort(childDirectories, StringComparer.Ordinal);
                for (var index = childDirectories.Length - 1;
                     index >= 0;
                     index--)
                {
                    RejectReparsePoint(childDirectories[index]);
                    directories.Push(childDirectories[index]);
                }

                var diskFiles = Directory.GetFiles(directory);
                Array.Sort(diskFiles, StringComparer.Ordinal);
                for (var index = 0; index < diskFiles.Length; index++)
                {
                    RejectReparsePoint(diskFiles[index]);
                    var relativePath = ToCanonicalRelativePath(
                        root,
                        diskFiles[index]);
                    if (exclusions.Contains(relativePath))
                    {
                        continue;
                    }
                    var bytes = File.ReadAllBytes(diskFiles[index]);
                    result.Add(new GameAssetModuleContentFile(
                        relativePath,
                        Classify(relativePath),
                        bytes));
                }
            }

            result.Sort((left, right) =>
                StringComparer.Ordinal.Compare(
                    left.RelativePath,
                    right.RelativePath));
            ValidateUniquePaths(result);
            return result;
        }

        private static bool HaveIdenticalContent(
            IReadOnlyList<GameAssetModuleContentFile> left,
            IReadOnlyList<GameAssetModuleContentFile> right)
        {
            if (left.Count != right.Count)
            {
                return false;
            }
            for (var index = 0; index < left.Count; index++)
            {
                if (!string.Equals(
                        left[index].RelativePath,
                        right[index].RelativePath,
                        StringComparison.Ordinal)
                    || left[index].Size != right[index].Size
                    || !string.Equals(
                        left[index].Sha256,
                        right[index].Sha256,
                        StringComparison.Ordinal))
                {
                    return false;
                }
            }
            return true;
        }

        private static void ValidateUniquePaths(
            IReadOnlyList<GameAssetModuleContentFile> files)
        {
            var exactPaths = new HashSet<string>(StringComparer.Ordinal);
            var insensitivePaths = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index]
                           ?? throw new InvalidDataException(
                               $"GameAsset module content entry {index} is null.");
                RequireCanonicalRelativePath(file.RelativePath);
                if (!exactPaths.Add(file.RelativePath)
                    || !insensitivePaths.Add(file.RelativePath))
                {
                    throw new InvalidDataException(
                        $"GameAsset module contains duplicate or case-colliding path '{file.RelativePath}'.");
                }
            }
        }

        private static ModManifest LoadAndValidateManifest(
            string root,
            string expectedModuleId,
            IReadOnlyList<GameAssetModuleContentFile> files)
        {
            const string manifestRelativePath =
                ModManifest.FILE_NAME;
            var filesByPath = files.ToDictionary(
                file => file.RelativePath,
                StringComparer.Ordinal);
            if (!filesByPath.TryGetValue(
                    manifestRelativePath,
                    out var manifestFile)
                || !string.Equals(
                    manifestFile.Kind,
                    "manifest",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"GameAsset module '{expectedModuleId}' must contain exact path manifest.json.");
            }

            ModManifest manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<ModManifest>(
                    manifestFile.ReadAllText(),
                    GameAssetJson.DataSettings);
            }
            catch (Exception exception) when (
                exception is JsonException
                || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    $"GameAsset manifest '{Path.Combine(root, manifestRelativePath)}' is invalid JSON.",
                    exception);
            }

            if (manifest == null
                || !string.Equals(
                    manifest.Mod,
                    expectedModuleId,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"GameAsset manifest at '{root}' must declare exact module '{expectedModuleId}'.");
            }

            manifest.Assets ??= new List<ModManifestEntry>();
            var manifestPaths = new HashSet<string>(StringComparer.Ordinal);
            var manifestKeys = new HashSet<GameAssetKey>(
                new GameAssetKeyComparer());
            var manifestGuids = new HashSet<Hash128>();
            for (var index = 0; index < manifest.Assets.Count; index++)
            {
                var entry = manifest.Assets[index]
                            ?? throw new InvalidDataException(
                                $"GameAsset manifest entry {index} is null.");
                if (!string.Equals(
                        entry.Key.Mod,
                        expectedModuleId,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest entry '{entry.Key}' belongs to another module.");
                }
                try
                {
                    GameAssetVersionUtils.RequireCanonical(
                        entry.Key.Version,
                        nameof(entry.Key.Version));
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest entry '{entry.Key}' has an invalid version.",
                        exception);
                }
                if (!entry.GUID.isValid)
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest entry '{entry.Key}' has no valid GUID.");
                }
                if (!manifestKeys.Add(entry.Key))
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest key '{entry.Key}' is duplicated.");
                }
                if (!manifestGuids.Add(entry.GUID))
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest GUID '{entry.GUID}' is duplicated.");
                }

                var relativePath = RequireCanonicalRelativePath(
                    entry.RelativeJsonPath);
                if (!manifestPaths.Add(relativePath))
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest path '{relativePath}' is duplicated.");
                }
                if (!filesByPath.TryGetValue(relativePath, out var assetFile))
                {
                    throw new FileNotFoundException(
                        $"GameAsset manifest path '{relativePath}' is absent from module '{expectedModuleId}'.",
                        relativePath);
                }
                if (!string.Equals(
                        assetFile.Kind,
                        "json",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest path '{relativePath}' is "
                        + $"'{assetFile.Kind}', expected ordinary JSON.");
                }
                ValidateAssetIdentity(
                    assetFile,
                    entry,
                    expectedModuleId);
            }

            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                if (string.Equals(
                        file.Kind,
                        "gameAsset",
                        StringComparison.Ordinal)
                    && !manifestPaths.Contains(file.RelativePath))
                {
                    throw new InvalidDataException(
                        $"GameAsset JSON '{file.RelativePath}' is not listed by module '{expectedModuleId}' manifest.");
                }
            }

            return manifest;
        }

        public static string CalculateAssetContentHash(
            string moduleId,
            ModManifest manifest,
            IReadOnlyDictionary<string, GameAssetModuleContentFile> filesByPath)
        {
            moduleId = RequireCanonicalModuleId(moduleId);
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(CONTENT_HASH_FORMAT_VERSION);
                writer.Write(moduleId);
                var ordered = (manifest.Assets ?? new List<ModManifestEntry>())
                    .OrderBy(entry => entry.RelativeJsonPath, StringComparer.Ordinal)
                    .ToArray();
                writer.Write(ordered.Length);
                for (var index = 0; index < ordered.Length; index++)
                {
                    var entry = ordered[index];
                    writer.Write(entry.RelativeJsonPath);
                    writer.Write(entry.Key.ToString());
                    writer.Write(entry.GUID.ToString());
                    writer.Write(
                        filesByPath.TryGetValue(entry.RelativeJsonPath, out var file)
                            ? file.Sha256
                            : string.Empty);
                }
            }

            return CalculateBytesHash(buffer.ToArray());
        }

        private static IReadOnlyList<ModDependency> LoadDependencies(
            string root,
            string expectedModuleId,
            IReadOnlyList<GameAssetModuleContentFile> files)
        {
            GameAssetModuleContentFile dependencyFile = null;
            for (var index = 0; index < files.Count; index++)
            {
                if (string.Equals(
                        files[index].RelativePath,
                        ModDependencies.FILE_NAME,
                        StringComparison.Ordinal))
                {
                    dependencyFile = files[index];
                    break;
                }
            }
            if (dependencyFile == null)
            {
                return Array.Empty<ModDependency>();
            }

            ModDependencies dependencies;
            try
            {
                dependencies = JsonConvert.DeserializeObject<ModDependencies>(
                    dependencyFile.ReadAllText(),
                    GameAssetJson.DataSettings);
            }
            catch (Exception exception) when (
                exception is JsonException
                || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    $"GameAsset dependency file '{Path.Combine(root, ModDependencies.FILE_NAME)}' is invalid JSON.",
                    exception);
            }

            if (dependencies == null)
            {
                throw new InvalidDataException(
                    $"GameAsset dependency file at '{root}' is empty.");
            }

            var declared = dependencies.DependsOn ?? new List<ModDependency>();
            var unique = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < declared.Count; index++)
            {
                var dependency = declared[index]
                                 ?? throw new InvalidDataException(
                                     $"GameAsset module '{expectedModuleId}' dependency {index} is null.");
                dependency.Mod = RequireCanonicalModuleId(dependency.Mod);
                if (string.Equals(dependency.Mod, expectedModuleId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"GameAsset module '{expectedModuleId}' declares a dependency on itself.");
                }
                if (!unique.Add(dependency.Mod))
                {
                    throw new InvalidDataException(
                        $"GameAsset module '{expectedModuleId}' declares duplicate dependency '{dependency.Mod}'.");
                }
                if (string.IsNullOrWhiteSpace(dependency.ContentHash))
                {
                    throw new InvalidDataException(
                        $"GameAsset module '{expectedModuleId}' dependency '{dependency.Mod}' has no ContentHash. Pin the value its manifest publishes.");
                }
            }

            return Array.AsReadOnly(declared
                .OrderBy(value => value.Mod, StringComparer.Ordinal)
                .ToArray());
        }

        private static void PromoteManifestAssets(
            IList<GameAssetModuleContentFile> files,
            ModManifest manifest)
        {
            var assetPaths = new HashSet<string>(
                manifest.Assets.Select(entry =>
                    RequireCanonicalRelativePath(
                        entry.RelativeJsonPath)),
                StringComparer.Ordinal);
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                if (!assetPaths.Contains(file.RelativePath))
                {
                    continue;
                }

                files[index] = new GameAssetModuleContentFile(
                    file.RelativePath,
                    "gameAsset",
                    file.CopyBytes());
            }
        }

        private static void ValidateAssetIdentity(
            GameAssetModuleContentFile file,
            ModManifestEntry expected,
            string moduleId)
        {
            GameAssetIdentityDocument identity;
            try
            {
                identity = JsonConvert.DeserializeObject<GameAssetIdentityDocument>(
                    file.ReadAllText(),
                    GameAssetJson.DataSettings);
            }
            catch (Exception exception) when (
                exception is JsonException
                || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    $"GameAsset '{moduleId}:{file.RelativePath}' is invalid JSON.",
                    exception);
            }
            if (identity == null
                || !identity.GUID.isValid
                || identity.Key != expected.Key
                || identity.GUID != expected.GUID)
            {
                throw new InvalidDataException(
                    $"GameAsset '{moduleId}:{file.RelativePath}' identity does not match its manifest entry '{expected.Key}' / '{expected.GUID}'.");
            }
        }

        private static string RequireModuleRoot(string moduleRoot)
        {
            if (string.IsNullOrWhiteSpace(moduleRoot))
            {
                throw new ArgumentException(
                    "A GameAsset module root is required.",
                    nameof(moduleRoot));
            }

            var root = Path.GetFullPath(moduleRoot);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException(
                    $"GameAsset module root '{root}' does not exist.");
            }
            RejectReparsePoint(root);
            return root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        }

        private static string ToCanonicalRelativePath(
            string root,
            string absolutePath)
        {
            var relativePath = Path.GetRelativePath(
                    root,
                    Path.GetFullPath(absolutePath))
                .Replace('\\', '/');
            return RequireCanonicalRelativePath(relativePath);
        }

        private static void RejectReparsePoint(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"GameAsset module path '{path}' uses a reparse point and cannot be mounted safely.");
            }
        }

        private static string Classify(string relativePath)
        {
            if (string.Equals(
                    relativePath,
                    ModManifest.FILE_NAME,
                    StringComparison.Ordinal))
            {
                return "manifest";
            }
            if (string.Equals(
                    relativePath,
                    ModDependencies.FILE_NAME,
                    StringComparison.Ordinal))
            {
                return "dependency";
            }

            var extension = Path.GetExtension(relativePath).ToLowerInvariant();
            if (extension == ".json")
            {
                if (string.Equals(
                        relativePath,
                        SpriteRenderModulePresets.RESOURCE_PATH,
                        StringComparison.Ordinal))
                {
                    return "spriteRenderPresets";
                }
                if (relativePath.EndsWith(
                        ".recipe.json",
                        StringComparison.Ordinal))
                {
                    return "spriteRecipe";
                }
                if (IsAtlasResource(relativePath))
                {
                    return "atlasMetadata";
                }
                return "json";
            }
            if (extension == ".png")
            {
                if (relativePath.Contains(
                        "/depth",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return "depthMap";
                }
                if (IsAtlasResource(relativePath))
                {
                    return "atlasPage";
                }
                return "sprite";
            }
            return "metadata";
        }

        private static bool IsAtlasResource(string relativePath)
        {
            return relativePath.StartsWith(
                       "atlas/",
                       StringComparison.OrdinalIgnoreCase)
                   || relativePath.StartsWith(
                       "render_atlas/",
                       StringComparison.OrdinalIgnoreCase)
                   || relativePath.Contains(
                       "/atlas/",
                       StringComparison.OrdinalIgnoreCase)
                   || relativePath.Contains(
                       "/render_atlas/",
                       StringComparison.OrdinalIgnoreCase);
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("x2"));
            }
            return builder.ToString();
        }

        private static void RequireCanonicalToken(
            string value,
            string field)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !string.Equals(
                    value,
                    value.Trim(),
                    StringComparison.Ordinal)
                || value.IndexOf('\r') >= 0
                || value.IndexOf('\n') >= 0
                || value.IndexOf('|') >= 0)
            {
                throw new InvalidDataException(
                    $"GameAsset module {field} is empty or not canonical.");
            }
        }

        private sealed class GameAssetIdentityDocument
        {
            public GameAssetKey Key;
            public Hash128 GUID;
        }

        private sealed class GameAssetKeyComparer : IEqualityComparer<GameAssetKey>
        {
            public bool Equals(GameAssetKey left, GameAssetKey right)
            {
                return string.Equals(left.Mod, right.Mod, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(left.Type, right.Type, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(left.Version, right.Version, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(GameAssetKey value)
            {
                unchecked
                {
                    var hash = 17;
                    hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(value.Mod ?? string.Empty);
                    hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(value.Type ?? string.Empty);
                    hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(value.Key ?? string.Empty);
                    hash = hash * 31 + StringComparer.OrdinalIgnoreCase.GetHashCode(value.Version ?? string.Empty);
                    return hash;
                }
            }
        }
    }
}
