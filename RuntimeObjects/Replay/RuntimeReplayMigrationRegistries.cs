using System;
using System.Collections.Generic;
using System.Linq;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public enum RuntimeReplayMigrationFailureKind : byte
    {
        None = 0,
        InvalidSourceVersion = 1,
        SourceNewerThanRuntime = 2,
        MissingSequentialStep = 3,
        UnknownTrack = 4,
    }

    public readonly struct RuntimeReplayMigrationPathAssessment
    {
        public readonly uint SourceVersion;
        public readonly uint TargetVersion;
        public readonly RuntimeReplayMigrationFailureKind FailureKind;
        public readonly string Diagnostic;

        public bool CanMigrate =>
            FailureKind == RuntimeReplayMigrationFailureKind.None;
        public bool RequiresMigration =>
            CanMigrate && SourceVersion != TargetVersion;

        public RuntimeReplayMigrationPathAssessment(
            uint sourceVersion,
            uint targetVersion,
            RuntimeReplayMigrationFailureKind failureKind,
            string diagnostic)
        {
            SourceVersion = sourceVersion;
            TargetVersion = targetVersion;
            FailureKind = failureKind;
            Diagnostic = diagnostic ?? string.Empty;
        }
    }

    public class RuntimeReplayMigrationException :
        InvalidOperationException
    {
        public readonly RuntimeReplayMigrationFailureKind FailureKind;
        public readonly string RegistryId;
        public readonly uint SourceVersion;
        public readonly uint TargetVersion;

        public RuntimeReplayMigrationException(
            RuntimeReplayMigrationFailureKind failureKind,
            string registryId,
            uint sourceVersion,
            uint targetVersion,
            string message) :
            base(message)
        {
            FailureKind = failureKind;
            RegistryId = registryId ?? string.Empty;
            SourceVersion = sourceVersion;
            TargetVersion = targetVersion;
        }
    }

    public class RuntimeReplayVersionMigrationStep
    {
        public readonly uint FromVersion;
        public readonly uint ToVersion;
        public readonly RuntimeReplayBinaryMigration Migration;

        public RuntimeReplayVersionMigrationStep(
            uint fromVersion,
            uint toVersion,
            RuntimeReplayBinaryMigration migration)
        {
            if (fromVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fromVersion));
            }
            if (toVersion != fromVersion + 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(toVersion),
                    "Replay migrations must advance exactly one version.");
            }

            FromVersion = fromVersion;
            ToVersion = toVersion;
            Migration = migration
                        ?? throw new ArgumentNullException(nameof(migration));
        }
    }

    public class RuntimeReplayVersionMigrationRegistry
    {
        private readonly Dictionary<uint, RuntimeReplayVersionMigrationStep>
            _steps = new();

        private readonly string _registryId;
        private bool _sealed;

        public readonly uint CurrentVersion;

        public bool IsSealed => _sealed;
        public int MigrationCount => _steps.Count;
        public string SchemaHash { get; private set; }

        public RuntimeReplayVersionMigrationRegistry(
            string registryId,
            uint currentVersion)
        {
            RuntimeReplayId.Validate(registryId, nameof(registryId));
            if (currentVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(currentVersion));
            }

            _registryId = registryId;
            CurrentVersion = currentVersion;
        }

        public void Register(
            uint fromVersion,
            uint toVersion,
            RuntimeReplayBinaryMigration migration)
        {
            ThrowIfSealed();
            var step = new RuntimeReplayVersionMigrationStep(
                fromVersion,
                toVersion,
                migration);
            if (step.ToVersion > CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"Replay migration {fromVersion}->{toVersion} advances past current version {CurrentVersion}.");
            }
            if (!_steps.TryAdd(fromVersion, step))
            {
                throw new InvalidOperationException(
                    $"Replay migration from version {fromVersion} is registered twice.");
            }
        }

        public void Seal()
        {
            ThrowIfSealed();
            using var writer = new RuntimeReplayCheckpointWriter();
            writer.WriteString("cms.runtime-replay.version-migrations");
            writer.WriteString(_registryId);
            writer.WriteUInt32(CurrentVersion);
            var steps = _steps.Values
                .OrderBy(value => value.FromVersion)
                .ToArray();
            writer.WriteInt32(steps.Length);
            for (var i = 0; i < steps.Length; i++)
            {
                writer.WriteUInt32(steps[i].FromVersion);
                writer.WriteUInt32(steps[i].ToVersion);
            }
            SchemaHash = RuntimeReplayHash.ToHex(
                RuntimeReplayHash.CalculateSha256(writer.ToArray()));
            _sealed = true;
        }

        public byte[] Migrate(uint sourceVersion, byte[] payload)
        {
            RequireSealed();
            var assessment = Assess(sourceVersion);
            if (!assessment.CanMigrate)
            {
                throw new RuntimeReplayMigrationException(
                    assessment.FailureKind,
                    _registryId,
                    sourceVersion,
                    CurrentVersion,
                    assessment.Diagnostic);
            }
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            var version = sourceVersion;
            var current = (byte[])payload.Clone();
            while (version < CurrentVersion)
            {
                if (!_steps.TryGetValue(version, out var step))
                {
                    throw new InvalidOperationException(
                        $"Replay migration registry '{_registryId}' has no step "
                        + $"from version {version} to {version + 1}.");
                }

                using var reader = new RuntimeReplayCheckpointReader(current);
                using var writer = new RuntimeReplayCheckpointWriter();
                step.Migration(reader, writer);
                reader.RequireEnd();
                current = writer.ToArray();
                version = step.ToVersion;
            }
            return current;
        }

        public RuntimeReplayMigrationPathAssessment Assess(
            uint sourceVersion)
        {
            RequireSealed();
            if (sourceVersion == 0)
            {
                return new RuntimeReplayMigrationPathAssessment(
                    sourceVersion,
                    CurrentVersion,
                    RuntimeReplayMigrationFailureKind
                        .InvalidSourceVersion,
                    $"Replay migration registry '{_registryId}' cannot "
                    + "migrate version 0.");
            }
            if (sourceVersion > CurrentVersion)
            {
                return new RuntimeReplayMigrationPathAssessment(
                    sourceVersion,
                    CurrentVersion,
                    RuntimeReplayMigrationFailureKind
                        .SourceNewerThanRuntime,
                    $"Replay version {sourceVersion} is newer than "
                    + $"runtime version {CurrentVersion} in registry "
                    + $"'{_registryId}'.");
            }

            var version = sourceVersion;
            while (version < CurrentVersion)
            {
                if (!_steps.TryGetValue(version, out var step))
                {
                    return new RuntimeReplayMigrationPathAssessment(
                        sourceVersion,
                        CurrentVersion,
                        RuntimeReplayMigrationFailureKind
                            .MissingSequentialStep,
                        $"Replay migration registry '{_registryId}' has "
                        + $"no step from version {version} to "
                        + $"{version + 1}.");
                }
                version = step.ToVersion;
            }
            return new RuntimeReplayMigrationPathAssessment(
                sourceVersion,
                CurrentVersion,
                RuntimeReplayMigrationFailureKind.None,
                sourceVersion == CurrentVersion
                    ? $"Replay registry '{_registryId}' is current."
                    : $"Replay registry '{_registryId}' can migrate "
                    + $"{sourceVersion} to {CurrentVersion}.");
        }

        private void ThrowIfSealed()
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    $"Replay migration registry '{_registryId}' is already sealed.");
            }
        }

        private void RequireSealed()
        {
            if (!_sealed)
            {
                throw new InvalidOperationException(
                    $"Replay migration registry '{_registryId}' must be sealed before use.");
            }
        }
    }

    public class RuntimeReplayContainerMigrationRegistry
    {
        private readonly RuntimeReplayVersionMigrationRegistry _registry;

        public uint CurrentVersion => _registry.CurrentVersion;
        public bool IsSealed => _registry.IsSealed;
        public string SchemaHash => _registry.SchemaHash;

        public RuntimeReplayContainerMigrationRegistry(uint currentVersion)
        {
            _registry = new RuntimeReplayVersionMigrationRegistry(
                "dgr.container",
                currentVersion);
        }

        public void Register(
            uint fromVersion,
            uint toVersion,
            RuntimeReplayBinaryMigration migration)
        {
            _registry.Register(fromVersion, toVersion, migration);
        }

        public void Seal()
        {
            _registry.Seal();
        }

        public byte[] Migrate(uint sourceVersion, byte[] payload)
        {
            return _registry.Migrate(sourceVersion, payload);
        }

        public RuntimeReplayMigrationPathAssessment Assess(
            uint sourceVersion)
        {
            return _registry.Assess(sourceVersion);
        }
    }

    public class RuntimeReplayTrackMigrationRegistry
    {
        private readonly Dictionary<string, RuntimeReplayVersionMigrationRegistry>
            _tracks = new(StringComparer.Ordinal);

        private bool _sealed;

        public bool IsSealed => _sealed;
        public int TrackCount => _tracks.Count;
        public string SchemaHash { get; private set; }

        public void RegisterTrack(string trackId, uint currentVersion)
        {
            ThrowIfSealed();
            RuntimeReplayId.Validate(trackId, nameof(trackId));
            if (!_tracks.TryAdd(
                    trackId,
                    new RuntimeReplayVersionMigrationRegistry(
                        $"dgr.track/{trackId}",
                        currentVersion)))
            {
                throw new InvalidOperationException(
                    $"Replay track '{trackId}' is registered twice.");
            }
        }

        public void RegisterMigration(
            string trackId,
            uint fromVersion,
            uint toVersion,
            RuntimeReplayBinaryMigration migration)
        {
            ThrowIfSealed();
            if (!_tracks.TryGetValue(trackId, out var registry))
            {
                throw new InvalidOperationException(
                    $"Replay track '{trackId}' is not registered.");
            }
            registry.Register(fromVersion, toVersion, migration);
        }

        public void Seal()
        {
            ThrowIfSealed();
            var tracks = _tracks
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray();
            using var writer = new RuntimeReplayCheckpointWriter();
            writer.WriteString("cms.runtime-replay.track-migrations");
            writer.WriteUInt32(1);
            writer.WriteInt32(tracks.Length);
            for (var i = 0; i < tracks.Length; i++)
            {
                tracks[i].Value.Seal();
                writer.WriteString(tracks[i].Key);
                writer.WriteUInt32(tracks[i].Value.CurrentVersion);
                writer.WriteString(tracks[i].Value.SchemaHash);
            }
            SchemaHash = RuntimeReplayHash.ToHex(
                RuntimeReplayHash.CalculateSha256(writer.ToArray()));
            _sealed = true;
        }

        public byte[] Migrate(
            string trackId,
            uint sourceVersion,
            byte[] payload)
        {
            RequireSealed();
            if (!_tracks.TryGetValue(trackId, out var registry))
            {
                throw new InvalidOperationException(
                    $"Replay track '{trackId}' is not registered.");
            }
            return registry.Migrate(sourceVersion, payload);
        }

        public RuntimeReplayMigrationPathAssessment Assess(
            string trackId,
            uint sourceVersion)
        {
            RequireSealed();
            if (!_tracks.TryGetValue(trackId, out var registry))
            {
                return new RuntimeReplayMigrationPathAssessment(
                    sourceVersion,
                    0,
                    RuntimeReplayMigrationFailureKind.UnknownTrack,
                    $"Replay track '{trackId}' is not registered.");
            }
            return registry.Assess(sourceVersion);
        }

        public uint GetCurrentVersion(string trackId)
        {
            RequireSealed();
            if (!_tracks.TryGetValue(trackId, out var registry))
            {
                throw new InvalidOperationException(
                    $"Replay track '{trackId}' is not registered.");
            }
            return registry.CurrentVersion;
        }

        public string GetTrackSchemaHash(string trackId)
        {
            RequireSealed();
            if (!_tracks.TryGetValue(trackId, out var registry))
            {
                throw new InvalidOperationException(
                    $"Replay track '{trackId}' is not registered.");
            }
            return registry.SchemaHash;
        }

        private void ThrowIfSealed()
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "Replay track migration registry is already sealed.");
            }
        }

        private void RequireSealed()
        {
            if (!_sealed)
            {
                throw new InvalidOperationException(
                    "Replay track migration registry must be sealed before use.");
            }
        }
    }
}
