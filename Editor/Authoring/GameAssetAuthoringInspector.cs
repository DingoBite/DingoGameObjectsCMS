using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using MackySoft.SerializeReferenceExtensions.Editor;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    [CustomEditor(typeof(GameAssetAuthoring))]
    public class GameAssetAuthoringInspector : UnityEditor.Editor
    {
        private GameAsset _preview;
        private bool _showPreview = true;
        private bool _needsRefresh = true;
        private GameAssetAuthoringCollection _collection;
        private GameAssetAuthoring _definition;
        private string _guid;
        private string _ownershipError;
        private string _collectionError;
        private bool _hasDefinition;
        private bool _isDefinition;

        private void OnEnable()
        {
            Undo.undoRedoPerformed += InvalidatePreview;
            EditorApplication.projectChanged += InvalidatePreview;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            _needsRefresh = true;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            var authoring = (GameAssetAuthoring)target;
            RefreshPreview(authoring);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.ObjectField("Script", MonoScript.FromMonoBehaviour(authoring), typeof(MonoScript), false);
                if (_hasDefinition)
                {
                    EditorGUILayout.TextField("Prefab GUID", _guid);
                    EditorGUILayout.ObjectField("Source definition", _definition.gameObject, typeof(GameObject), false);
                }
            }
            if (!_hasDefinition)
                EditorGUILayout.HelpBox(_ownershipError, MessageType.Error);
            else if (!_isDefinition)
                EditorGUILayout.HelpBox("This instance may override values. Component types, converter sources and binding composition belong to its prefab. Effective values are preview only in phase 1.", MessageType.Info);

            var components = serializedObject.FindProperty("_components");
            var converters = serializedObject.FindProperty("_converters");
            if (_hasDefinition && !_isDefinition)
                DrawInstanceValues(components, converters);
            else
            {
                EditorGUILayout.PropertyField(components, new GUIContent("Manual GAC components"), true);
                EditorGUILayout.PropertyField(converters, new GUIContent("Converters (source → converter)"), true);
            }
            if (serializedObject.ApplyModifiedProperties())
            {
                _needsRefresh = true;
                RefreshPreview(authoring);
            }

            if (_collection == null)
            {
                EditorGUILayout.HelpBox(_collectionError, MessageType.Error);
                return;
            }
            EditorGUILayout.HelpBox($"Valid definition: {_collection.Components.Count} GAC components. Preview creates definition data only.", MessageType.Info);
            _showPreview = EditorGUILayout.Foldout(_showPreview, "Effective GAC components", true);
            if (!_showPreview)
                return;
            if (_preview == null)
            {
                _preview = ScriptableObject.CreateInstance<GameAsset>();
                _preview.hideFlags = HideFlags.HideAndDontSave;
                _preview.SetComponents(_collection.Components);
            }
            using (var previewFields = new SerializedObject(_preview))
            {
                var values = previewFields.FindProperty("_components");
                using (new EditorGUI.DisabledScope(true))
                {
                    for (var i = 0; i < _collection.Components.Count; i++)
                    {
                        EditorGUILayout.LabelField(_collection.Components[i].GetType().Name, EditorStyles.boldLabel);
                        EditorGUILayout.LabelField("Source", _collection.Sources[i].Description);
                        GameAssetAuthoringInspectorGUI.DrawFields(values.GetArrayElementAtIndex(i));
                    }
                }
            }
        }

        private void RefreshPreview(GameAssetAuthoring authoring)
        {
            if (!_needsRefresh)
                return;
            _needsRefresh = false;
            _hasDefinition = GameAssetAuthoringEditorUtility.TryGetDefinition(authoring, out _definition, out _guid, out _isDefinition, out _ownershipError);
            GameAssetAuthoringEditorUtility.TryCollect(authoring, out _collection, out _collectionError);
            if (_preview != null && _collection != null)
                _preview.SetComponents(_collection.Components);
        }

        private void InvalidatePreview()
        {
            _needsRefresh = true;
            Repaint();
        }

        private void OnObjectChanges(ref ObjectChangeEventStream changes)
        {
            InvalidatePreview();
        }

        private static void DrawInstanceValues(SerializedProperty components, SerializedProperty converters)
        {
            EditorGUILayout.LabelField("Manual GAC values", EditorStyles.boldLabel);
            for (var i = 0; i < components.arraySize; i++)
            {
                var value = components.GetArrayElementAtIndex(i);
                var type = ManagedReferenceUtility.GetType(value.managedReferenceFullTypename);
                value.isExpanded = EditorGUILayout.Foldout(value.isExpanded, type?.Name ?? "Missing GAC", true);
                if (value.isExpanded && value.managedReferenceValue != null)
                {
                    EditorGUI.indentLevel++;
                    GameAssetAuthoringInspectorGUI.DrawFields(value);
                    EditorGUI.indentLevel--;
                }
            }
            EditorGUILayout.LabelField("Converter values", EditorStyles.boldLabel);
            for (var i = 0; i < converters.arraySize; i++)
            {
                var binding = converters.GetArrayElementAtIndex(i);
                var source = binding.FindPropertyRelative("_source");
                var converter = binding.FindPropertyRelative("_converter");
                var type = ManagedReferenceUtility.GetType(converter.managedReferenceFullTypename);
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.PropertyField(source, new GUIContent("Source"));
                    EditorGUILayout.TextField("Converter", type?.Name ?? "Missing converter");
                }
                if (converter.managedReferenceValue != null)
                    GameAssetAuthoringInspectorGUI.DrawFields(converter);
            }
        }

        private void OnDisable()
        {
            Undo.undoRedoPerformed -= InvalidatePreview;
            EditorApplication.projectChanged -= InvalidatePreview;
            ObjectChangeEvents.changesPublished -= OnObjectChanges;
            if (_preview != null)
                DestroyImmediate(_preview);
        }
    }

    [CustomEditor(typeof(PrefabGameAssetIndex))]
    public class PrefabGameAssetIndexInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var index = (PrefabGameAssetIndex)target;
            EditorGUILayout.HelpBox("Generated prefab definition index. Its entries are maintained after prefab save/import and checked before builds.", MessageType.Info);
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.IntField("Definitions", index.Entries.Count);
                foreach (var entry in index.Entries)
                {
                    EditorGUILayout.TextField("Prefab GUID", entry.GUID);
                    EditorGUILayout.ObjectField("Authoring", entry.Authoring, typeof(GameAssetAuthoring), false);
                }
            }
        }
    }
}
