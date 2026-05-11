#if NEWTONSOFT_EXISTS
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.RuntimeObjects;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public interface IGameAssetEditService
    {
        GameAssetEditProfile Profile { get; }

        IReadOnlyList<GameAsset> ListEditableAssets(bool includeExternal = true);
        GameAsset CreateDefaultAsset();
        Task<GameAssetSaveResult> SaveNewAsync(GameAsset asset, CancellationToken ct = default);
        Task<GameAssetSaveResult> SaveNewVersionAsync(GameAsset asset, GameAssetKey previousKey, UnityEngine.Hash128 previousGuid, CancellationToken ct = default);
        Task<bool> DeleteAsync(GameAssetKey key, CancellationToken ct = default);
        GameAssetEditValidationReport Validate(GameAsset asset);
        string GetAssetsRootPath();
        string GetModRootPath(string mod);
        bool IsEditableKey(GameAssetKey key);
    }
}
#endif
