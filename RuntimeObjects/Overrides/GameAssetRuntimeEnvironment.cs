using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public class GameAssetRuntimeEnvironment : IDisposable
    {
        private bool _disposed;

        public RuntimePatchCodecRegistry PatchCodecs { get; }
        public GameAssetTemplateCache Templates { get; }
        public GameAssetLibraryLock LibraryLock { get; }
        public GameAssetSessionCatalog AssetCatalog => LibraryLock.AssetCatalog;
        public IReadOnlyList<ModPackage> MountedModules { get; }
        public PrefabGameAssetDefinitions PrefabDefinitions { get; }
        public bool IsDisposed => _disposed;
        
        public string RuntimeSchemaHash => PatchCodecs.SchemaHash;

        public GameAssetRuntimeEnvironment(RuntimePatchCodecRegistry patchCodecs, RuntimePatchCodecContext templateContext)
            : this(patchCodecs, templateContext, null, false) {}

        public GameAssetRuntimeEnvironment(RuntimePatchCodecRegistry patchCodecs, RuntimePatchCodecContext templateContext, PrefabGameAssetIndex prefabIndex)
            : this(patchCodecs, templateContext, prefabIndex, true) {}

        private GameAssetRuntimeEnvironment(RuntimePatchCodecRegistry patchCodecs, RuntimePatchCodecContext templateContext, PrefabGameAssetIndex prefabIndex, bool requirePrefabIndex)
        {
            PatchCodecs = patchCodecs ?? throw new ArgumentNullException(nameof(patchCodecs));
            if (templateContext == null)
                throw new ArgumentNullException(nameof(templateContext));
            if (requirePrefabIndex && prefabIndex == null)
                throw new ArgumentNullException(nameof(prefabIndex), "A native prefab GameAsset runtime environment requires its configured prefab index.");
            if (!RuntimeComponentTypeRegistry.IsInitialized)
                throw new InvalidOperationException("Runtime component types must be initialized before the GA runtime environment.");

            GameAssetLibraryManifest.EnsureInitialized();
            Templates = new GameAssetTemplateCache(PatchCodecs, templateContext);
            try
            {
                if (prefabIndex != null)
                {
                    if (!PrefabGameAssetDefinitions.TryPrepare(prefabIndex, out var definitions, out var error))
                        throw new InvalidOperationException($"Prefab GameAsset definitions are not ready: {error}");
                    PrefabDefinitions = definitions;
                }
                MountedModules = GameAssetLibraryManifest.CollectImmutableModPackages();
                LibraryLock = BuildConfiguredSessionSnapshot();
            }
            catch
            {
                Templates.Clear();
                PrefabDefinitions?.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            Templates.Clear();
            PrefabDefinitions?.Dispose();
        }

        private GameAssetLibraryLock BuildConfiguredSessionSnapshot()
        {
            if (!GameAssetLibraryManifest.HasConfiguredSessionBasePackage)
            {
                throw new InvalidOperationException("A session base package must be configured explicitly before creating a dynamic GameAsset lock.");
            }

            var sessionLock = GameAssetLibraryLockBuilder.Build(Templates, MountedModules, PrefabDefinitions);
            if (sessionLock.Mods.Count == 0 || sessionLock.Entries.Count == 0)
            {
                throw new InvalidOperationException($"The configured session GameAsset package at '{GameAssetLibraryManifest.GetSessionBasePackageRoot()}' " + "contains no loadable manifest or assets.");
            }

            return sessionLock;
        }
    }
}
