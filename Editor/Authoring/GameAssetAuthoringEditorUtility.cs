using System;
using System.Collections.Generic;
using System.Linq;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    public static class GameAssetAuthoringEditorUtility
    {
        private static readonly Dictionary<Type, IReadOnlyList<Type>> CONVERTER_TYPES_BY_SOURCE = new();
        private static IReadOnlyList<KeyValuePair<Type, Type>> _converterCatalog;

        public static bool TryGetDefinition(GameAssetAuthoring authoring, out GameAssetAuthoring definition, out string guid, out string error)
        {
            return TryGetDefinition(authoring, out definition, out guid, out _, out error);
        }

        public static bool TryGetDefinition(GameAssetAuthoring authoring, out GameAssetAuthoring definition, out string guid, out bool isDefinition, out string error)
        {
            definition = null;
            guid = null;
            isDefinition = false;
            error = null;
            if (authoring == null)
            {
                error = "The GameAssetAuthoring component is missing.";
                return false;
            }
            var stage = PrefabStageUtility.GetPrefabStage(authoring.gameObject);
            string path;
            if (stage != null && stage.prefabContentsRoot == authoring.gameObject)
            {
                path = stage.assetPath;
                isDefinition = true;
            }
            else if (EditorUtility.IsPersistent(authoring) && AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(authoring)) == authoring.gameObject)
            {
                path = AssetDatabase.GetAssetPath(authoring);
                isDefinition = true;
            }
            else
            {
                if (PrefabUtility.GetNearestPrefabInstanceRoot(authoring.gameObject) != authoring.gameObject || PrefabUtility.GetCorrespondingObjectFromSource(authoring) == null)
                {
                    error = "GameAssetAuthoring must be on a prefab asset root or an instance of that root; plain scene objects and manually authored child markers are invalid.";
                    return false;
                }
                path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(authoring.gameObject);
            }
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null || !path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || !prefab.TryGetComponent(out definition))
            {
                error = "The source prefab has no root GameAssetAuthoring: " + path;
                return false;
            }
            guid = AssetDatabase.AssetPathToGUID(path);
            if (string.IsNullOrEmpty(guid))
            {
                error = "Save the prefab asset before collecting its GameAsset definition.";
                return false;
            }
            return true;
        }

        public static bool TryCollect(GameAssetAuthoring authoring, out GameAssetAuthoringCollection collection, out string error, GameAssetAuthoringPreparation preparation = null)
        {
            collection = null;
            if (authoring == null)
            {
                error = "A GameAsset authoring component is required.";
                return false;
            }
            preparation ??= new GameAssetAuthoringPreparation();
            if (preparation.TryGetCollection(authoring, out collection, out error))
                return collection != null;
            var success = TryCollectComponents(authoring, out collection, out error, preparation);
            preparation.StoreCollection(authoring, collection, error);
            return success;
        }

        private static bool TryCollectComponents(GameAssetAuthoring authoring, out GameAssetAuthoringCollection collection, out string error, GameAssetAuthoringPreparation preparation)
        {
            collection = null;
            if (!TryGetDefinition(authoring, out var definition, out _, out var isDefinition, out error))
                return false;
            GameAssetAuthoringCollection original = null;
            if (!isDefinition)
            {
                if (!TryCollect(definition, out original, out error, preparation))
                {
                    error = "The source prefab definition is invalid: " + error;
                    return false;
                }
                if (!TryValidateInstanceStructure(authoring, definition, out error))
                    return false;
            }
            if (!TryPrepareConverters(authoring, out _, out error, preparation) || !GameAssetAuthoringCollector.TryCollect(authoring, out var effective, out error))
                return false;
            if (!isDefinition && !original.Components.Select(value => value.GetType()).ToHashSet().SetEquals(effective.Components.Select(value => value.GetType())))
            {
                error = "Prefab instances may override values only; the resulting GAC type set differs from the source prefab.";
                return false;
            }
            collection = effective;
            return true;
        }

        public static bool PrepareConverters(GameAssetAuthoring authoring)
        {
            if (!TryPrepareConverters(authoring, out var changed, out var error))
                Debug.LogError(error, authoring);
            return changed;
        }

        public static bool TryPrepareConverters(GameAssetAuthoring authoring, out bool changed, out string error, GameAssetAuthoringPreparation preparation = null)
        {
            changed = false;
            error = null;
            if (authoring == null || authoring.Converters == null)
                return true;
            if (preparation != null && preparation.TryGetConverterPreparation(authoring, out error))
                return error == null;
            try
            {
                changed = SynchronizeConverters(authoring);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, authoring);
                error = $"GameAsset authoring '{authoring.name}' automatic converter setup failed: {exception.Message}";
                preparation?.StoreConverterPreparation(authoring, error);
                return false;
            }
            for (var index = 0; index < authoring.Converters.Count; index++)
            {
                var binding = authoring.Converters[index];
                if (binding?.Source == null || binding.Converter == null)
                    continue;
                try
                {
                    changed |= binding.Converter.Prepare(binding.Source, ResourceGuid);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, authoring);
                    error = $"GameAsset authoring '{authoring.name}' binding #{index + 1}: converter '{binding.Converter.GetType().FullName}' preparation failed: {exception.Message}";
                    changed = true;
                    break;
                }
            }
            if (changed)
            {
                EditorUtility.SetDirty(authoring);
                if (PrefabUtility.IsPartOfPrefabInstance(authoring))
                    PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
            }
            preparation?.StoreConverterPreparation(authoring, error);
            return error == null;
        }

        public static bool IsDefinitionAuthoring(GameAssetAuthoring authoring)
        {
            if (authoring == null)
                return false;
            var stage = PrefabStageUtility.GetPrefabStage(authoring.gameObject);
            return stage != null && stage.prefabContentsRoot == authoring.gameObject || EditorUtility.IsPersistent(authoring) && AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(authoring)) == authoring.gameObject;
        }

        public static bool SynchronizeConverters(GameAssetAuthoring authoring, Func<Component, IReadOnlyList<Type>> converterTypes = null)
        {
            if (!IsDefinitionAuthoring(authoring) || authoring.Converters == null)
                return false;
            converterTypes ??= GetConverterTypes;
            var assigned = new HashSet<Component>(authoring.Converters.Where(binding => binding?.Source != null).Select(binding => binding.Source));
            var bindings = new List<GameAssetAuthoringConverterBinding>(authoring.Converters);
            var discovered = new Dictionary<Type, IReadOnlyList<Type>>();
            foreach (var source in authoring.GetComponentsInChildren<Component>(true))
            {
                if (source == null || assigned.Contains(source) || source.GetComponentInParent<GameAssetAuthoring>(true) != authoring)
                    continue;
                var sourceType = source.GetType();
                if (!discovered.TryGetValue(sourceType, out var types))
                {
                    types = converterTypes(source).OrderBy(type => type.FullName, StringComparer.Ordinal).ThenBy(type => type.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
                    discovered.Add(sourceType, types);
                }
                if (types.Count == 0)
                    continue;
                var converter = (GameAssetAuthoringConverter)Activator.CreateInstance(types[0]);
                bindings.Add(new GameAssetAuthoringConverterBinding(source, converter));
            }
            if (bindings.Count == authoring.Converters.Count)
                return false;
            Undo.RecordObject(authoring, "Add GameAsset converters");
            authoring.SetConverters(bindings);
            EditorUtility.SetDirty(authoring);
            if (PrefabUtility.IsPartOfPrefabInstance(authoring))
                PrefabUtility.RecordPrefabInstancePropertyModifications(authoring);
            return true;
        }

        private static string ResourceGuid(UnityEngine.Object resource)
        {
            if (resource == null)
                return null;
            if (resource is GameObject root && root.TryGetComponent<GameAssetAuthoring>(out var authoring) && TryGetDefinition(authoring, out _, out var guid, out _))
                return guid;
            return AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(resource));
        }

        public static bool TryValidateInstanceStructure(GameAssetAuthoring instance, GameAssetAuthoring definition, out string error)
        {
            error = null;
            if (instance == null || definition == null || instance.Components == null || definition.Components == null || instance.Converters == null || definition.Converters == null)
            {
                error = "The prefab instance or source definition has missing authoring data.";
                return false;
            }
            if (instance.Components.Count != definition.Components.Count)
            {
                error = "Prefab instances cannot add or remove manual GAC components.";
                return false;
            }
            for (var i = 0; i < instance.Components.Count; i++)
            {
                if (instance.Components[i]?.GetType() != definition.Components[i]?.GetType())
                {
                    error = $"Prefab instances cannot change the type or order of manual GAC #{i + 1}.";
                    return false;
                }
            }
            if (instance.Converters.Count != definition.Converters.Count)
            {
                error = "Prefab instances cannot add or remove converter bindings.";
                return false;
            }
            var path = AssetDatabase.GetAssetPath(definition.gameObject);
            for (var i = 0; i < instance.Converters.Count; i++)
            {
                var current = instance.Converters[i];
                var original = definition.Converters[i];
                if (current == null || original == null || current.Converter?.GetType() != original.Converter?.GetType())
                {
                    error = $"Prefab instances cannot change converter type at binding #{i + 1}.";
                    return false;
                }
                var source = current.Source == null ? null : PrefabUtility.GetCorrespondingObjectFromSourceAtPath(current.Source, path);
                if (current.Source != original.Source && source != original.Source)
                {
                    error = $"Prefab instances cannot change the source component at binding #{i + 1}.";
                    return false;
                }
            }
            return true;
        }

        public static IReadOnlyList<Type> GetConverterTypes(Component source)
        {
            if (source == null)
                return Array.Empty<Type>();
            var sourceType = source.GetType();
            if (CONVERTER_TYPES_BY_SOURCE.TryGetValue(sourceType, out var types))
                return types;
            _converterCatalog ??= CreateConverterCatalog();
            types = Array.AsReadOnly(_converterCatalog.Where(entry => entry.Value.IsAssignableFrom(sourceType)).Select(entry => entry.Key).ToArray());
            CONVERTER_TYPES_BY_SOURCE.Add(sourceType, types);
            return types;
        }

        private static IReadOnlyList<KeyValuePair<Type, Type>> CreateConverterCatalog()
        {
            var entries = new List<KeyValuePair<Type, Type>>();
            var playerAssemblies = new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.Player).Select(assembly => assembly.name), StringComparer.Ordinal);
            foreach (var type in TypeCache.GetTypesDerivedFrom<GameAssetAuthoringConverter>())
            {
                if (!playerAssemblies.Contains(type.Assembly.GetName().Name) || (!type.IsPublic && !type.IsNestedPublic) || type.IsAbstract || type.IsGenericType || !Attribute.IsDefined(type, typeof(SerializableAttribute)) || Attribute.IsDefined(type, typeof(HideInTypeMenuAttribute)) || type.GetConstructor(Type.EmptyTypes) == null)
                    continue;
                try
                {
                    var converter = (GameAssetAuthoringConverter)Activator.CreateInstance(type);
                    var supported = converter.SourceComponentType;
                    if (supported != null && typeof(Component).IsAssignableFrom(supported))
                        entries.Add(new KeyValuePair<Type, Type>(type, supported));
                }
                catch
                {
                    // An unavailable converter constructor cannot be offered in the Inspector.
                }
            }
            return entries.OrderBy(entry => entry.Key.FullName, StringComparer.Ordinal).ThenBy(entry => entry.Key.AssemblyQualifiedName, StringComparer.Ordinal).ToArray();
        }

        public static bool TryGetReferenceAuthoring(UnityEngine.Object candidate, out GameAssetAuthoring authoring, out string error)
        {
            authoring = null;
            error = null;
            if (candidate == null)
                return true;
            var gameObject = candidate is GameObject value ? value : (candidate as Component)?.gameObject;
            if (gameObject == null || !EditorUtility.IsPersistent(gameObject) || AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GetAssetPath(gameObject)) != gameObject || !gameObject.TryGetComponent(out authoring))
            {
                authoring = null;
                error = "Assign a prefab asset with GameAssetAuthoring on its root. Scene objects and nested child objects are not definition references.";
                return false;
            }
            return true;
        }
    }
}
