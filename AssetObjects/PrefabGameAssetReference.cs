using System;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
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

        public bool TryResolveAuthoredKey(out GameAssetReference reference)
        {
            return PrefabGameAssetDefinitions.TryGetAuthoredReference(_authoring, out reference);
        }
    }
}
