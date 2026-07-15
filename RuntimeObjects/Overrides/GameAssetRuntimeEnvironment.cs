using System;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    /// <summary>
    /// Generic immutable GA environment. A project only supplies its generated
    /// codec registry and codec context; the SDK owns manifest initialization,
    /// checked-in lock loading and strict package validation.
    /// </summary>
    public sealed class GameAssetRuntimeEnvironment
    {
        public RuntimePatchCodecRegistry PatchCodecs { get; }
        public GameAssetTemplateCache Templates { get; }
        public GameAssetLibraryLock LibraryLock { get; }

        public string RuntimeSchemaHash => PatchCodecs.SchemaHash;

        public GameAssetRuntimeEnvironment(
            RuntimePatchCodecRegistry patchCodecs,
            RuntimePatchCodecContext templateContext,
            string lockPath = null)
        {
            PatchCodecs = patchCodecs ?? throw new ArgumentNullException(nameof(patchCodecs));
            if (templateContext == null)
                throw new ArgumentNullException(nameof(templateContext));
            if (!RuntimeComponentTypeRegistry.IsInitialized)
                throw new InvalidOperationException("Runtime component types must be initialized before the GA runtime environment.");

            GameAssetLibraryManifest.EnsureInitialized();
            Templates = new GameAssetTemplateCache(PatchCodecs, templateContext);
            LibraryLock = GameAssetLibraryLockFile.LoadStrict(
                lockPath ?? GameAssetLibraryLockFile.GetDefaultPath(Application.streamingAssetsPath),
                Templates);
        }
    }
}
