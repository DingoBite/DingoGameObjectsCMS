using System;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public enum GameAssetRuntimeLockSource
    {
        CheckedInFile = 0,
        ConfiguredSessionBase = 1,
    }

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

        public string RuntimeSchemaHash => PatchCodecs.SchemaHash;

        public GameAssetRuntimeEnvironment(
            RuntimePatchCodecRegistry patchCodecs,
            RuntimePatchCodecContext templateContext,
            string lockPath = null)
            : this(
                patchCodecs,
                templateContext,
                GameAssetRuntimeLockSource.CheckedInFile,
                lockPath)
        {
        }

        public GameAssetRuntimeEnvironment(
            RuntimePatchCodecRegistry patchCodecs,
            RuntimePatchCodecContext templateContext,
            GameAssetRuntimeLockSource lockSource,
            string lockPath = null)
        {
            PatchCodecs = patchCodecs ?? throw new ArgumentNullException(nameof(patchCodecs));
            if (templateContext == null)
                throw new ArgumentNullException(nameof(templateContext));
            if (!RuntimeComponentTypeRegistry.IsInitialized)
                throw new InvalidOperationException("Runtime component types must be initialized before the GA runtime environment.");

            GameAssetLibraryManifest.EnsureInitialized();
            Templates = new GameAssetTemplateCache(PatchCodecs, templateContext);
            LibraryLock = lockSource switch
            {
                GameAssetRuntimeLockSource.CheckedInFile => GameAssetLibraryLockFile.LoadStrict(
                    lockPath ?? GameAssetLibraryLockFile.GetDefaultPath(Application.streamingAssetsPath),
                    Templates),
                GameAssetRuntimeLockSource.ConfiguredSessionBase => BuildConfiguredSessionLock(lockPath),
                _ => throw new ArgumentOutOfRangeException(nameof(lockSource), lockSource, null),
            };
        }

        private GameAssetLibraryLock BuildConfiguredSessionLock(string lockPath)
        {
            if (!GameAssetLibraryManifest.HasConfiguredSessionBasePackage)
            {
                throw new InvalidOperationException(
                    "A session base package must be configured explicitly before creating a dynamic GameAsset lock.");
            }
            if (!string.IsNullOrWhiteSpace(lockPath))
            {
                throw new ArgumentException(
                    "A lock file path cannot be combined with a dynamically sealed session package.",
                    nameof(lockPath));
            }

            var sessionLock = GameAssetLibraryLockBuilder.Build(Templates);
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
