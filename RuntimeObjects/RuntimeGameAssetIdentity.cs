using System;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;
using Unity.Entities;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.RuntimeObjects
{
    /// <summary>
    /// Reconstructable session-local asset identity projected only onto the
    /// ECS root of a locked GA-backed GRO. Factory products reach it through
    /// RuntimeEntityFactoryOwner instead of copying it into every product.
    /// </summary>
    [RuntimeDotsDerived]
    [Serializable, Preserve]
    public struct RuntimeGameAssetIdentity :
        IComponentData,
        IEquatable<RuntimeGameAssetIdentity>
    {
        public GameAssetIndex AssetIndex;
        public GameAssetIdentityIndex IdentityIndex;

        public bool IsValid => AssetIndex.IsValid && IdentityIndex.IsValid;

        public bool Equals(RuntimeGameAssetIdentity other) =>
            AssetIndex.Equals(other.AssetIndex)
            && IdentityIndex.Equals(other.IdentityIndex);

        public override bool Equals(object value) =>
            value is RuntimeGameAssetIdentity other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(AssetIndex, IdentityIndex);
    }
}
