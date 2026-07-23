using System;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    /// <summary>
    /// A domain-owned visual choice. The physical sprite, preset tag and
    /// optional sparse exception travel together on the owning GameAsset.
    /// </summary>
    [Serializable, Preserve]
    public struct SpriteVisualRef : IEquatable<SpriteVisualRef>
    {
        public GameAssetResourceRef Resource;
        public string PresetId;
        public GameAssetResourceRef RecipeResource;

        public bool IsDefined => Resource.IsDefined;

        public SpriteVisualRef(
            GameAssetResourceRef resource,
            string presetId = null,
            GameAssetResourceRef recipeResource = default)
        {
            Resource = resource;
            PresetId = presetId;
            RecipeResource = recipeResource;
        }

        public bool Equals(SpriteVisualRef other)
        {
            return Resource.Equals(other.Resource)
                   && string.Equals(PresetId, other.PresetId, StringComparison.Ordinal)
                   && RecipeResource.Equals(other.RecipeResource);
        }

        public override bool Equals(object obj)
        {
            return obj is SpriteVisualRef other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Resource, PresetId, RecipeResource);
        }

        public override string ToString()
        {
            var preset = string.IsNullOrWhiteSpace(PresetId) ? "<default>" : PresetId;
            var recipe = RecipeResource.IsDefined ? RecipeResource.ToString() : "<none>";
            return $"{Resource} preset={preset} recipe={recipe}";
        }
    }
}
