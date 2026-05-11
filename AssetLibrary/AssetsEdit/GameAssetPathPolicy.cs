#if NEWTONSOFT_EXISTS
using System.IO;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoUnityExtensions.Utils;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class GameAssetPathPolicy
    {
        private const string ASSET_JSON_FOLDER = "assets";

        public static string BuildDefaultRelativeJsonPath(GameAssetKey key)
        {
            return NormalizeSlashes(Path.Combine(ASSET_JSON_FOLDER, key.Type, key.Key, $"{key.Key}@{key.Version}.json"));
        }

        public static string CombineAbsolute(string rootAbs, string relativePath)
        {
            return Path.GetFullPath(Path.Combine(rootAbs, (relativePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
        }

        public static string NormalizeSlashes(string path)
        {
            return path.NormalizePath().TrimStart('/');
        }
    }
}
#endif