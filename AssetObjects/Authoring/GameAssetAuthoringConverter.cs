using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    [Serializable, Preserve, HideInTypeMenu]
    public abstract class GameAssetAuthoringConverter
    {
        public abstract Type SourceComponentType { get; }

        // Editor composition prepares derived runtime resource identity; invalidate unusable metadata before a preparation failure.
        public virtual bool Prepare(Component source, Func<UnityEngine.Object, string> resourceGuid) => false;

        public abstract IReadOnlyList<GameAssetComponent> Convert(Component source);
    }

    [Serializable, Preserve]
    public class GameAssetAuthoringConverterBinding
    {
        [SerializeField] private Component _source;
        [SerializeReference, SubclassSelector] private GameAssetAuthoringConverter _converter;

        public Component Source => _source;
        public GameAssetAuthoringConverter Converter => _converter;

        public GameAssetAuthoringConverterBinding(Component source, GameAssetAuthoringConverter converter)
        {
            _source = source;
            _converter = converter;
        }
    }
}
