using System.Collections.Generic;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using UnityEngine;

namespace DingoGameObjectsCMS.Editor.Authoring
{
    // One synchronous preparation owns these results. Saving or reimporting assets ends their validity.
    public class GameAssetAuthoringPreparation
    {
        private static int _assetImportVersion;
        private readonly Dictionary<GameAssetAuthoring, string> _preparationErrors = new();
        private readonly Dictionary<GameAssetAuthoring, GameAssetAuthoringCollection> _collections = new();
        private readonly Dictionary<GameAssetAuthoring, string> _collectionErrors = new();
        private int _importVersion = _assetImportVersion;

        public static void NotifyAssetsChanged() => _assetImportVersion++;

        public bool TryGetConverterPreparation(GameAssetAuthoring authoring, out string error)
        {
            EnsureCurrentImports();
            return _preparationErrors.TryGetValue(authoring, out error);
        }

        public void StoreConverterPreparation(GameAssetAuthoring authoring, string error)
        {
            EnsureCurrentImports();
            _preparationErrors[authoring] = error;
        }

        public bool TryGetCollection(GameAssetAuthoring authoring, out GameAssetAuthoringCollection collection, out string error)
        {
            EnsureCurrentImports();
            error = null;
            if (_collections.TryGetValue(authoring, out collection))
            {
                _collectionErrors.TryGetValue(authoring, out error);
                return true;
            }
            return false;
        }

        public void StoreCollection(GameAssetAuthoring authoring, GameAssetAuthoringCollection collection, string error)
        {
            EnsureCurrentImports();
            _collections[authoring] = collection;
            _collectionErrors[authoring] = error;
        }

        public void Invalidate()
        {
            _preparationErrors.Clear();
            _collections.Clear();
            _collectionErrors.Clear();
            _importVersion = _assetImportVersion;
        }

        public void ReleaseHierarchy(GameObject root)
        {
            foreach (var authoring in root.GetComponentsInChildren<GameAssetAuthoring>(true))
            {
                _preparationErrors.Remove(authoring);
                _collections.Remove(authoring);
                _collectionErrors.Remove(authoring);
            }
        }

        private void EnsureCurrentImports()
        {
            if (_importVersion != _assetImportVersion)
                Invalidate();
        }
    }
}
