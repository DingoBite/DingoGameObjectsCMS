using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using Newtonsoft.Json.Linq;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetObjects
{
    /// <summary>
    /// A sparse override of a GameAsset document, without saying what it
    /// overrides. A named derived asset pairs it with a base key in
    /// <see cref="GameAssetPrefab"/>; a placement pairs it with the asset the
    /// placement already references.
    ///
    /// Fields are addressed by a path rooted at the component list, where a
    /// segment is a component <c>$type</c> alias and <c>..</c> walks one level
    /// up: <c>/SimpleSpriteLink_GAC/Visual/Resource/RelativePath</c>,
    /// <c>/../Surfaces/0/Width</c>. Operations apply in declaration order:
    /// removed components, overridden components, removed fields, overridden
    /// fields.
    /// </summary>
    [Serializable, Preserve]
    public class GameAssetOverrides
    {
        /// <summary>
        /// Component <c>$type</c> aliases dropped from the base.
        /// </summary>
        public List<string> RemovedComponents;

        /// <summary>
        /// Whole component documents, each carrying its <c>$type</c>. A component
        /// the base already provides is replaced; otherwise it is added.
        /// </summary>
        public List<JObject> OverrideComponents;

        /// <summary>
        /// Document paths dropped from the base, so the field falls back to its
        /// declared default.
        /// </summary>
        public List<string> RemovedFields;

        /// <summary>
        /// Document path to replacement value. A value is replaced as a whole;
        /// there is no deep merge, which keeps a collection override atomic
        /// exactly like the runtime patch lane. Paths are applied in ordinal
        /// order so the result never depends on document member order.
        /// </summary>
        public Dictionary<string, JToken> OverrideFields;

        public bool HasAny =>
            RemovedComponents is { Count: > 0 } || OverrideComponents is { Count: > 0 }
            || RemovedFields is { Count: > 0 } || OverrideFields is { Count: > 0 };
    }

    /// <summary>
    /// Declares this GameAsset as a sparse override of another exact GameAsset
    /// instead of a hand-duplicated copy of it.
    ///
    /// Composition runs on the authored JSON document before it is deserialized,
    /// so a derived asset reaches materialization, the immutable library lock and
    /// the wire as an ordinary complete GameAsset.
    /// </summary>
    [Serializable, Preserve]
    public class GameAssetPrefab : GameAssetOverrides
    {
        /// <summary>
        /// Exact base key. A <c>latest</c> request is rejected: the effective
        /// content of a derived asset must not drift when a new version of the
        /// base appears.
        /// </summary>
        public GameAssetKey Base;

        /// <summary>
        /// True when a base is declared at all. Deliberately permissive so that
        /// a partially filled or unpinned base reaches validation instead of
        /// silently skipping composition.
        /// </summary>
        public bool HasBase =>
            !string.IsNullOrWhiteSpace(Base.Mod) || !string.IsNullOrWhiteSpace(Base.Type)
            || !string.IsNullOrWhiteSpace(Base.Key) || !string.IsNullOrWhiteSpace(Base.Version);
    }
}
