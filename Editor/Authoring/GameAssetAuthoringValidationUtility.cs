using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using UnityEditor;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    [InitializeOnLoad]
    public static class GameAssetAuthoringValidationUtility
    {
        private static readonly ConcurrentQueue<GameAssetAuthoring> PENDING = new();

        static GameAssetAuthoringValidationUtility()
        {
            GameAssetAuthoring.ValidationRequested += Request;
            ObjectChangeEvents.changesPublished += OnObjectChanges;
            EditorApplication.update += PreparePending;
            foreach (var authoring in Resources.FindObjectsOfTypeAll<GameAssetAuthoring>())
            {
                Request(authoring);
            }
        }

        // OnValidate may run on a loading thread. Unity objects are inspected only by the Editor update owner.
        public static void Request(GameAssetAuthoring authoring) => PENDING.Enqueue(authoring);

        private static void PreparePending()
        {
            if (PENDING.IsEmpty || EditorApplication.isCompiling || EditorApplication.isUpdating || PrefabGameAssetOverrideEditorUtility.IsBakingSavedAssets)
                return;
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                PENDING.Clear();
                return;
            }
            var changed = new HashSet<GameAssetAuthoring>();
            while (PENDING.TryDequeue(out var authoring))
            {
                if (authoring != null)
                    changed.Add(authoring);
            }
            foreach (var authoring in changed)
            {
                if (!GameAssetAuthoringEditorUtility.IsDefinitionAuthoring(authoring))
                    continue;
                try
                {
                    GameAssetAuthoringEditorUtility.PrepareConverters(authoring);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception, authoring);
                }
            }
        }

        private static void OnObjectChanges(ref ObjectChangeEventStream changes)
        {
            for (var index = 0; index < changes.length; index++)
            {
                switch (changes.GetEventType(index))
                {
                    case ObjectChangeKind.ChangeGameObjectStructure:
                        changes.GetChangeGameObjectStructureEvent(index, out var structure);
                        RequestHierarchy(structure.entityId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectStructureHierarchy:
                        changes.GetChangeGameObjectStructureHierarchyEvent(index, out var hierarchy);
                        RequestHierarchy(hierarchy.entityId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectParent:
                        changes.GetChangeGameObjectParentEvent(index, out var parent);
                        RequestHierarchy(parent.entityId);
                        RequestHierarchy(parent.previousParentEntityId);
                        break;
                    case ObjectChangeKind.CreateGameObjectHierarchy:
                        changes.GetCreateGameObjectHierarchyEvent(index, out var created);
                        RequestHierarchy(created.entityId);
                        break;
                    case ObjectChangeKind.ChangeGameObjectOrComponentProperties:
                        changes.GetChangeGameObjectOrComponentPropertiesEvent(index, out var properties);
                        RequestHierarchy(properties.entityId);
                        break;
                    case ObjectChangeKind.ChangeAssetObjectProperties:
                        changes.GetChangeAssetObjectPropertiesEvent(index, out var asset);
                        RequestHierarchy(asset.entityId);
                        break;
                }
            }
        }

        private static void RequestHierarchy(EntityId id)
        {
            var changed = EditorUtility.EntityIdToObject(id);
            var root = changed is GameObject gameObject ? gameObject : (changed as Component)?.gameObject;
            if (root == null)
                return;
            var owner = root.GetComponentInParent<GameAssetAuthoring>(true);
            if (owner != null)
                Request(owner);
            foreach (var authoring in root.GetComponentsInChildren<GameAssetAuthoring>(true))
            {
                Request(authoring);
            }
        }
    }
}
