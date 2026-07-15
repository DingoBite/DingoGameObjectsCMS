using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor
{
    public class GameAssetInstanceRuntimeComponentEditHost : ScriptableObject
    {
        [SerializeReference] public GameRuntimeComponent Value;
    }

    public class GameAssetInstanceOverrideDrawer : IDisposable
    {
        private readonly Dictionary<uint, GameAssetInstanceRuntimeComponentEditHost> _componentHosts = new();
        private readonly Dictionary<uint, SerializedObject> _serializedHosts = new();
        private readonly Dictionary<uint, bool> _expanded = new();
        private readonly HashSet<string> _editingInheritedFields = new(StringComparer.Ordinal);

        private GameAssetInstanceOverrideHost _host;
        private Vector2 _scroll;

        public void Bind(GameAssetInstanceOverrideHost host)
        {
            ReleaseComponentHosts();
            _host = host ?? throw new ArgumentNullException(nameof(host));
            var typeIds = _host.ComponentTypeIds;
            for (var i = 0; i < typeIds.Count; i++)
            {
                if (_host.TryTake(typeIds[i], out _, out var current) && current != null)
                    RebuildComponentHost(typeIds[i]);
            }
        }

        public void Draw()
        {
            if (_host == null)
            {
                EditorGUILayout.HelpBox("Bind a GameAssetInstanceOverrideHost before drawing overrides.", MessageType.Error);
                return;
            }

            var instance = _host.Value;
            using (new EditorGUI.DisabledScope(true))
            {
                EditorGUILayout.TextField("Instance GUID", instance.InstanceGuid.ToString());
                EditorGUILayout.TextField("Requested GA", instance.Asset.RequestedKey.ToString());
                EditorGUILayout.TextField("Resolved GA", _host.ResolvedAsset.Key.ToString());
            }
            EditorGUILayout.LabelField(
                instance.Patch == null ? "No overrides" : $"Overrides: {instance.Patch.Components?.Count ?? 0} component(s)",
                EditorStyles.helpBox);

            if (GUILayout.Button("Add Component"))
                ShowAddComponentMenu();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var typeIds = _host.ComponentTypeIds.ToArray();
            for (var i = 0; i < typeIds.Length; i++)
            {
                DrawComponent(typeIds[i]);
            }
            EditorGUILayout.EndScrollView();
        }

        public void Dispose()
        {
            ReleaseComponentHosts();
            _host = null;
        }

        private void DrawComponent(uint typeId)
        {
            if (!_host.TryTake(typeId, out var baseline, out var current))
                return;

            var type = current?.GetType() ?? baseline?.GetType() ?? typeId.GetRegisteredType();
            var state = _host.GetComponentState(typeId);
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();
            var expanded = _expanded.TryGetValue(typeId, out var stored) && stored;
            expanded = EditorGUILayout.Foldout(expanded, $"{type.Name}  [{state}]", true);
            _expanded[typeId] = expanded;

            if (current == null)
            {
                if (GUILayout.Button("Revert", GUILayout.Width(64)))
                {
                    _host.RevertComponent(typeId);
                    RebuildComponentHost(typeId);
                }
            }
            else if (baseline == null)
            {
                if (GUILayout.Button("Remove", GUILayout.Width(64)))
                {
                    _host.RemoveComponent(typeId);
                    RebuildComponentHost(typeId);
                }
            }
            else
            {
                if (state != GameAssetInstanceComponentOverrideState.Inherited
                    && GUILayout.Button("Revert", GUILayout.Width(64)))
                {
                    _host.RevertComponent(typeId);
                    RebuildComponentHost(typeId);
                }
                if (GUILayout.Button("Remove", GUILayout.Width(64)))
                {
                    _host.RemoveComponent(typeId);
                    RebuildComponentHost(typeId);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (expanded && current != null)
                DrawFields(typeId, baseline, current);
            EditorGUILayout.EndVertical();
        }

        private void DrawFields(uint typeId, GameRuntimeComponent baseline, GameRuntimeComponent current)
        {
            if (!_componentHosts.TryGetValue(typeId, out var componentHost) || componentHost == null)
                RebuildComponentHost(typeId);
            if (!_componentHosts.TryGetValue(typeId, out componentHost) || componentHost == null)
                return;

            var serializedHost = _serializedHosts[typeId];
            serializedHost.Update();
            var valueProperty = serializedHost.FindProperty(nameof(GameAssetInstanceRuntimeComponentEditHost.Value));
            var fields = current.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Where(field => !field.IsStatic)
                .OrderBy(field => field.MetadataToken)
                .ToArray();
            if (fields.Length == 0)
            {
                EditorGUILayout.LabelField("Tag component (zero payload)", EditorStyles.miniLabel);
                return;
            }

            var codec = _host.Registry.Get(typeId);
            var componentPatch = baseline == null ? null : _host.BuildComponentPatch(typeId);
            for (var i = 0; i < fields.Length; i++)
            {
                var field = fields[i];
                var property = valueProperty?.FindPropertyRelative(field.Name);
                if (property == null)
                {
                    EditorGUILayout.HelpBox($"Field '{field.Name}' cannot be edited by Unity serialization.", MessageType.Warning);
                    continue;
                }

                var fieldKey = $"{codec.ComponentTypeKey}/{field.Name}";
                var isPersistedOverride = baseline == null || IsFieldOverridden(componentPatch, fieldKey);
                var isEditing = isPersistedOverride || _editingInheritedFields.Contains(fieldKey);
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                var enabled = EditorGUILayout.Toggle(isEditing, GUILayout.Width(18));
                if (EditorGUI.EndChangeCheck())
                {
                    if (enabled)
                    {
                        _editingInheritedFields.Add(fieldKey);
                    }
                    else if (baseline != null)
                    {
                        _host.RevertField(typeId, field);
                        _editingInheritedFields.Remove(fieldKey);
                        RebuildComponentHost(typeId);
                        EditorGUILayout.EndHorizontal();
                        return;
                    }
                }

                using (new EditorGUI.DisabledScope(!enabled))
                {
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(property, new GUIContent(field.Name), true);
                    if (EditorGUI.EndChangeCheck())
                    {
                        serializedHost.ApplyModifiedProperties();
                        _host.CommitComponent(typeId, componentHost.Value);
                    }
                }
                if (isPersistedOverride && baseline != null && GUILayout.Button("Revert", GUILayout.Width(54)))
                {
                    _host.RevertField(typeId, field);
                    _editingInheritedFields.Remove(fieldKey);
                    RebuildComponentHost(typeId);
                    EditorGUILayout.EndHorizontal();
                    return;
                }
                EditorGUILayout.EndHorizontal();
            }
        }

        private void ShowAddComponentMenu()
        {
            var menu = new GenericMenu();
            var count = 0;
            for (var i = 0; i < RuntimeComponentTypeRegistry.TypesById.Count; i++)
            {
                var type = RuntimeComponentTypeRegistry.TypesById[i];
                if (type == null || type.IsAbstract || !typeof(GameRuntimeComponent).IsAssignableFrom(type))
                    continue;

                var typeId = (uint)i;
                if (_host.Current.ContainsKey(typeId) || !_host.Registry.TryGet(typeId, out _))
                    continue;

                var capturedType = type;
                var capturedId = typeId;
                menu.AddItem(new GUIContent(type.FullName), false, () => AddComponent(capturedId, capturedType));
                count++;
            }
            if (count == 0)
                menu.AddDisabledItem(new GUIContent("No components available"));
            menu.ShowAsContext();
        }

        private void AddComponent(uint typeId, Type type)
        {
            if (Activator.CreateInstance(type) is not GameRuntimeComponent component)
                throw new InvalidOperationException($"Runtime component '{type.FullName}' cannot be constructed for authoring.");

            _host.AddComponent(typeId, component);
            _expanded[typeId] = true;
            RebuildComponentHost(typeId);
        }

        private static bool IsFieldOverridden(ComponentPatch componentPatch, string fieldKey)
        {
            if (componentPatch == null)
                return false;
            if (componentPatch.Kind != ComponentPatchKind.Fields)
                return true;
            return componentPatch.Fields != null
                   && componentPatch.Fields.Any(field => string.Equals(field.FieldKey, fieldKey, StringComparison.Ordinal));
        }

        private void RebuildComponentHost(uint typeId)
        {
            if (_componentHosts.Remove(typeId, out var existing) && existing != null)
                UnityEngine.Object.DestroyImmediate(existing);
            _serializedHosts.Remove(typeId);
            if (!_host.TryTake(typeId, out _, out var component) || component == null)
                return;

            var componentHost = ScriptableObject.CreateInstance<GameAssetInstanceRuntimeComponentEditHost>();
            componentHost.hideFlags = HideFlags.HideAndDontSave;
            componentHost.Value = component;
            _componentHosts[typeId] = componentHost;
            _serializedHosts[typeId] = new SerializedObject(componentHost);
        }

        private void ReleaseComponentHosts()
        {
            foreach (var componentHost in _componentHosts.Values)
            {
                if (componentHost != null)
                    UnityEngine.Object.DestroyImmediate(componentHost);
            }
            _componentHosts.Clear();
            _serializedHosts.Clear();
        }
    }
}
