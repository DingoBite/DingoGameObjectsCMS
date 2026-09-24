using System;
using DingoGameObjectsCMS.RuntimeObjects;
using NaughtyAttributes;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    [DisallowMultipleComponent]
    public class GameAssetObjectAuthoring : MonoBehaviour
    {
        [SerializeField] private GameAssetKey _key;
        [SerializeField] private string _relativeJsonPath;
        [SerializeField] private string _assetGuid;
        [SerializeField] private string _authoringSourceId;
        [SerializeField] private string _documentSha256;
        [SerializeField] private bool _autoBake = true;

        public GameAssetKey Key => _key;
        public string RelativeJsonPath => _relativeJsonPath;
        public string AssetGuid => _assetGuid;
        public string AuthoringSourceId => _authoringSourceId;
        public string DocumentSha256 => _documentSha256;
        public bool AutoBake => _autoBake;
        public bool HasAsset => !string.IsNullOrWhiteSpace(_assetGuid);

#if UNITY_EDITOR
        public static event Action<GameAssetObjectAuthoring> Validated;
        public static event Action<GameAssetObjectAuthoring> ResetRequested;
        public static event Action<GameAssetObjectAuthoring> RefreshRequested;
        public static event Action<GameAssetObjectAuthoring> DeleteRequested;

        [Button("Reset", EButtonEnableMode.Editor)]
        public void ResetAssetLink()
        {
            ResetRequested?.Invoke(this);
        }

        [Button("Refresh", EButtonEnableMode.Editor)]
        public void RefreshGameAsset()
        {
            RefreshRequested?.Invoke(this);
        }

        [Button("Delete", EButtonEnableMode.Editor)]
        public void DeleteGameAsset()
        {
            DeleteRequested?.Invoke(this);
        }

        private void OnValidate()
        {
            Validated?.Invoke(this);
        }
#endif
    }
}
