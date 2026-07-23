using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    [Serializable, Preserve]
    public class GameAssetModulePackageFile
    {
        public string RelativePath;
        public string Kind;
        public long Size;
        public string Sha256;
    }

    [Serializable, Preserve]
    public class GameAssetModulePackageLock
    {
        public const int CURRENT_FORMAT_VERSION = 1;

        public int FormatVersion = CURRENT_FORMAT_VERSION;
        public string ModuleId;
        public string ModuleVersion;
        public string RuntimeSchemaHash;
        public string CatalogHash;
        public List<GameAssetModulePackageFile> Files = new();
    }

    public class GameAssetVerifiedPackage
    {
        private readonly Dictionary<string, GameAssetModulePackageFile> _files;

        public readonly string RootPath;
        public readonly GameAssetModulePackageLock PackageLock;

        public string ModuleId => PackageLock.ModuleId;
        public string CatalogHash => PackageLock.CatalogHash;
        public string RuntimeSchemaHash => PackageLock.RuntimeSchemaHash;

        public GameAssetVerifiedPackage(string rootPath, GameAssetModulePackageLock packageLock)
        {
            RootPath = Path.GetFullPath(rootPath ?? throw new ArgumentNullException(nameof(rootPath)));
            PackageLock = packageLock ?? throw new ArgumentNullException(nameof(packageLock));
            _files = new Dictionary<string, GameAssetModulePackageFile>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < PackageLock.Files.Count; i++)
            {
                var file = PackageLock.Files[i];
                _files.Add(file.RelativePath, file);
            }
        }

        public bool Contains(string relativePath)
        {
            var canonical = GameAssetModulePackageFileUtils.RequireCanonicalRelativePath(relativePath);
            return _files.ContainsKey(canonical);
        }

        public string Resolve(in GameAssetResourceRef resource)
        {
            _ = RequireFile(resource);
            return GameAssetModulePackageFileUtils.ResolveInsideRoot(RootPath, resource.RelativePath);
        }

        public string RequireKind(in GameAssetResourceRef resource)
        {
            return RequireFile(resource).Kind;
        }

        private GameAssetModulePackageFile RequireFile(in GameAssetResourceRef resource)
        {
            if (!string.Equals(resource.ModuleId, ModuleId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Resource module '{resource.ModuleId}' cannot be resolved from verified module '{ModuleId}'.");
            }

            var canonical = GameAssetModulePackageFileUtils.RequireCanonicalRelativePath(resource.RelativePath);
            if (!_files.TryGetValue(canonical, out var file))
            {
                throw new FileNotFoundException(
                    $"Verified GameAsset module '{ModuleId}' does not declare resource '{canonical}'.",
                    canonical);
            }

            return file;
        }

        public T LoadJson<T>(in GameAssetResourceRef resource)
        {
            var path = Resolve(resource);
            try
            {
                var value = JsonConvert.DeserializeObject<T>(File.ReadAllText(path), GameAssetJson.Settings);
                if (value == null)
                {
                    throw new InvalidDataException(
                        $"Verified GameAsset resource '{resource}' produced a null {typeof(T).FullName} value.");
                }
                return value;
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
            {
                throw new InvalidDataException(
                    $"Verified GameAsset resource '{resource}' is not valid {typeof(T).FullName} JSON.",
                    exception);
            }
        }
    }

    public static class GameAssetModulePackageFileUtils
    {
        public const string PACKAGE_LOCK_FILE_NAME = "package.lock.json";

        private static readonly JsonSerializerSettings PackageJsonSettings = new()
        {
            ContractResolver = new RequiredCamelCaseContractResolver(),
            Culture = CultureInfo.InvariantCulture,
            DateParseHandling = DateParseHandling.None,
            MissingMemberHandling = MissingMemberHandling.Error,
            NullValueHandling = NullValueHandling.Include,
            ObjectCreationHandling = ObjectCreationHandling.Replace,
        };

        public static GameAssetModulePackageLock Build(
            string packageRoot,
            string moduleId,
            string moduleVersion,
            string runtimeSchemaHash,
            params string[] excludedRelativePaths)
        {
            var root = RequirePackageRoot(packageRoot);
            RequireCanonicalToken(moduleId, nameof(moduleId));
            RequireCanonicalToken(moduleVersion, nameof(moduleVersion));
            RequireLowerHex(runtimeSchemaHash, 64, nameof(runtimeSchemaHash));

            var exclusions = new HashSet<string>(StringComparer.Ordinal)
            {
                PACKAGE_LOCK_FILE_NAME,
            };
            if (excludedRelativePaths != null)
            {
                for (var i = 0; i < excludedRelativePaths.Length; i++)
                {
                    exclusions.Add(RequireCanonicalRelativePath(excludedRelativePaths[i]));
                }
            }

            var files = new List<GameAssetModulePackageFile>();
            var diskFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            for (var i = 0; i < diskFiles.Length; i++)
            {
                RejectReparsePoints(root, diskFiles[i]);
                var relativePath = ToCanonicalRelativePath(root, diskFiles[i]);
                if (exclusions.Contains(relativePath))
                {
                    continue;
                }

                var info = new FileInfo(diskFiles[i]);
                files.Add(new GameAssetModulePackageFile
                {
                    RelativePath = relativePath,
                    Kind = Classify(relativePath),
                    Size = info.Length,
                    Sha256 = CalculateFileHash(diskFiles[i]),
                });
            }

            files.Sort((left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
            var packageLock = new GameAssetModulePackageLock
            {
                ModuleId = moduleId,
                ModuleVersion = moduleVersion,
                RuntimeSchemaHash = runtimeSchemaHash,
                Files = files,
            };
            packageLock.CatalogHash = CalculateCatalogHash(packageLock);
            ValidateManifestCoverage(root, packageLock);
            return packageLock;
        }

        public static void Write(string packageRoot, GameAssetModulePackageLock packageLock)
        {
            ValidateDocument(packageLock, expectedModuleId: null, expectedRuntimeSchemaHash: null);
            var root = RequirePackageRoot(packageRoot);
            var path = Path.Combine(root, PACKAGE_LOCK_FILE_NAME);
            var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var json = JsonConvert.SerializeObject(packageLock, Formatting.Indented, PackageJsonSettings);
            File.WriteAllText(temporaryPath, NormalizeNewlines(json) + "\n", new UTF8Encoding(false));
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(temporaryPath, path, null);
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        public static GameAssetVerifiedPackage LoadAndVerify(
            string packageRoot,
            string expectedModuleId,
            string expectedRuntimeSchemaHash,
            params string[] excludedRelativePaths)
        {
            var root = RequirePackageRoot(packageRoot);
            var lockPath = Path.Combine(root, PACKAGE_LOCK_FILE_NAME);
            if (!File.Exists(lockPath))
            {
                throw new FileNotFoundException(
                    $"GameAsset module package at '{root}' has no self-lock '{PACKAGE_LOCK_FILE_NAME}'.",
                    lockPath);
            }

            GameAssetModulePackageLock packageLock;
            try
            {
                packageLock = JsonConvert.DeserializeObject<GameAssetModulePackageLock>(
                    File.ReadAllText(lockPath),
                    PackageJsonSettings);
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
            {
                throw new InvalidDataException($"GameAsset module package lock '{lockPath}' is invalid JSON.", exception);
            }

            ValidateDocument(packageLock, expectedModuleId, expectedRuntimeSchemaHash);
            var exclusions = new HashSet<string>(StringComparer.Ordinal)
            {
                PACKAGE_LOCK_FILE_NAME,
            };
            if (excludedRelativePaths != null)
            {
                for (var i = 0; i < excludedRelativePaths.Length; i++)
                {
                    exclusions.Add(RequireCanonicalRelativePath(excludedRelativePaths[i]));
                }
            }

            var declared = new Dictionary<string, GameAssetModulePackageFile>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < packageLock.Files.Count; i++)
            {
                var file = packageLock.Files[i];
                declared.Add(file.RelativePath, file);
                var absolutePath = ResolveInsideRoot(root, file.RelativePath);
                RejectReparsePoints(root, absolutePath);
                if (!File.Exists(absolutePath))
                {
                    throw new FileNotFoundException(
                        $"GameAsset module '{packageLock.ModuleId}' is incomplete: '{file.RelativePath}' is missing.",
                        absolutePath);
                }

                var actualSize = new FileInfo(absolutePath).Length;
                if (actualSize != file.Size)
                {
                    throw new InvalidDataException(
                        $"GameAsset module file '{file.RelativePath}' has size {actualSize}, expected {file.Size}.");
                }

                var actualHash = CalculateFileHash(absolutePath);
                if (!string.Equals(actualHash, file.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"GameAsset module file '{file.RelativePath}' hash does not match its self-lock.");
                }
            }

            var diskFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
            for (var i = 0; i < diskFiles.Length; i++)
            {
                RejectReparsePoints(root, diskFiles[i]);
                var relativePath = ToCanonicalRelativePath(root, diskFiles[i]);
                if (!exclusions.Contains(relativePath) && !declared.ContainsKey(relativePath))
                {
                    throw new InvalidDataException(
                        $"GameAsset module '{packageLock.ModuleId}' contains undeclared file '{relativePath}'.");
                }
            }

            ValidateManifestCoverage(root, packageLock);
            return new GameAssetVerifiedPackage(root, packageLock);
        }

        public static string RequireCanonicalRelativePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath)
                || !string.Equals(relativePath, relativePath.Trim(), StringComparison.Ordinal)
                || Path.IsPathRooted(relativePath)
                || relativePath.IndexOf('\\') >= 0
                || relativePath.IndexOf(':') >= 0
                || relativePath.StartsWith("/", StringComparison.Ordinal)
                || relativePath.EndsWith("/", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"GameAsset package path '{relativePath}' is not canonical and relative.");
            }

            var parts = relativePath.Split('/');
            for (var i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i])
                    || string.Equals(parts[i], ".", StringComparison.Ordinal)
                    || string.Equals(parts[i], "..", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"GameAsset package path '{relativePath}' is not canonical and relative.");
                }
            }

            return relativePath;
        }

        public static string ResolveInsideRoot(string packageRoot, string relativePath)
        {
            var root = RequirePackageRoot(packageRoot);
            var canonical = RequireCanonicalRelativePath(relativePath);
            var resolved = Path.GetFullPath(Path.Combine(root, canonical.Replace('/', Path.DirectorySeparatorChar)));
            var prefix = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"GameAsset package path '{relativePath}' escapes '{root}'.");
            }

            return resolved;
        }

        public static string CalculateCatalogHash(GameAssetModulePackageLock packageLock)
        {
            if (packageLock == null)
            {
                throw new ArgumentNullException(nameof(packageLock));
            }

            var builder = new StringBuilder();
            builder.Append("formatVersion|").Append(packageLock.FormatVersion).Append('\n')
                .Append("moduleId|").Append(packageLock.ModuleId).Append('\n')
                .Append("moduleVersion|").Append(packageLock.ModuleVersion).Append('\n')
                .Append("runtimeSchemaHash|").Append(packageLock.RuntimeSchemaHash).Append('\n');
            var ordered = packageLock.Files.OrderBy(file => file.RelativePath, StringComparer.Ordinal);
            foreach (var file in ordered)
            {
                builder.Append("file|").Append(file.RelativePath).Append('|')
                    .Append(file.Kind).Append('|')
                    .Append(file.Size).Append('|')
                    .Append(file.Sha256).Append('\n');
            }

            using var sha = SHA256.Create();
            return ToLowerHex(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static void ValidateDocument(
            GameAssetModulePackageLock packageLock,
            string expectedModuleId,
            string expectedRuntimeSchemaHash)
        {
            if (packageLock == null)
            {
                throw new InvalidDataException("GameAsset module package lock is null.");
            }
            if (packageLock.FormatVersion != GameAssetModulePackageLock.CURRENT_FORMAT_VERSION)
            {
                throw new InvalidDataException(
                    $"GameAsset package lock format {packageLock.FormatVersion} does not match required format "
                    + $"{GameAssetModulePackageLock.CURRENT_FORMAT_VERSION}.");
            }

            RequireCanonicalToken(packageLock.ModuleId, "moduleId");
            RequireCanonicalToken(packageLock.ModuleVersion, "moduleVersion");
            RequireLowerHex(packageLock.RuntimeSchemaHash, 64, "runtimeSchemaHash");
            RequireLowerHex(packageLock.CatalogHash, 64, "catalogHash");
            if (!string.IsNullOrWhiteSpace(expectedModuleId)
                && !string.Equals(packageLock.ModuleId, expectedModuleId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"GameAsset package declares module '{packageLock.ModuleId}', expected '{expectedModuleId}'.");
            }
            if (!string.IsNullOrWhiteSpace(expectedRuntimeSchemaHash)
                && !string.Equals(packageLock.RuntimeSchemaHash, expectedRuntimeSchemaHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"GameAsset package runtime schema '{packageLock.RuntimeSchemaHash}' does not match "
                    + $"Player schema '{expectedRuntimeSchemaHash}'.");
            }
            if (packageLock.Files == null || packageLock.Files.Count == 0)
            {
                throw new InvalidDataException("GameAsset package lock has no files.");
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string previousPath = null;
            for (var i = 0; i < packageLock.Files.Count; i++)
            {
                var file = packageLock.Files[i]
                           ?? throw new InvalidDataException($"GameAsset package file entry {i} is null.");
                RequireCanonicalRelativePath(file.RelativePath);
                RequireCanonicalToken(file.Kind, $"files[{i}].kind");
                if (file.Size < 0)
                {
                    throw new InvalidDataException($"GameAsset package file '{file.RelativePath}' has a negative size.");
                }
                RequireLowerHex(file.Sha256, 64, $"files[{i}].sha256");
                if (!paths.Add(file.RelativePath))
                {
                    throw new InvalidDataException(
                        $"GameAsset package file '{file.RelativePath}' is duplicated or has a case collision.");
                }
                if (previousPath != null && StringComparer.Ordinal.Compare(previousPath, file.RelativePath) >= 0)
                {
                    throw new InvalidDataException("GameAsset package files must use unique ordinal path order.");
                }
                previousPath = file.RelativePath;
            }

            var actualCatalogHash = CalculateCatalogHash(packageLock);
            if (!string.Equals(actualCatalogHash, packageLock.CatalogHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("GameAsset package catalog hash does not match its canonical file table.");
            }
        }

        private static void ValidateManifestCoverage(string root, GameAssetModulePackageLock packageLock)
        {
            const string manifestRelativePath = "manifest.json";
            var manifestFile = packageLock.Files.SingleOrDefault(
                file => string.Equals(file.RelativePath, manifestRelativePath, StringComparison.Ordinal));
            if (manifestFile == null || !string.Equals(manifestFile.Kind, "manifest", StringComparison.Ordinal))
            {
                throw new InvalidDataException("GameAsset package self-lock must include manifest.json as kind 'manifest'.");
            }

            var manifestPath = ResolveInsideRoot(root, manifestRelativePath);
            ModManifest manifest;
            try
            {
                manifest = JsonConvert.DeserializeObject<ModManifest>(
                    File.ReadAllText(manifestPath),
                    GameAssetJson.Settings);
            }
            catch (Exception exception) when (exception is JsonException || exception is ArgumentException)
            {
                throw new InvalidDataException($"GameAsset manifest '{manifestPath}' is invalid JSON.", exception);
            }
            if (manifest == null || !string.Equals(manifest.Mod, packageLock.ModuleId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"GameAsset manifest must declare exact module '{packageLock.ModuleId}'.");
            }

            manifest.Assets ??= new List<ModManifestEntry>();
            var declaredFiles = new HashSet<string>(
                packageLock.Files.Select(file => file.RelativePath),
                StringComparer.OrdinalIgnoreCase);
            var manifestPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < manifest.Assets.Count; i++)
            {
                var entry = manifest.Assets[i]
                            ?? throw new InvalidDataException($"GameAsset manifest entry {i} is null.");
                var relativePath = RequireCanonicalRelativePath(entry.RelativeJsonPath);
                if (!manifestPaths.Add(relativePath))
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest path '{relativePath}' is duplicated or has a case collision.");
                }
                if (!declaredFiles.Contains(relativePath))
                {
                    throw new InvalidDataException(
                        $"GameAsset manifest path '{relativePath}' is absent from the package self-lock.");
                }
            }
        }

        private static string RequirePackageRoot(string packageRoot)
        {
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                throw new ArgumentException("A GameAsset package root is required.", nameof(packageRoot));
            }

            var root = Path.GetFullPath(packageRoot);
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"GameAsset package root '{root}' does not exist.");
            }
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"GameAsset package root '{root}' uses a reparse point and cannot be self-locked safely.");
            }

            return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static string ToCanonicalRelativePath(string root, string absolutePath)
        {
            var relativePath = Path.GetRelativePath(root, Path.GetFullPath(absolutePath)).Replace('\\', '/');
            return RequireCanonicalRelativePath(relativePath);
        }

        private static void RejectReparsePoints(string root, string absolutePath)
        {
            var current = Path.GetFullPath(absolutePath);
            while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"GameAsset package path '{current}' uses a reparse point and cannot be self-locked safely.");
                }
                current = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(current))
                {
                    throw new InvalidDataException($"GameAsset package path '{absolutePath}' is outside '{root}'.");
                }
            }
        }

        private static string Classify(string relativePath)
        {
            if (string.Equals(relativePath, "manifest.json", StringComparison.Ordinal))
            {
                return "manifest";
            }

            var extension = Path.GetExtension(relativePath).ToLowerInvariant();
            if (extension == ".json")
            {
                if (relativePath.EndsWith(".recipe.json", StringComparison.Ordinal))
                {
                    return "spriteRecipe";
                }
                if (IsAtlasResource(relativePath))
                {
                    return "atlasMetadata";
                }
                return "gameAsset";
            }
            if (extension == ".png")
            {
                if (relativePath.Contains("/depth", StringComparison.OrdinalIgnoreCase))
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
            return relativePath.StartsWith("atlas/", StringComparison.OrdinalIgnoreCase)
                   || relativePath.StartsWith("render_atlas/", StringComparison.OrdinalIgnoreCase)
                   || relativePath.Contains("/atlas/", StringComparison.OrdinalIgnoreCase)
                   || relativePath.Contains("/render_atlas/", StringComparison.OrdinalIgnoreCase);
        }

        private static string CalculateFileHash(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();
            return ToLowerHex(sha.ComputeHash(stream));
        }

        private static string ToLowerHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var i = 0; i < bytes.Length; i++)
            {
                builder.Append(bytes[i].ToString("x2"));
            }
            return builder.ToString();
        }

        private static void RequireCanonicalToken(string value, string field)
        {
            if (string.IsNullOrWhiteSpace(value)
                || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
                || value.IndexOf('\r') >= 0
                || value.IndexOf('\n') >= 0
                || value.IndexOf('|') >= 0)
            {
                throw new InvalidDataException($"GameAsset package {field} is empty or not canonical.");
            }
        }

        private static void RequireLowerHex(string value, int length, string field)
        {
            if (value == null || value.Length != length)
            {
                throw new InvalidDataException(
                    $"GameAsset package {field} must contain {length} lowercase hex characters.");
            }
            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
                {
                    throw new InvalidDataException(
                        $"GameAsset package {field} must contain {length} lowercase hex characters.");
                }
            }
        }

        private static string NormalizeNewlines(string value)
        {
            return value.Replace("\r\n", "\n").Replace('\r', '\n');
        }
    }
}
