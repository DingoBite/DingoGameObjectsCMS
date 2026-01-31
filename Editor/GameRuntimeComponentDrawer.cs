#if UNITY_EDITOR
using System;
using DingoGameObjectsCMS.RuntimeObjects;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor
{
    [CustomPropertyDrawer(typeof(GameRuntimeComponent), true)]
    public sealed class GameRuntimeComponentDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            label = new GUIContent(GetNiceTypeName(property));

            var line = new Rect(position.x, position.y, position.width, EditorGUIUtility.singleLineHeight);
            property.isExpanded = EditorGUI.Foldout(line, property.isExpanded, label, true);

            if (!property.isExpanded)
                return;

            EditorGUI.indentLevel++;

            var y = line.yMax + EditorGUIUtility.standardVerticalSpacing;

            var it = property.Copy();
            var end = it.GetEndProperty();

            it.NextVisible(true);

            while (!SerializedProperty.EqualContents(it, end))
            {
                var h = EditorGUI.GetPropertyHeight(it, true);
                var r = new Rect(position.x, y, position.width, h);
                EditorGUI.PropertyField(r, it, true);
                y += h + EditorGUIUtility.standardVerticalSpacing;

                it.NextVisible(false);
            }

            EditorGUI.indentLevel--;
        }

        public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
        {
            var height = EditorGUIUtility.singleLineHeight;

            if (!property.isExpanded)
                return height;

            var it = property.Copy();
            var end = it.GetEndProperty();
            it.NextVisible(true);

            while (!SerializedProperty.EqualContents(it, end))
            {
                height += EditorGUI.GetPropertyHeight(it, true) + EditorGUIUtility.standardVerticalSpacing;
                it.NextVisible(false);
            }

            return height;
        }

        private static string GetNiceTypeName(SerializedProperty property)
        {
            if (property.propertyType != SerializedPropertyType.ManagedReference)
                return property.displayName;

            var full = property.managedReferenceFullTypename;
            if (string.IsNullOrEmpty(full))
                return "None";

            var parts = full.Split(' ');
            if (parts.Length < 2)
                return full;

            var asm = parts[0].Trim('[', ']');
            var typeName = parts[1].Replace('/', '+');

            var t = Type.GetType($"{typeName}, {asm}");

            var shortName = typeName;
            var dot = shortName.LastIndexOf('.');
            if (dot >= 0)
                shortName = shortName[(dot + 1)..];
            if (t == null)
                return ObjectNames.NicifyVariableName(shortName);

            return ObjectNames.NicifyVariableName(t.Name);
        }
    }
}
#endif