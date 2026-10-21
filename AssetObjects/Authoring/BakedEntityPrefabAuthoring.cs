using System;
using System.Collections.Generic;
using Unity.Entities.Serialization;
using UnityEngine;
using UnityEngine.Scripting;
using EntityHash128 = Unity.Entities.Hash128;

namespace DingoGameObjectsCMS.AssetObjects.Authoring
{
    [DisallowMultipleComponent, RequireComponent(typeof(GameAssetAuthoring)), AddComponentMenu("Dingo CMS/Baked Entity Prefab")]
    public class BakedEntityPrefabAuthoring : MonoBehaviour
    {
    }

    [Serializable, Preserve]
    public class BakedEntityPrefabConverter : GameAssetAuthoringConverter
    {
        [SerializeField, HideInInspector] private EntityPrefabReference _prefab;

        public override Type SourceComponentType => typeof(BakedEntityPrefabAuthoring);

        public override bool Prepare(Component source, Func<UnityEngine.Object, string> resourceGuid)
        {
            var previous = _prefab;
            _prefab = default;
            var guid = resourceGuid(source.gameObject);
            if (Guid.TryParseExact(guid, "N", out _))
                _prefab = new EntityPrefabReference(new EntityHash128(guid));
            return previous != _prefab;
        }

        public override IReadOnlyList<GameAssetComponent> Convert(Component source)
        {
            return new GameAssetComponent[] { new BakedEntityPrefab_GAC { Prefab = _prefab } };
        }
    }
}
