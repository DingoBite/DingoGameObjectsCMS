using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using UnityEngine;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Modding
{
    [Serializable, Preserve]
    public sealed class ModManifest
    {
        public const string FILE_NAME = "manifest.json";

        public string Mod;
        public string GeneratedUtc;
        public int ManifestVersion = 1;

        /// <summary>
        /// Hash over this module's assets — key, GUID, path and document bytes.
        /// Deliberately not the whole-module file hash: this value lives inside
        /// manifest.json and a file hash would include itself. A dependent
        /// module pins this value in its dependency.json.
        /// </summary>
        public string ContentHash;

        public List<ModManifestEntry> Assets = new();
    }

    [Serializable, Preserve]
    public sealed class ModManifestEntry
    {
        public GameAssetKey Key;
        public Hash128 GUID;
        public string RelativeJsonPath;
    }
}
