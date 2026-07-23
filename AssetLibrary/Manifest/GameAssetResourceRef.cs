using System;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    [Serializable, Preserve]
    public struct GameAssetResourceRef : IEquatable<GameAssetResourceRef>
    {
        public string ModuleId;
        public string RelativePath;

        public bool IsDefined => !string.IsNullOrWhiteSpace(ModuleId) && !string.IsNullOrWhiteSpace(RelativePath);

        public GameAssetResourceRef(string moduleId, string relativePath)
        {
            ModuleId = moduleId;
            RelativePath = GameAssetModulePackageFileUtils.RequireCanonicalRelativePath(relativePath);
        }

        public bool Equals(GameAssetResourceRef other)
        {
            return string.Equals(ModuleId, other.ModuleId, StringComparison.Ordinal)
                   && string.Equals(RelativePath, other.RelativePath, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is GameAssetResourceRef other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(ModuleId, RelativePath);
        }

        public override string ToString()
        {
            return $"{ModuleId}:{RelativePath}";
        }
    }
}
