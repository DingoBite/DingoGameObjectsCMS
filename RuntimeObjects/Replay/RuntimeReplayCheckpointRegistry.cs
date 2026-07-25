using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public readonly struct RuntimeReplayCheckpointMigrationKey :
        IEquatable<RuntimeReplayCheckpointMigrationKey>
    {
        public readonly uint SectionId;
        public readonly uint FromVersion;

        public RuntimeReplayCheckpointMigrationKey(
            uint sectionId,
            uint fromVersion)
        {
            if (fromVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fromVersion));
            }

            SectionId = sectionId;
            FromVersion = fromVersion;
        }

        public bool Equals(RuntimeReplayCheckpointMigrationKey other)
        {
            return SectionId == other.SectionId
                   && FromVersion == other.FromVersion;
        }

        public override bool Equals(object obj)
        {
            return obj is RuntimeReplayCheckpointMigrationKey other
                   && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int)SectionId * 397) ^ (int)FromVersion;
            }
        }
    }

    public class RuntimeReplayCheckpointMigrationStep
    {
        public readonly uint SectionId;
        public readonly uint FromVersion;
        public readonly uint ToVersion;
        public readonly RuntimeReplayBinaryMigration Migration;

        public RuntimeReplayCheckpointMigrationStep(
            uint sectionId,
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
                    "Checkpoint migrations must advance exactly one version.");
            }

            SectionId = sectionId;
            FromVersion = fromVersion;
            ToVersion = toVersion;
            Migration = migration
                        ?? throw new ArgumentNullException(nameof(migration));
        }
    }

    public class RuntimeReplayCheckpointPreparedSection
    {
        public readonly uint SectionId;
        public readonly uint SectionVersion;
        public readonly byte[] Payload;

        public RuntimeReplayCheckpointPreparedSection(
            uint sectionId,
            uint sectionVersion,
            byte[] payload)
        {
            if (sectionVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sectionVersion));
            }
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }

            SectionId = sectionId;
            SectionVersion = sectionVersion;
            Payload = (byte[])payload.Clone();
        }
    }

    public class RuntimeReplayCheckpointRestorePlan
    {
        public readonly RuntimeReplayCheckpointRegistry Registry;
        public readonly long CompletedTick;
        public readonly ulong Cursor;
        public readonly string SourceSchemaHash;
        public readonly string TargetSchemaHash;
        public readonly IReadOnlyList<RuntimeReplayCheckpointPreparedSection>
            Sections;

        public RuntimeReplayCheckpointRestorePlan(
            RuntimeReplayCheckpointRegistry registry,
            long completedTick,
            ulong cursor,
            string sourceSchemaHash,
            string targetSchemaHash,
            IReadOnlyList<RuntimeReplayCheckpointPreparedSection> sections)
        {
            Registry = registry
                       ?? throw new ArgumentNullException(nameof(registry));
            if (completedTick < -1)
            {
                throw new ArgumentOutOfRangeException(nameof(completedTick));
            }
            if (!RuntimeReplayHash.IsSha256Hex(sourceSchemaHash)
                || !RuntimeReplayHash.IsSha256Hex(targetSchemaHash))
            {
                throw new ArgumentException(
                    "Restore plan schema hashes must be SHA-256 hex strings.");
            }
            if (sections == null)
            {
                throw new ArgumentNullException(nameof(sections));
            }

            var copy =
                new RuntimeReplayCheckpointPreparedSection[sections.Count];
            for (var i = 0; i < sections.Count; i++)
            {
                copy[i] = sections[i]
                          ?? throw new ArgumentException(
                              $"Prepared section {i} is null.",
                              nameof(sections));
            }

            CompletedTick = completedTick;
            Cursor = cursor;
            SourceSchemaHash = sourceSchemaHash.ToLowerInvariant();
            TargetSchemaHash = targetSchemaHash.ToLowerInvariant();
            Sections = Array.AsReadOnly(copy);
        }
    }

    public class RuntimeReplayCheckpointRegistry
    {
        private readonly Dictionary<uint, IRuntimeReplayCheckpointParticipant>
            _participants = new();

        private readonly Dictionary<
                RuntimeReplayCheckpointMigrationKey,
                RuntimeReplayCheckpointMigrationStep>
            _migrations = new();

        private IRuntimeReplayCheckpointParticipant[] _orderedParticipants =
            Array.Empty<IRuntimeReplayCheckpointParticipant>();

        private IReadOnlyDictionary<uint, string> _sectionSchemaHashes =
            new ReadOnlyDictionary<uint, string>(
                new Dictionary<uint, string>());

        private bool _sealed;

        public bool IsSealed => _sealed;
        public string SchemaHash { get; private set; }
        public string CheckpointSchemaHash => SchemaHash;
        public IReadOnlyDictionary<uint, string> SectionSchemaHashes =>
            _sectionSchemaHashes;
        public int ParticipantCount => _participants.Count;
        public int MigrationCount => _migrations.Count;

        public void RegisterParticipant(
            IRuntimeReplayCheckpointParticipant participant)
        {
            ThrowIfSealed();
            if (participant == null)
            {
                throw new ArgumentNullException(nameof(participant));
            }

            if (participant.CurrentVersion == 0)
            {
                throw new InvalidOperationException(
                    $"Checkpoint section '{participant.SectionId}' has version zero.");
            }
            if (!_participants.TryAdd(participant.SectionId, participant))
            {
                throw new InvalidOperationException(
                    $"Checkpoint section '{participant.SectionId}' is registered twice.");
            }
        }

        public void RegisterMigration(
            uint sectionId,
            uint fromVersion,
            uint toVersion,
            RuntimeReplayBinaryMigration migration)
        {
            ThrowIfSealed();
            var step = new RuntimeReplayCheckpointMigrationStep(
                sectionId,
                fromVersion,
                toVersion,
                migration);
            var key = new RuntimeReplayCheckpointMigrationKey(
                sectionId,
                fromVersion);
            if (!_migrations.TryAdd(key, step))
            {
                throw new InvalidOperationException(
                    $"Checkpoint section '{sectionId}' already has a migration from version {fromVersion}.");
            }
        }

        public void Seal()
        {
            ThrowIfSealed();
            _orderedParticipants = _participants.Values
                .OrderBy(value => value.SectionId)
                .ToArray();

            foreach (var step in _migrations.Values)
            {
                if (!_participants.TryGetValue(
                        step.SectionId,
                        out var participant))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint migration targets unregistered section '{step.SectionId}'.");
                }
                if (step.ToVersion > participant.CurrentVersion)
                {
                    throw new InvalidOperationException(
                        $"Checkpoint migration '{step.SectionId}' {step.FromVersion}->{step.ToVersion} "
                        + $"advances past current version {participant.CurrentVersion}.");
                }
            }

            var sectionHashes = CalculateSectionSchemaHashes();
            _sectionSchemaHashes =
                new ReadOnlyDictionary<uint, string>(sectionHashes);
            SchemaHash = CalculateSchemaHash(sectionHashes);
            _sealed = true;
        }

        public RuntimeReplayCheckpointEnvelope Capture(
            long completedTick,
            ulong cursor)
        {
            RequireSealed();
            var sections =
                new RuntimeReplayCheckpointSection[_orderedParticipants.Length];
            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                var participant = _orderedParticipants[i];
                using var writer = new RuntimeReplayCheckpointWriter(
                    maxBytes: RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                participant.Capture(writer);
                var payload = writer.ToArray();
                sections[i] = new RuntimeReplayCheckpointSection(
                    participant.SectionId,
                    participant.CurrentVersion,
                    payload,
                    RuntimeReplayHash.CalculateSha256(payload));
            }

            return RuntimeReplayCheckpointCodec.Create(
                completedTick,
                cursor,
                SchemaHash,
                sections);
        }

        public RuntimeReplayCheckpointFingerprintEnvelope
            CaptureFingerprint(long completedTick)
        {
            RequireSealed();
            var sections =
                new RuntimeReplayCheckpointFingerprintSection[
                    _orderedParticipants.Length];
            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                var participant = _orderedParticipants[i];
                using var writer =
                    new RuntimeReplayCheckpointWriter(
                        maxBytes:
                        RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                participant.AppendFingerprint(writer);
                sections[i] =
                    new RuntimeReplayCheckpointFingerprintSection(
                        participant.SectionId,
                        RuntimeReplayHash.CalculateSha256(
                            writer.ToArray()));
            }

            return RuntimeReplayCheckpointFingerprintCodec.Create(
                completedTick,
                SchemaHash,
                sections);
        }

        public RuntimeReplayCheckpointRestorePlan Prevalidate(
            RuntimeReplayCheckpointEnvelope checkpoint)
        {
            RequireSealed();
            RuntimeReplayCheckpointCodec.Validate(checkpoint);
            if (checkpoint.Sections.Count != _orderedParticipants.Length)
            {
                throw new InvalidOperationException(
                    $"Checkpoint has {checkpoint.Sections.Count} sections, "
                    + $"but the active registry requires {_orderedParticipants.Length}.");
            }

            var requiresVersionMigration = false;
            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                var participant = _orderedParticipants[i];
                var source = checkpoint.Sections[i];
                if (source.SectionId != participant.SectionId)
                {
                    throw new InvalidOperationException(
                        $"Checkpoint section {i} is '{source.SectionId}', "
                        + $"expected '{participant.SectionId}'.");
                }
                ValidateMigrationPath(
                    source,
                    participant.CurrentVersion);
                requiresVersionMigration |=
                    source.SectionVersion
                    != participant.CurrentVersion;
            }
            if (!requiresVersionMigration
                && !string.Equals(
                    checkpoint.SchemaHash,
                    SchemaHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Checkpoint schema hash does not match the active registry.");
            }

            var prepared =
                new RuntimeReplayCheckpointPreparedSection[
                    _orderedParticipants.Length];
            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                var participant = _orderedParticipants[i];
                var source = checkpoint.Sections[i];
                var payload = MigrateToCurrentVersion(
                    source,
                    participant.CurrentVersion);
                if (participant is IRuntimeReplayCheckpointPrevalidator
                    prevalidator)
                {
                    using var reader = new RuntimeReplayCheckpointReader(
                        payload,
                        RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                    prevalidator.Prevalidate(reader);
                    reader.RequireEnd();
                }

                prepared[i] = new RuntimeReplayCheckpointPreparedSection(
                    participant.SectionId,
                    participant.CurrentVersion,
                    payload);
            }

            var validationContext =
                new RuntimeReplayCheckpointValidationContext();
            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                if (_orderedParticipants[i]
                    is not
                    IRuntimeReplayCheckpointValidationContextContributor
                    contributor)
                {
                    continue;
                }

                using var reader = new RuntimeReplayCheckpointReader(
                    prepared[i].Payload,
                    RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                contributor.ContributeValidationContext(
                    reader,
                    validationContext);
                reader.RequireEnd();
            }

            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                if (_orderedParticipants[i]
                    is not IRuntimeReplayCheckpointContextPrevalidator
                    prevalidator)
                {
                    continue;
                }

                using var reader = new RuntimeReplayCheckpointReader(
                    prepared[i].Payload,
                    RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                prevalidator.Prevalidate(
                    reader,
                    validationContext);
                reader.RequireEnd();
            }

            return new RuntimeReplayCheckpointRestorePlan(
                this,
                checkpoint.CompletedTick,
                checkpoint.Cursor,
                checkpoint.SchemaHash,
                SchemaHash,
                prepared);
        }

        public void Restore(RuntimeReplayCheckpointEnvelope checkpoint)
        {
            Restore(Prevalidate(checkpoint));
        }

        public void Restore(RuntimeReplayCheckpointRestorePlan plan)
        {
            RequireSealed();
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            if (!ReferenceEquals(plan.Registry, this)
                || !string.Equals(
                    plan.TargetSchemaHash,
                    SchemaHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Checkpoint restore plan belongs to a different registry schema.");
            }
            if (plan.Sections.Count != _orderedParticipants.Length)
            {
                throw new InvalidOperationException(
                    "Checkpoint restore plan section count changed after prevalidation.");
            }

            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                var participant = _orderedParticipants[i];
                var section = plan.Sections[i];
                if (section.SectionId != participant.SectionId
                    || section.SectionVersion != participant.CurrentVersion)
                {
                    throw new InvalidOperationException(
                        $"Prepared checkpoint section {i} no longer matches participant '{participant.SectionId}'.");
                }

                using var reader = new RuntimeReplayCheckpointReader(
                    section.Payload,
                    RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                participant.Restore(reader);
                reader.RequireEnd();
            }
        }

        private void ValidateMigrationPath(
            RuntimeReplayCheckpointSection source,
            uint targetVersion)
        {
            if (source.SectionVersion > targetVersion)
            {
                throw new InvalidOperationException(
                    $"Checkpoint section '{source.SectionId}' version {source.SectionVersion} "
                    + $"is newer than supported version {targetVersion}.");
            }

            var version = source.SectionVersion;
            while (version < targetVersion)
            {
                var key = new RuntimeReplayCheckpointMigrationKey(
                    source.SectionId,
                    version);
                if (!_migrations.TryGetValue(key, out var step))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint section '{source.SectionId}' has no migration "
                        + $"from version {version} to {version + 1}.");
                }
                version = step.ToVersion;
            }
        }

        private byte[] MigrateToCurrentVersion(
            RuntimeReplayCheckpointSection source,
            uint targetVersion)
        {
            if (source.SectionVersion > targetVersion)
            {
                throw new InvalidOperationException(
                    $"Checkpoint section '{source.SectionId}' version {source.SectionVersion} "
                    + $"is newer than supported version {targetVersion}.");
            }

            var version = source.SectionVersion;
            var payload = (byte[])source.Payload.Clone();
            while (version < targetVersion)
            {
                var key = new RuntimeReplayCheckpointMigrationKey(
                    source.SectionId,
                    version);
                if (!_migrations.TryGetValue(key, out var step))
                {
                    throw new InvalidOperationException(
                        $"Checkpoint section '{source.SectionId}' has no migration "
                        + $"from version {version} to {version + 1}.");
                }

                using var reader = new RuntimeReplayCheckpointReader(
                    payload,
                    RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                using var writer = new RuntimeReplayCheckpointWriter(
                    maxBytes: RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                step.Migration(reader, writer);
                reader.RequireEnd();
                payload = writer.ToArray();
                version = step.ToVersion;
            }
            return payload;
        }

        private Dictionary<uint, string> CalculateSectionSchemaHashes()
        {
            var result = new Dictionary<uint, string>(
                _orderedParticipants.Length);
            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                var participant = _orderedParticipants[i];
                using var sectionWriter =
                    new RuntimeReplayCheckpointWriter();
                sectionWriter.WriteString(
                    "cms.runtime-replay.checkpoint-section");
                sectionWriter.WriteUInt32(1);
                sectionWriter.WriteUInt32(participant.SectionId);
                sectionWriter.WriteUInt32(participant.CurrentVersion);
                sectionWriter.WriteString(
                    GetParticipantTypeKey(participant));

                var migrations = _migrations.Values
                    .Where(value =>
                        value.SectionId == participant.SectionId)
                    .OrderBy(value => value.FromVersion)
                    .ToArray();
                sectionWriter.WriteInt32(migrations.Length);
                for (var migrationIndex = 0;
                     migrationIndex < migrations.Length;
                     migrationIndex++)
                {
                    sectionWriter.WriteUInt32(
                        migrations[migrationIndex].FromVersion);
                    sectionWriter.WriteUInt32(
                        migrations[migrationIndex].ToVersion);
                }

                result.Add(
                    participant.SectionId,
                    RuntimeReplayHash.ToHex(
                        RuntimeReplayHash.CalculateSha256(
                            sectionWriter.ToArray())));
            }
            return result;
        }

        private static string GetParticipantTypeKey(
            IRuntimeReplayCheckpointParticipant participant)
        {
            var type = participant.GetType();
            var assemblyName =
                type.Assembly.GetName().Name
                ?? string.Empty;
            var typeName = type.FullName ?? type.Name;
            return assemblyName + ":" + typeName;
        }

        private string CalculateSchemaHash(
            IReadOnlyDictionary<uint, string> sectionHashes)
        {
            using var writer = new RuntimeReplayCheckpointWriter();
            writer.WriteString(
                "cms.runtime-replay.checkpoint-registry");
            writer.WriteUInt32(1);
            writer.WriteInt32(_orderedParticipants.Length);
            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                var participant = _orderedParticipants[i];
                writer.WriteUInt32(participant.SectionId);
                writer.WriteUInt32(participant.CurrentVersion);
                writer.WriteString(
                    sectionHashes[participant.SectionId]);
            }
            return RuntimeReplayHash.ToHex(
                RuntimeReplayHash.CalculateSha256(writer.ToArray()));
        }

        private void ThrowIfSealed()
        {
            if (_sealed)
            {
                throw new InvalidOperationException(
                    "Checkpoint registry is already sealed.");
            }
        }

        private void RequireSealed()
        {
            if (!_sealed)
            {
                throw new InvalidOperationException(
                    "Checkpoint registry must be sealed before use.");
            }
        }
    }
}
