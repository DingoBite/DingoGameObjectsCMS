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
        public readonly int PayloadLength;
        public readonly IReadOnlyList<RuntimeReplayCheckpointPage> Pages;

        public byte[] Payload => CopyPayload();

        public RuntimeReplayCheckpointPreparedSection(
            uint sectionId,
            uint sectionVersion,
            byte[] payload)
            : this(
                sectionId,
                sectionVersion,
                RuntimeReplayCheckpointPageUtils.Split(payload),
                payload?.Length ?? 0)
        {
        }

        public RuntimeReplayCheckpointPreparedSection(
            uint sectionId,
            uint sectionVersion,
            IReadOnlyList<RuntimeReplayCheckpointPage> pages,
            int payloadLength)
        {
            if (sectionVersion == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sectionVersion));
            }
            if (pages == null)
            {
                throw new ArgumentNullException(nameof(pages));
            }
            if (payloadLength < 0
                || payloadLength > RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES)
            {
                throw new ArgumentOutOfRangeException(nameof(payloadLength));
            }

            SectionId = sectionId;
            SectionVersion = sectionVersion;
            PayloadLength = payloadLength;
            if (pages.Count == 0
                || pages.Count
                > RuntimeReplayCheckpointCodec.MAX_PAGES_PER_SECTION)
            {
                throw new ArgumentOutOfRangeException(nameof(pages));
            }
            var copy = new RuntimeReplayCheckpointPage[pages.Count];
            var actualLength = 0;
            for (var i = 0; i < pages.Count; i++)
            {
                copy[i] = pages[i]
                          ?? throw new ArgumentException(
                              $"Prepared checkpoint page {i} is null.",
                              nameof(pages));
                if (copy[i].PageIndex != i
                    || i + 1 < pages.Count
                    && copy[i].PayloadLength
                    != RuntimeReplayCheckpointCodec.PAGE_BYTES)
                {
                    throw new ArgumentException(
                        $"Prepared checkpoint page {i} has invalid layout.",
                        nameof(pages));
                }
                actualLength = checked(
                    actualLength + copy[i].PayloadLength);
            }
            if (actualLength != payloadLength)
            {
                throw new ArgumentException(
                    $"Prepared checkpoint pages contain {actualLength} bytes, expected {payloadLength}.",
                    nameof(payloadLength));
            }
            Pages = Array.AsReadOnly(copy);
        }

        public byte[] CopyPayload()
        {
            var result = new byte[PayloadLength];
            var offset = 0;
            for (var i = 0; i < Pages.Count; i++)
            {
                var payload = Pages[i].UnsafePayload;
                if (payload.Length > 0)
                {
                    Buffer.BlockCopy(
                        payload,
                        0,
                        result,
                        offset,
                        payload.Length);
                }
                offset += payload.Length;
            }
            return result;
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

        public bool TryTakeParticipant<TParticipant>(
            out TParticipant participant)
            where TParticipant : class, IRuntimeReplayCheckpointParticipant
        {
            RequireSealed();
            for (var i = 0; i < _orderedParticipants.Length; i++)
            {
                if (_orderedParticipants[i] is not TParticipant candidate)
                {
                    continue;
                }

                participant = candidate;
                return true;
            }

            participant = null;
            return false;
        }

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
                var pages = writer.ToPages();
                sections[i] = new RuntimeReplayCheckpointSection(
                    participant.SectionId,
                    participant.CurrentVersion,
                    pages,
                    writer.Length,
                    RuntimeReplayHash.CalculateSha256(
                        pages,
                        writer.Length));
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
                var pages = writer.ToPages();
                sections[i] =
                    new RuntimeReplayCheckpointFingerprintSection(
                        participant.SectionId,
                        RuntimeReplayHash.CalculateSha256(
                            pages,
                            writer.Length));
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
                var preparedSection = MigrateToCurrentVersion(
                    source,
                    participant.CurrentVersion);
                if (participant is IRuntimeReplayCheckpointPrevalidator
                    prevalidator)
                {
                    using var reader = new RuntimeReplayCheckpointReader(
                        preparedSection.Pages,
                        preparedSection.PayloadLength,
                        RuntimeReplayCheckpointCodec.MAX_SECTION_BYTES);
                    prevalidator.Prevalidate(reader);
                    reader.RequireEnd();
                }

                prepared[i] = preparedSection;
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
                    prepared[i].Pages,
                    prepared[i].PayloadLength,
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
                    prepared[i].Pages,
                    prepared[i].PayloadLength,
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
                    section.Pages,
                    section.PayloadLength,
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

        private RuntimeReplayCheckpointPreparedSection
            MigrateToCurrentVersion(
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
            if (version == targetVersion)
            {
                return new RuntimeReplayCheckpointPreparedSection(
                    source.SectionId,
                    source.SectionVersion,
                    source.Pages,
                    source.PayloadLength);
            }

            var payload = source.CopyPayload();
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
            return new RuntimeReplayCheckpointPreparedSection(
                source.SectionId,
                targetVersion,
                payload);
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
                sectionWriter.WriteUInt32(2);
                sectionWriter.WriteUInt32(participant.SectionId);
                sectionWriter.WriteUInt32(participant.CurrentVersion);
                sectionWriter.WriteString(
                    GetParticipantTypeKey(participant));

                if (participant
                    is IRuntimeReplayCheckpointSchemaFingerprintContributor
                    schemaContributor)
                {
                    sectionWriter.WriteBoolean(true);
                    var schemaBlock =
                        sectionWriter.BeginLengthPrefixedBlock();
                    schemaContributor.AppendCheckpointSchemaFingerprint(
                        sectionWriter);
                    sectionWriter.EndLengthPrefixedBlock(schemaBlock);
                }
                else
                {
                    sectionWriter.WriteBoolean(false);
                }

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
            writer.WriteUInt32(2);
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
