#if NEWTONSOFT_EXISTS
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Serialization;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class GameAssetCloneExtensions
    {
        public static GameAsset CloneAsset(this GameAsset source)
        {
            return source == null ? null : source.ToJson().FromJson<GameAsset>();
        }
    }
}
#endif
