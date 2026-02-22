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
        public string Mod;
        public string GeneratedUtc;
        public int ManifestVersion = 1;

        public List<ModManifestEntry> Assets = new();
    }

    [Serializable, Preserve]
    public sealed class ModManifestEntry
    {
        public GameAssetKey Key;
        public Hash128 GUID;
        public string RelativeJsonPath;

        public string SoType;
    }
}