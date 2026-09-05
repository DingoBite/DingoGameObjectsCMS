using System;
using System.IO;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public readonly struct GameAssetPackageProvisioningResult
    {
        public readonly string SourceModuleRoot;
        public readonly string InstalledModuleRoot;
        public readonly string ContentHash;
        public readonly bool Changed;

        public GameAssetPackageProvisioningResult(
            string sourceModuleRoot,
            string installedModuleRoot,
            string contentHash,
            bool changed)
        {
            SourceModuleRoot = sourceModuleRoot;
            InstalledModuleRoot = installedModuleRoot;
            ContentHash = contentHash;
            Changed = changed;
        }
    }

    public static class GameAssetPackageProvisioner
    {
        public static GameAssetPackageProvisioningResult ProvisionModule(
            string sourceModuleRoot,
            string destinationAssetsRoot,
            string moduleId)
        {
            var sourceRoot = Path.GetFullPath(
                sourceModuleRoot
                ?? throw new ArgumentNullException(
                    nameof(sourceModuleRoot)));
            var canonicalModuleId = GameAssetModuleContentScanner
                .RequireCanonicalModuleId(moduleId);
            var source = GameAssetModuleContentScanner.Scan(
                sourceRoot,
                canonicalModuleId);
            var assetsRoot = GameAssetModPathPolicy.GetAssetsRootPath(
                destinationAssetsRoot);
            Directory.CreateDirectory(assetsRoot);
            var installedRoot = GameAssetModPathPolicy.GetModRootPath(
                canonicalModuleId,
                canonicalModuleId,
                assetsRoot);

            if (PathsEqual(sourceRoot, installedRoot))
            {
                return new GameAssetPackageProvisioningResult(
                    sourceRoot,
                    installedRoot,
                    source.ContentHash,
                    changed: false);
            }

            if (TryGetInstalledHash(
                    installedRoot,
                    canonicalModuleId,
                    out var installedHash)
                && string.Equals(
                    installedHash,
                    source.ContentHash,
                    StringComparison.Ordinal))
            {
                return new GameAssetPackageProvisioningResult(
                    sourceRoot,
                    installedRoot,
                    source.ContentHash,
                    changed: false);
            }

            var operationId = Guid.NewGuid().ToString("N");
            var stagingRoot = Path.Combine(
                assetsRoot,
                $".{canonicalModuleId}.provisioning-{operationId}");
            var backupRoot = Path.Combine(
                assetsRoot,
                $".{canonicalModuleId}.backup-{operationId}");
            var movedExistingInstallation = false;
            try
            {
                CopyModule(sourceRoot, stagingRoot);
                var staged = GameAssetModuleContentScanner.Scan(
                    stagingRoot,
                    canonicalModuleId);
                if (!string.Equals(
                        staged.ContentHash,
                        source.ContentHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Provisioned GameAsset module '{canonicalModuleId}' "
                        + "does not match its packaged source.");
                }

                if (Directory.Exists(installedRoot))
                {
                    Directory.Move(installedRoot, backupRoot);
                    movedExistingInstallation = true;
                }

                Directory.Move(stagingRoot, installedRoot);
                var installed = GameAssetModuleContentScanner.Scan(
                    installedRoot,
                    canonicalModuleId);
                if (!string.Equals(
                        installed.ContentHash,
                        source.ContentHash,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Installed GameAsset module '{canonicalModuleId}' "
                        + "does not match its packaged source.");
                }

                if (Directory.Exists(backupRoot))
                {
                    Directory.Delete(backupRoot, recursive: true);
                }

                return new GameAssetPackageProvisioningResult(
                    sourceRoot,
                    installedRoot,
                    source.ContentHash,
                    changed: true);
            }
            catch
            {
                if (movedExistingInstallation
                    && Directory.Exists(backupRoot))
                {
                    if (Directory.Exists(installedRoot))
                    {
                        Directory.Delete(installedRoot, recursive: true);
                    }
                    Directory.Move(backupRoot, installedRoot);
                }
                throw;
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }
        }

        private static bool TryGetInstalledHash(
            string moduleRoot,
            string moduleId,
            out string contentHash)
        {
            contentHash = null;
            if (!Directory.Exists(moduleRoot))
            {
                return false;
            }

            try
            {
                contentHash = GameAssetModuleContentScanner
                    .Scan(moduleRoot, moduleId)
                    .ContentHash;
                return true;
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static void CopyModule(string sourceRoot, string targetRoot)
        {
            Directory.CreateDirectory(targetRoot);
            var directories = Directory.GetDirectories(
                sourceRoot,
                "*",
                SearchOption.AllDirectories);
            Array.Sort(directories, StringComparer.Ordinal);
            for (var index = 0; index < directories.Length; index++)
            {
                var relative = Path.GetRelativePath(
                    sourceRoot,
                    directories[index]);
                Directory.CreateDirectory(Path.Combine(targetRoot, relative));
            }

            var files = Directory.GetFiles(
                sourceRoot,
                "*",
                SearchOption.AllDirectories);
            Array.Sort(files, StringComparer.Ordinal);
            for (var index = 0; index < files.Length; index++)
            {
                if (string.Equals(
                        Path.GetExtension(files[index]),
                        ".meta",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(
                    sourceRoot,
                    files[index]);
                var target = Path.Combine(targetRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(files[index], target, overwrite: false);
            }
        }

        private static bool PathsEqual(string left, string right)
        {
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                comparison);
        }
    }
}
