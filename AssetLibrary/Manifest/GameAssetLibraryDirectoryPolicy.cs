using System;
using System.Collections.Generic;
using System.IO;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public static class GameAssetLibraryDirectoryPolicy
    {
        public static bool IsIgnoredLibraryDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException(
                    "Library directory path is required.",
                    nameof(directoryPath));
            }

            var name = Path.GetFileName(directoryPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            return name.StartsWith(".", StringComparison.Ordinal);
        }

        public static string[] EnumerateModuleDirectories(string assetsRoot)
        {
            if (string.IsNullOrWhiteSpace(assetsRoot))
            {
                throw new ArgumentException(
                    "Assets root is required.",
                    nameof(assetsRoot));
            }

            var directories = Directory.GetDirectories(assetsRoot);
            var result = new List<string>(directories.Length);
            for (var index = 0; index < directories.Length; index++)
            {
                if (!IsIgnoredLibraryDirectory(directories[index]))
                {
                    result.Add(directories[index]);
                }
            }
            return result.ToArray();
        }
    }
}
