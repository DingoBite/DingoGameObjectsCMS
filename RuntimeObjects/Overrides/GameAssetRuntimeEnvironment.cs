using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    /// <summary>
    /// Generic immutable GA environment. A project only supplies its generated
    /// codec registry and codec context; the SDK owns manifest initialization
    /// and seals the selected content catalog for the lifetime of the session.
    /// </summary>
    public sealed class GameAssetRuntimeEnvironment
    {
        public RuntimePatchCodecRegistry PatchCodecs { get; }
        public GameAssetTemplateCache Templates { get; }
        public GameAssetLibraryLock LibraryLock { get; }
        public IReadOnlyList<ModPackage> MountedModules { get; }

        public string RuntimeSchemaHash => PatchCodecs.SchemaHash;

        public GameAssetRuntimeEnvironment(
            RuntimePatchCodecRegistry patchCodecs,
            RuntimePatchCodecContext templateContext)
        {
            PatchCodecs = patchCodecs ?? throw new ArgumentNullException(nameof(patchCodecs));
            if (templateContext == null)
                throw new ArgumentNullException(nameof(templateContext));
            if (!RuntimeComponentTypeRegistry.IsInitialized)
                throw new InvalidOperationException("Runtime component types must be initialized before the GA runtime environment.");

            GameAssetLibraryManifest.EnsureInitialized();
            Templates = new GameAssetTemplateCache(PatchCodecs, templateContext);
            MountedModules = GameAssetLibraryManifest
                .CollectImmutableModPackages();
            LibraryLock = BuildConfiguredSessionSnapshot();
        }

        private GameAssetLibraryLock BuildConfiguredSessionSnapshot()
        {
            if (!GameAssetLibraryManifest.HasConfiguredSessionBasePackage)
            {
                throw new InvalidOperationException(
                    "A session base package must be configured explicitly before creating a dynamic GameAsset lock.");
            }
            var sessionLock = GameAssetLibraryLockBuilder.Build(
                Templates,
                MountedModules);
            if (sessionLock.Mods.Count == 0 || sessionLock.Entries.Count == 0)
            {
                throw new InvalidOperationException(
                    $"The configured session GameAsset package at '{GameAssetLibraryManifest.GetSessionBasePackageRoot()}' "
                    + "contains no loadable manifest or assets.");
            }

            return sessionLock;
        }
    }
}
