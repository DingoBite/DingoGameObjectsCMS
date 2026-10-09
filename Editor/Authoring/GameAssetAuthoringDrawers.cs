using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using MackySoft.SerializeReferenceExtensions.Editor;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    public static class GameAssetAuthoringInspectorGUI
    {
        public static void DrawFields(SerializedProperty property)
        {
            var iterator = property.Copy();
            var end = iterator.GetEndProperty();
            if (!iterator.NextVisible(true))
                return;
            while (!SerializedProperty.EqualContents(iterator, end))
            {
                EditorGUILayout.PropertyField(iterator, true);
                if (!iterator.NextVisible(false))
                    break;
            }
        }

        public static void DrawFields(Rect position, SerializedProperty property)
        {
            var iterator = property.Copy();
            var end = iterator.GetEndProperty();
            if (!iterator.NextVisible(true))
                return;
            while (!SerializedProperty.EqualContents(iterator, end))
            {
                var height = EditorGUI.GetPropertyHeight(iterator, true);
                EditorGUI.PropertyField(new Rect(position.x, position.y, position.width, height), iterator, true);
                position.y += height + EditorGUIUtility.standardVerticalSpacing;
                if (!iterator.NextVisible(false))
                    break;
            }
        }

        public static float FieldsHeight(SerializedProperty property)
        {
            var height = 0f;
            var iterator = property.Copy();
            var end = iterator.GetEndProperty();
            if (!iterator.NextVisible(true))
                return height;
            while (!SerializedProperty.EqualContents(iterator, end))
            {
                height += EditorGUI.GetPropertyHeight(iterator, true) + EditorGUIUtility.standardVerticalSpacing;
                if (!iterator.NextVisible(false))
                    break;
            }
            return height;
        }
    }

    [CustomPropertyDrawer(typeof(GameAssetAuthoringConverterBinding))]
    public class GameAssetAuthoringConverterBindingDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var source = property.FindPropertyRelative("_source");
            var converter = property.FindPropertyRelative("_converter");
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            property.isExpanded = EditorGUI.Foldout(new Rect(line.x, line.y, 15f, line.height), property.isExpanded, GUIContent.none);
            var width = (line.width - 21f) * .5f;
            EditorGUI.PropertyField(new Rect(line.x + 16f, line.y, width, line.height), source, GUIContent.none);
            var popup = new Rect(line.x + 20f + width, line.y, width, line.height);
            var converterType = ManagedReferenceUtility.GetType(converter.managedReferenceFullTypename);
            using (new EditorGUI.DisabledScope(source.objectReferenceValue == null))
            {
                if (EditorGUI.DropdownButton(popup, new GUIContent(converterType == null ? "None" : ObjectNames.NicifyVariableName(converterType.Name)), FocusType.Keyboard))
                    ShowConverters(popup, source.objectReferenceValue as Component, converter);
            }
            if (property.isExpanded && converter.managedReferenceValue != null)
            {
                EditorGUI.indentLevel++;
                GameAssetAuthoringInspectorGUI.DrawFields(new Rect(position.x, line.yMax + EditorGUIUtility.standardVerticalSpacing, position.width, position.height - line.height), converter);
                EditorGUI.indentLevel--;
            }
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var converter = property.FindPropertyRelative("_converter");
            return EditorGUIUtility.singleLineHeight + (property.isExpanded && converter.managedReferenceValue != null ? EditorGUIUtility.standardVerticalSpacing + GameAssetAuthoringInspectorGUI.FieldsHeight(converter) : 0f);
        }

        private static void ShowConverters(Rect position, Component source, SerializedProperty property)
        {
            var menu = new GenericMenu();
            var path = property.propertyPath;
            var targets = property.serializedObject.targetObjects;
            var current = ManagedReferenceUtility.GetType(property.managedReferenceFullTypename);
            menu.AddItem(new GUIContent(TypeMenuUtility.k_NullDisplayName), current == null, () => SetConverter(targets, path, null));
            foreach (var type in GameAssetAuthoringEditorUtility.GetConverterTypes(source))
            {
                var selected = type;
                var title = string.Join("/", TypeMenuUtility.GetSplittedTypePath(type));
                menu.AddItem(new GUIContent(title), current == type, () => SetConverter(targets, path, selected));
            }
            menu.DropDown(position);
        }

        private static void SetConverter(IEnumerable<UnityEngine.Object> targets, string path, Type type)
        {
            foreach (var target in targets)
            {
                if (target == null)
                    continue;
                using (var fields = new SerializedObject(target))
                {
                    var converter = fields.FindProperty(path);
                    if (converter == null)
                        continue;
                    converter.SetManagedReference(type);
                    converter.isExpanded = true;
                    fields.ApplyModifiedProperties();
                }
            }
        }
    }

    [CustomPropertyDrawer(typeof(PrefabGameAssetReference))]
    public class PrefabGameAssetReferenceDrawer : PropertyDrawer
    {
        private readonly Dictionary<string, string> _errors = new();

        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            EditorGUI.BeginProperty(position, label, property);
            var reference = property.FindPropertyRelative("_authoring");
            var current = reference.objectReferenceValue as GameAssetAuthoring;
            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            var identity = property.serializedObject.targetObject.GetEntityId() + ":" + property.propertyPath;
            EditorGUI.BeginChangeCheck();
            var candidate = EditorGUI.ObjectField(line, label, current == null ? null : current.gameObject, typeof(GameObject), false);
            if (EditorGUI.EndChangeCheck())
            {
                if (GameAssetAuthoringEditorUtility.TryGetReferenceAuthoring(candidate, out var authoring, out var error))
                {
                    reference.objectReferenceValue = authoring;
                    _errors.Remove(identity);
                }
                else
                    _errors[identity] = error;
            }
            _errors.TryGetValue(identity, out var message);
            if (message == null && current != null && !GameAssetAuthoringEditorUtility.TryGetReferenceAuthoring(current, out _, out var currentError))
                message = currentError;
            var help = new Rect(position.x, line.yMax + EditorGUIUtility.standardVerticalSpacing, position.width, EditorGUIUtility.singleLineHeight * 2f);
            EditorGUI.HelpBox(help, message ?? "Prefab definition reference; scene objects are not allowed.", message == null ? MessageType.None : MessageType.Error);
            EditorGUI.EndProperty();
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label) => EditorGUIUtility.singleLineHeight * 3f + EditorGUIUtility.standardVerticalSpacing;
    }
}
