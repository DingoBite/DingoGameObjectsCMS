#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.RuntimeObjects;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class GameAssetPathPolicy
    {
        private const string ASSET_JSON_FOLDER = "assets";

        private static StringComparison FileSystemPathComparison =>
            Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        public static string BuildDefaultRelativeJsonPath(GameAssetKey key)
        {
            return NormalizeSlashes(Path.Combine(ASSET_JSON_FOLDER, key.Type, key.Key, $"{key.Key}@{key.Version}.json"));
        }

        public static string CombineAbsolute(string rootAbs, string relativePath)
        {
            var root = Path.GetFullPath(rootAbs ?? throw new ArgumentNullException(nameof(rootAbs)))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var resolved = GameAssetModulePackageFileUtils.ResolveInsideRoot(root, relativePath);
            EnsureNoReparsePoints(root, resolved);
            return resolved;
        }

        public static string NormalizeSlashes(string path)
        {
            return (path ?? string.Empty).Replace('\\', '/');
        }

        private static void EnsureNoReparsePoints(string root, string absolutePath)
        {
            var current = Path.GetFullPath(absolutePath);
            while (!string.Equals(current, root, FileSystemPathComparison))
            {
                if ((File.Exists(current) || Directory.Exists(current))
                    && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        $"GameAsset mod path '{current}' uses a reparse point and is not allowed.");
                }

                current = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(current))
                    throw new InvalidDataException($"GameAsset mod path '{absolutePath}' is outside '{root}'.");
            }
        }
    }
}
#endif
