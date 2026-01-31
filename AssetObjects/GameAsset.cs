using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects
{
    [CreateAssetMenu(menuName = MENU_PREFIX + "GameAsset")]
    public class GameAsset : GameAssetScriptableObject
    {
        [SerializeReference, SubclassSelector] private List<GameAssetComponent> _components;
        
        [Header("Keep value by default if there is no SourceAsset")]
        [SerializeField, Tooltip("If this GameAsset is representation of other GameAsset")] 
        private Hash128 _sourceAssetGUID;

        public IReadOnlyList<GameAssetComponent> Components => _components;
        
#if UNITY_EDITOR
        public List<GameAssetComponent> Components_Editor { get => _components; set => _components = value; }
#endif
        
        public virtual void SetupRuntimeObject(GameRuntimeObject g)
        {
            g.Key = Key;
            g.AssetGUID = GUID;
            g.SourceAssetGUID = _sourceAssetGUID;
            if (_components == null)
                return;

            foreach (var mobAssetComponent in _components)
            {
                mobAssetComponent.SetupRuntimeComponent(g);
            }
        }
    }
}