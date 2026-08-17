using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Modding
{
    /// <summary>
    /// One module this module is allowed to reach into, pinned by the asset
    /// content hash the dependency published in its manifest.
    /// </summary>
    [Serializable, Preserve]
    public class ModDependency
    {
        public string Mod;
        public string ContentHash;
        public int ManifestVersion;
    }

    /// <summary>
    /// Declares which other modules a module may reach into. A prefab base or
    /// any other cross-module reference must resolve inside the module itself or
    /// one of the modules listed here, so the coupling between modules is stated
    /// in the content instead of being discovered at load time.
    ///
    /// The owning module is not named here: the file lives in its folder. The
    /// file itself is optional, and a module without it depends on nothing.
    /// </summary>
    [Serializable, Preserve]
    public class ModDependencies
    {
        public const string FILE_NAME = "dependency.json";

        public List<ModDependency> DependsOn = new();
    }
}
