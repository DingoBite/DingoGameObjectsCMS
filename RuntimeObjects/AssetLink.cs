using Unity.Entities;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    public struct AssetLink : IComponentData
    {
        public Hash128 AssetGUID;
    }
    
    public struct SourceAssetLink : IComponentData
    {
        public Hash128 AssetGUID;
    }

    public struct AssetPresentationTag : IComponentData
    {
    }
}