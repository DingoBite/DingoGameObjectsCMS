using System;
using System.Collections.Generic;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    [DisallowMultipleComponent, AddComponentMenu("Dingo CMS/Game Asset Authoring")]
    public class GameAssetAuthoring : MonoBehaviour
    {
        [SerializeReference, SubclassSelector] private List<GameAssetComponent> _components = new();
        [SerializeField] private List<GameAssetAuthoringConverterBinding> _converters = new();
        [SerializeField, HideInInspector] private string _sourceDefinitionGuid;
        [SerializeField, HideInInspector] private string _runtimeOverrideId;

        public IReadOnlyList<GameAssetComponent> Components => _components;
        public IReadOnlyList<GameAssetAuthoringConverterBinding> Converters => _converters;
        public string SourceDefinitionGuid => _sourceDefinitionGuid;
        public string RuntimeOverrideId => _runtimeOverrideId;

#if UNITY_EDITOR
        public static event Action<GameAssetAuthoring> ValidationRequested;

        private void OnValidate() => ValidationRequested?.Invoke(this);
#endif

        public bool SetRuntimeDefinitionSource(string guid, string overrideId = null)
        {
            if (_sourceDefinitionGuid == guid && _runtimeOverrideId == overrideId)
                return false;
            _sourceDefinitionGuid = guid;
            _runtimeOverrideId = overrideId;
            return true;
        }

        public void SetComponents(IEnumerable<GameAssetComponent> components)
        {
            _components = components != null ? new List<GameAssetComponent>(components) : new List<GameAssetComponent>();
        }

        public void SetConverters(IEnumerable<GameAssetAuthoringConverterBinding> converters)
        {
            _converters = converters != null ? new List<GameAssetAuthoringConverterBinding>(converters) : new List<GameAssetAuthoringConverterBinding>();
        }
    }
}
