using System;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects
{
    [Serializable, Preserve]
    public struct PrefabGameAssetReference
    {
        [SerializeField] private GameAssetAuthoring _authoring;

        public GameAssetAuthoring Authoring => _authoring;

        public PrefabGameAssetReference(GameAssetAuthoring authoring)
        {
            _authoring = authoring;
        }
    }
}
