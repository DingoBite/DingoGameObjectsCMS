using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    public static class PrefabGameAssetOverrideEditorUtility
    {
        private static bool _savingMetadata;

        public static IReadOnlyList<PrefabGameAssetOverrideEntry> CollectOverrides(IReadOnlyList<PrefabGameAssetIndexEntry> entries, PrefabGameAssetContentHashCache hashCache = null)
        {
            var result = new List<PrefabGameAssetOverrideEntry>();
            hashCache ??= new PrefabGameAssetContentHashCache();
            var paths = CollectHierarchyAssetPaths(entries);
            foreach (var path in paths.Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab != null && BakeHierarchy(prefab, path))
                    PrefabUtility.SavePrefabAsset(prefab);
            }
            hashCache.Clear();
            foreach (var path in paths)
            {
                if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                        CollectHierarchy(prefab, path, result, false, hashCache);
                    continue;
                }
                var scene = default(Scene);
                try
                {
                    scene = EditorSceneManager.OpenPreviewScene(path);
                    foreach (var root in scene.GetRootGameObjects())
                    {
                        CollectHierarchy(root, path, result, false, hashCache);
                    }
                }
                finally
                {
                    if (scene.IsValid())
                        EditorSceneManager.ClosePreviewScene(scene);
                }
            }
            return result;
        }

        public static IReadOnlyList<string> CollectHierarchyAssetPaths(IReadOnlyList<PrefabGameAssetIndexEntry> entries)
        {
            var script = entries.Count > 0 ? MonoScript.FromMonoBehaviour(entries[0].Authoring) : MonoImporter.GetAllRuntimeMonoScripts().First(candidate => candidate.GetClass() == typeof(GameAssetAuthoring));
            var scriptPath = AssetDatabase.GetAssetPath(script);
            var candidates = AssetDatabase.FindAssets("t:Prefab t:Scene", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Where(PrefabGameAssetIndexConfiguration.Includes).Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            var relevant = new HashSet<string>(entries.Select(entry => AssetDatabase.GetAssetPath(entry.Authoring)), StringComparer.Ordinal);
            var relevantPending = new Queue<string>(relevant);
            var pending = new Queue<string>(candidates);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var parents = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            while (pending.Count > 0)
            {
                var path = pending.Dequeue();
                if (!visited.Add(path))
                    continue;
                foreach (var dependency in AssetDatabase.GetDependencies(path, false))
                {
                    if (string.Equals(dependency, scriptPath, StringComparison.Ordinal) && relevant.Add(path))
                        relevantPending.Enqueue(path);
                    if (string.Equals(dependency, path, StringComparison.Ordinal) || !dependency.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!parents.TryGetValue(dependency, out var dependencyParents))
                    {
                        dependencyParents = new HashSet<string>(StringComparer.Ordinal);
                        parents.Add(dependency, dependencyParents);
                    }
                    dependencyParents.Add(path);
                    if (!visited.Contains(dependency))
                        pending.Enqueue(dependency);
                }
            }
            while (relevantPending.Count > 0)
            {
                var path = relevantPending.Dequeue();
                if (!parents.TryGetValue(path, out var dependencyParents))
                    continue;
                foreach (var parent in dependencyParents)
                {
                    if (relevant.Add(parent))
                        relevantPending.Enqueue(parent);
                }
            }
            return candidates.Where(relevant.Contains).ToArray();
        }

        public static bool ApplyIndexedSceneMetadata(Scene scene, PrefabGameAssetIndex index)
        {
            if (index == null)
                throw new ArgumentNullException(nameof(index));
            var records = index.Overrides.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
            var changed = false;
            foreach (var root in scene.GetRootGameObjects())
            {
                foreach (var authoring in root.GetComponentsInChildren<GameAssetAuthoring>(true))
                {
                    if (!GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out var source, out var sourceGuid, out _))
                    {
                        changed |= authoring.SetRuntimeDefinitionSource(null);
                        continue;
                    }
                    var hasOverride = TryGetRecordId(authoring, scene.path, out var recordId) && records.TryGetValue(recordId, out var record) && record.Source == source;
                    changed |= authoring.SetRuntimeDefinitionSource(sourceGuid, hasOverride ? recordId : null);
                }
            }
            return changed;
        }

        public static bool TryCreateOverride(GameAssetAuthoring authoring, string assetPath, out PrefabGameAssetOverrideEntry entry, out string sourceGuid, out string error, PrefabGameAssetContentHashCache hashCache = null)
        {
            return TryCreateOverride(authoring, assetPath, out entry, out sourceGuid, out error, hashCache, true);
        }

        private static bool TryCreateOverride(GameAssetAuthoring authoring, string assetPath, out PrefabGameAssetOverrideEntry entry, out string sourceGuid, out string error, PrefabGameAssetContentHashCache hashCache, bool collectHash)
        {
            entry = null;
            sourceGuid = null;
            if (!GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out var source, out sourceGuid, out var isDefinition, out error))
                return false;
            if (isDefinition)
                return true;
            if (!GameAssetAuthoringEditorUtility.TryCollect(authoring, out var effective, out error))
                return false;
            if (!GameAssetAuthoringCollector.TryCollect(source, out var original, out error))
                return false;
            try
            {
                var serializer = JsonSerializer.Create(GameAssetJson.Settings);
                serializer.Converters.Insert(0, new PrefabGameAssetUnityIdentityJsonConverter());
                var originalByType = original.Components.ToDictionary(component => component.GetType(), component => SerializeComponent(component, serializer));
                var changed = new List<JObject>();
                foreach (var component in effective.Components)
                {
                    var value = SerializeComponent(component, serializer);
                    if (!JToken.DeepEquals(value, originalByType[component.GetType()]))
                        changed.Add(value);
                }
                if (changed.Count == 0)
                    return true;
                if (!TryGetRecordId(authoring, assetPath, out var recordId))
                {
                    error = "Save the prefab or scene before preparing its instance override identity.";
                    return false;
                }
                var overridesJson = GameAssetDocumentComposer.CanonicalizeOverrides(new GameAssetOverrides { OverrideComponents = changed });
                entry = new PrefabGameAssetOverrideEntry(recordId, source, CloneComponents(effective.Components), overridesJson, collectHash ? PrefabGameAssetContentHashUtility.Compute(assetPath, effective.Components, hashCache) : null);
                return true;
            }
            catch (Exception exception)
            {
                error = "Prefab instance values could not be frozen: " + exception.Message;
                return false;
            }
        }

        public static bool BakeHierarchy(GameObject root, string path)
        {
            return CollectHierarchy(root, path, null, true);
        }

        public static void BakeSavedAssets(IEnumerable<string> paths)
        {
            if (_savingMetadata)
                return;
            _savingMetadata = true;
            try
            {
                var sourcePaths = paths.Where(PrefabGameAssetIndexConfiguration.Includes).Distinct(StringComparer.Ordinal).ToArray();
                foreach (var path in sourcePaths.Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null && prefab.TryGetComponent<GameAssetAuthoring>(out var authoring) && authoring.SetRuntimeDefinitionSource(AssetDatabase.AssetPathToGUID(path)))
                    {
                        EditorUtility.SetDirty(authoring);
                        PrefabUtility.SavePrefabAsset(prefab);
                    }
                }
                foreach (var path in sourcePaths)
                {
                    if (path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                        if (prefab != null && BakeHierarchy(prefab, path))
                            PrefabUtility.SavePrefabAsset(prefab);
                    }
                    else if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        var scene = SceneManager.GetSceneByPath(path);
                        if (!scene.IsValid() || !scene.isLoaded)
                            continue;
                        foreach (var root in scene.GetRootGameObjects())
                        {
                            BakeHierarchy(root, path);
                        }
                    }
                }
            }
            finally
            {
                _savingMetadata = false;
            }
        }

        private static bool CollectHierarchy(GameObject root, string path, ICollection<PrefabGameAssetOverrideEntry> entries, bool bake, PrefabGameAssetContentHashCache hashCache = null)
        {
            var changed = false;
            foreach (var authoring in root.GetComponentsInChildren<GameAssetAuthoring>(true))
            {
                if (!TryCreateOverride(authoring, path, out var entry, out var sourceGuid, out _, hashCache, entries != null))
                {
                    // Validation reports the rejected content once. Do not retain an older valid override's identity.
                    if (!GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out _, out sourceGuid, out _))
                        sourceGuid = null;
                    entry = null;
                }
                if (entry != null)
                    entries?.Add(entry);
                if (bake && authoring.SetRuntimeDefinitionSource(sourceGuid, entry?.Id))
                {
                    EditorUtility.SetDirty(authoring);
                    if (PrefabUtility.IsPartOfPrefabInstance(authoring))
                        PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
                    changed = true;
                }
            }
            return changed;
        }

        private static JObject SerializeComponent(GameAssetComponent component, JsonSerializer serializer)
        {
            var result = JObject.FromObject(component, serializer);
            serializer.SerializationBinder.BindToName(component.GetType(), out _, out var typeName);
            result["$type"] = typeName;
            return result;
        }

        private static bool TryGetRecordId(GameAssetAuthoring authoring, string path, out string recordId)
        {
            recordId = null;
            var id = GlobalObjectId.GetGlobalObjectIdSlow(authoring);
            var assetGuid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(assetGuid) || id.targetObjectId == 0)
                return false;
            recordId = assetGuid + ":" + id.targetObjectId + ":" + id.targetPrefabId;
            return true;
        }

        private static IReadOnlyList<GameAssetComponent> CloneComponents(IEnumerable<GameAssetComponent> components)
        {
            var source = ScriptableObject.CreateInstance<GameAsset>();
            GameAsset clone = null;
            try
            {
                source.SetComponents(components);
                clone = UnityEngine.Object.Instantiate(source);
                return clone.Components.ToArray();
            }
            finally
            {
                if (clone != null)
                    UnityEngine.Object.DestroyImmediate(clone);
                UnityEngine.Object.DestroyImmediate(source);
            }
        }
    }

    public class PrefabGameAssetUnityIdentityJsonConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => typeof(UnityEngine.Object).IsAssignableFrom(objectType);

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            if (value is not UnityEngine.Object asset || asset == null)
            {
                writer.WriteNull();
                return;
            }
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long fileId) || string.IsNullOrEmpty(guid))
                throw new InvalidOperationException("Definition resources must be persistent Unity assets: " + asset.name);
            writer.WriteStartObject();
            writer.WritePropertyName("UnityAssetGuid");
            writer.WriteValue(guid);
            writer.WritePropertyName("LocalFileId");
            writer.WriteValue(fileId);
            writer.WriteEndObject();
        }

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            throw new NotSupportedException("Native Unity identities are used only to fingerprint frozen prefab overrides.");
        }
    }

    [InitializeOnLoad]
    public static class PrefabGameAssetSceneSaveHook
    {
        private static bool _saving;

        static PrefabGameAssetSceneSaveHook()
        {
            EditorSceneManager.sceneSaved += OnSceneSaved;
        }

        private static void OnSceneSaved(Scene scene)
        {
            if (_saving || EditorSceneManager.IsPreviewScene(scene) || !PrefabGameAssetIndexConfiguration.Includes(scene.path))
                return;
            _saving = true;
            try
            {
                var changed = false;
                foreach (var root in scene.GetRootGameObjects())
                {
                    changed |= PrefabGameAssetOverrideEditorUtility.BakeHierarchy(root, scene.path);
                }
                if (changed)
                    EditorSceneManager.SaveScene(scene);
                PrefabGameAssetIndexEditorUtility.RequestReconcile();
            }
            catch (Exception error)
            {
                Debug.LogException(error);
            }
            finally
            {
                _saving = false;
            }
        }
    }

    public class PrefabGameAssetSceneBuildProcessor : IProcessSceneWithReport
    {
        public int callbackOrder => -1;

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (!PrefabGameAssetIndexConfiguration.IsConfigured || !PrefabGameAssetIndexConfiguration.Includes(scene.path))
                return;
            var index = AssetDatabase.LoadAssetAtPath<PrefabGameAssetIndex>(PrefabGameAssetIndexConfiguration.IndexAssetPath);
            PrefabGameAssetOverrideEditorUtility.ApplyIndexedSceneMetadata(scene, index);
        }
    }
}
