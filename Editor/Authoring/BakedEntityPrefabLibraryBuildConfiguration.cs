using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Scenes.Editor;
using UnityEditor;
using UnityEditor.Build;
using EntityHash128 = Unity.Entities.Hash128;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    public static class BakedEntityPrefabLibraryBuildConfiguration
    {
        private static IReadOnlyList<string> _scenePaths = Array.Empty<string>();

        public static IReadOnlyList<string> ScenePaths => _scenePaths;

        public static void Configure(IEnumerable<string> libraryScenePaths)
        {
            _scenePaths = Array.AsReadOnly((libraryScenePaths ?? Array.Empty<string>()).Select(path => path?.Replace('\\', '/')).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        }

        public static HashSet<EntityHash128> CollectSceneGuids()
        {
            var guids = new HashSet<EntityHash128>();
            foreach (var path in _scenePaths)
            {
                if (!path.StartsWith("Assets/", StringComparison.Ordinal) || AssetDatabase.LoadAssetAtPath<SceneAsset>(path) == null)
                    throw new BuildFailedException($"Baked Entity Prefab library scene '{path}' must be a saved Unity scene inside Assets.");
                guids.Add(new EntityHash128(AssetDatabase.AssetPathToGUID(path)));
            }
            return guids;
        }
    }

    public class BakedEntityPrefabLibraryBuildAdditions : IEntitySceneBuildAdditions
    {
        public HashSet<EntityHash128> RegisterAdditionalEntityScenesToBuild()
        {
            PrefabGameAssetIndexEditorUtility.Reconcile();
            return BakedEntityPrefabLibraryBuildConfiguration.CollectSceneGuids();
        }
    }
}
