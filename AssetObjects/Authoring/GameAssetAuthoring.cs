using System.Collections.Generic;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    [DisallowMultipleComponent, AddComponentMenu("Dingo CMS/Game Asset Authoring")]
    public class GameAssetAuthoring : MonoBehaviour
    {
        [SerializeReference, SubclassSelector] private List<GameAssetComponent> _components = new();
        [SerializeField] private List<GameAssetAuthoringConverterBinding> _converters = new();

        public IReadOnlyList<GameAssetComponent> Components => _components;
        public IReadOnlyList<GameAssetAuthoringConverterBinding> Converters => _converters;

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
