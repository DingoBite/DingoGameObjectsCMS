using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Modding
{
    [Serializable, Preserve]
    public class ModDependency
    {
        public string Mod;
        public string ContentHash;
        public int ManifestVersion;
    }

    [Serializable, Preserve]
    public class ModDependencies
    {
        public const string FILE_NAME = "dependency.json";

        public List<ModDependency> DependsOn = new();
    }
}
