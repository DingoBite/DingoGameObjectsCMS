#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class GameAssetModPathPolicy
    {
        public const string DEFAULT_ASSETS_ROOT_SUB_PATH = "assets";

        public static string GetAssetsRootPath(string assetsRootAbs = null, string assetsRootSubPath = DEFAULT_ASSETS_ROOT_SUB_PATH)
        {
            return string.IsNullOrWhiteSpace(assetsRootAbs)
                ? Path.GetFullPath(Path.Combine(Application.persistentDataPath, assetsRootSubPath))
                : Path.GetFullPath(assetsRootAbs);
        }

        public static string GetModRootPath(
            string mod,
            string fallbackMod,
            string assetsRootAbs = null,
            string assetsRootSubPath = DEFAULT_ASSETS_ROOT_SUB_PATH)
        {
            var root = GetAssetsRootPath(assetsRootAbs, assetsRootSubPath);
            var normalizedMod = GameAssetKeyPolicy.NormalizeModName(mod, fallbackMod);
            normalizedMod = GameAssetModuleContentScanner
                .RequireCanonicalModuleId(normalizedMod);
            var resolved = Path.GetFullPath(Path.Combine(root, normalizedMod));
            var rootPrefix = root.TrimEnd(
                                 Path.DirectorySeparatorChar,
                                 Path.AltDirectorySeparatorChar)
                             + Path.DirectorySeparatorChar;
            var comparison = Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!resolved.StartsWith(rootPrefix, comparison))
            {
                throw new InvalidDataException(
                    $"GameAsset module '{normalizedMod}' escapes assets root '{root}'.");
            }
            return resolved;
        }
    }
}
#endif
