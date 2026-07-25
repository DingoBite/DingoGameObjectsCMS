using System;
using System.IO;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class ReplayCheckpointTestParticipant6F31 :
        IRuntimeReplayCheckpointParticipant,
        IRuntimeReplayCheckpointPrevalidator
    {
        private readonly uint _sectionId;
        private readonly uint _currentVersion;

        public int Value;
        public int FingerprintValue;
        public int CaptureCalls;
        public int FingerprintCalls;
        public int PrevalidateCalls;
        public int RestoreCalls;
        public bool FailPrevalidation;

        public uint SectionId => _sectionId;
        public uint CurrentVersion => _currentVersion;

        public ReplayCheckpointTestParticipant6F31(
            uint sectionId,
            uint currentVersion,
            int value,
            int fingerprintValue)
        {
            _sectionId = sectionId;
            _currentVersion = currentVersion;
            Value = value;
            FingerprintValue = fingerprintValue;
        }

        public void Capture(RuntimeReplayCheckpointWriter writer)
        {
            CaptureCalls++;
            writer.WriteInt32(Value);
        }

        public void Restore(RuntimeReplayCheckpointReader reader)
        {
            RestoreCalls++;
            Value = reader.ReadInt32();
        }

        public void AppendFingerprint(
            RuntimeReplayCheckpointWriter writer)
        {
            FingerprintCalls++;
            writer.WriteString("test.int32");
            writer.WriteInt32(FingerprintValue);
        }

        public void Prevalidate(RuntimeReplayCheckpointReader reader)
        {
            PrevalidateCalls++;
            reader.ReadInt32();
            if (FailPrevalidation)
            {
                throw new FormatException(
                    "Intentional checkpoint prevalidation failure.");
            }
        }
    }

    public class RuntimeReplayPersistenceCoreV1_6F31Tests
    {
        private string _temporaryDirectory;

        [SetUp]
        public void SetUp()
        {
            _temporaryDirectory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DgrReplayPersistenceCoreV1_6F31",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_temporaryDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(
                    _temporaryDirectory,
                    recursive: true);
            }
        }

        [Test]
        public void Registry_CaptureCodecRestore_UsesNumericOrderedSections()
        {
            var second = new ReplayCheckpointTestParticipant6F31(
                sectionId: 20,
                currentVersion: 1,
                value: 220,
                fingerprintValue: 2);
            var first = new ReplayCheckpointTestParticipant6F31(
                sectionId: 0,
                currentVersion: 1,
                value: 110,
                fingerprintValue: 1);
            var registry = new RuntimeReplayCheckpointRegistry();
            registry.RegisterParticipant(second);
            registry.RegisterParticipant(first);
            registry.Seal();

            var checkpoint = registry.Capture(
                completedTick: 45,
                cursor: 91);
            Assert.That(checkpoint.Sections.Count, Is.EqualTo(2));
            Assert.That(
                checkpoint.Sections[0].SectionId,
                Is.EqualTo(0u));
            Assert.That(
                checkpoint.Sections[1].SectionId,
                Is.EqualTo(20u));
            Assert.That(first.CaptureCalls, Is.EqualTo(1));
            Assert.That(second.CaptureCalls, Is.EqualTo(1));
            Assert.That(first.FingerprintCalls, Is.Zero);
            Assert.That(second.FingerprintCalls, Is.Zero);
            Assert.That(
                registry.SectionSchemaHashes[0],
                Has.Length.EqualTo(
                    RuntimeReplayHash.SHA256_HEX_CHARS));

            var encoded =
                RuntimeReplayCheckpointCodec.Encode(checkpoint);
            var decoded =
                RuntimeReplayCheckpointCodec.Decode(encoded);
            first.Value = 0;
            second.Value = 0;
            registry.Restore(decoded);

            Assert.That(first.Value, Is.EqualTo(110));
            Assert.That(second.Value, Is.EqualTo(220));
            Assert.That(decoded.CompletedTick, Is.EqualTo(45));
            Assert.That(decoded.Cursor, Is.EqualTo(91ul));

            var wrongSchema =
                RuntimeReplayCheckpointCodec.Create(
                    decoded.CompletedTick,
                    decoded.Cursor,
                    new string('f', 64),
                    decoded.Sections);
            Assert.Throws<InvalidOperationException>(
                () => registry.Restore(wrongSchema));

            encoded[encoded.Length - 1] ^= 0x5a;
            Assert.Throws<InvalidOperationException>(
                () => RuntimeReplayCheckpointCodec.Decode(encoded));
        }

        [Test]
        public void Registry_StateFingerprintsDoNotChangeSchema()
        {
            var firstRegistry =
                CreateFingerprintRegistry6F31(
                    section10Fingerprint: 100,
                    section20Fingerprint: 200);
            var secondRegistry =
                CreateFingerprintRegistry6F31(
                    section10Fingerprint: 100,
                    section20Fingerprint: 201);

            Assert.That(
                firstRegistry.SectionSchemaHashes[10],
                Is.EqualTo(
                    secondRegistry.SectionSchemaHashes[10]));
            Assert.That(
                firstRegistry.SectionSchemaHashes[20],
                Is.EqualTo(
                    secondRegistry.SectionSchemaHashes[20]));
            Assert.That(
                firstRegistry.SchemaHash,
                Is.EqualTo(secondRegistry.SchemaHash));

            var firstFingerprint =
                firstRegistry.CaptureFingerprint(
                    completedTick: 70);
            var secondFingerprint =
                secondRegistry.CaptureFingerprint(
                    completedTick: 70);
            Assert.That(
                firstFingerprint.Sections[0].FingerprintHash,
                Is.EqualTo(
                    secondFingerprint.Sections[0]
                        .FingerprintHash));
            Assert.That(
                firstFingerprint.Sections[1].FingerprintHash,
                Is.Not.EqualTo(
                    secondFingerprint.Sections[1]
                        .FingerprintHash));
            Assert.That(
                firstFingerprint.OverallHash,
                Is.Not.EqualTo(
                    secondFingerprint.OverallHash));

            var roundTrip =
                RuntimeReplayCheckpointFingerprintCodec.Decode(
                    RuntimeReplayCheckpointFingerprintCodec.Encode(
                        firstFingerprint));
            Assert.That(roundTrip.CompletedTick, Is.EqualTo(70));
            Assert.That(
                roundTrip.OverallHash,
                Is.EqualTo(firstFingerprint.OverallHash));
        }

        [Test]
        public void Registry_PrevalidatesEverySectionBeforeAnyRestore()
        {
            var first = new ReplayCheckpointTestParticipant6F31(
                sectionId: 1,
                currentVersion: 1,
                value: 10,
                fingerprintValue: 1);
            var second = new ReplayCheckpointTestParticipant6F31(
                sectionId: 2,
                currentVersion: 1,
                value: 20,
                fingerprintValue: 2);
            var registry = new RuntimeReplayCheckpointRegistry();
            registry.RegisterParticipant(first);
            registry.RegisterParticipant(second);
            registry.Seal();
            var checkpoint = registry.Capture(5, 6);

            second.FailPrevalidation = true;
            Assert.Throws<FormatException>(
                () => registry.Restore(checkpoint));
            Assert.That(first.PrevalidateCalls, Is.EqualTo(1));
            Assert.That(second.PrevalidateCalls, Is.EqualTo(1));
            Assert.That(first.RestoreCalls, Is.Zero);
            Assert.That(second.RestoreCalls, Is.Zero);
        }

        [Test]
        public void Registry_MigratesSectionBeforePrevalidateAndRestore()
        {
            var participant =
                new ReplayCheckpointTestParticipant6F31(
                    sectionId: 7,
                    currentVersion: 2,
                    value: 0,
                    fingerprintValue: 7);
            var registry = new RuntimeReplayCheckpointRegistry();
            registry.RegisterParticipant(participant);
            registry.RegisterMigration(
                sectionId: 7,
                fromVersion: 1,
                toVersion: 2,
                migration: (source, destination) =>
                {
                    destination.WriteInt32(
                        source.ReadInt32() * 10);
                });
            registry.Seal();

            byte[] versionOnePayload;
            using (var writer =
                   new RuntimeReplayCheckpointWriter())
            {
                writer.WriteInt32(3);
                versionOnePayload = writer.ToArray();
            }
            var section = new RuntimeReplayCheckpointSection(
                sectionId: 7,
                sectionVersion: 1,
                payload: versionOnePayload,
                payloadHash:
                RuntimeReplayHash.CalculateSha256(
                    versionOnePayload));
            var sourceCheckpoint =
                RuntimeReplayCheckpointCodec.Create(
                    completedTick: 9,
                    cursor: 10,
                    schemaHash: new string('0', 64),
                    sections:
                    new RuntimeReplayCheckpointSection[]
                    {
                        section,
                    });

            registry.Restore(sourceCheckpoint);
            Assert.That(participant.Value, Is.EqualTo(30));
            Assert.That(participant.PrevalidateCalls, Is.EqualTo(1));
            Assert.That(participant.RestoreCalls, Is.EqualTo(1));
        }

        [Test]
        public void DgrWriter_RoundTripsDeflateChunksAndFooterIndex()
        {
            var finalPath = System.IO.Path.Combine(
                _temporaryDirectory,
                "roundtrip.dgr");
            var header = CreateHeader6F31();
            var commandPayload = new byte[8192];
            for (var i = 0; i < commandPayload.Length; i++)
            {
                commandPayload[i] = (byte)(i % 7);
            }
            var checkpointPayload = new byte[]
            {
                4,
                3,
                2,
                1,
            };

            using (var writer =
                   new DgrReplayFileWriter(
                       finalPath,
                       header))
            {
                writer.EnqueueChunk(
                    "commands",
                    startTick: 90,
                    endTick: 100,
                    cursor: 101,
                    payload: commandPayload);
                writer.EnqueueChunk(
                    "checkpoints",
                    completedTick: 100,
                    cursor: 102,
                    payload: checkpointPayload);
                writer.Complete();
            }

            Assert.That(File.Exists(finalPath), Is.True);
            Assert.That(
                File.Exists(finalPath + ".tmp"),
                Is.False);
            var scan = DgrReplayFileScanner.Scan(finalPath);
            Assert.That(scan.IsComplete, Is.True, scan.Failure);
            Assert.That(scan.Chunks.Count, Is.EqualTo(2));
            Assert.That(scan.Chunks[0].StartTick, Is.EqualTo(90));
            Assert.That(scan.Chunks[0].EndTick, Is.EqualTo(100));
            Assert.That(
                scan.Chunks[1].StartTick,
                Is.EqualTo(scan.Chunks[1].EndTick));
            Assert.That(
                scan.Chunks[0].Compression,
                Is.EqualTo(DgrReplayCompression.Deflate));
            Assert.That(
                scan.Chunks[0].StoredLength,
                Is.LessThan(commandPayload.Length));
            Assert.That(
                scan.Header.Metadata,
                Is.EqualTo(
                    new byte[]
                    {
                        9,
                        8,
                        7,
                        6,
                    }));
            Assert.That(
                scan.Header.MetadataHash,
                Is.EqualTo(
                    RuntimeReplayHash.CalculateSha256(
                        scan.Header.Metadata)));
            var mutableMetadataCopy = scan.Header.Metadata;
            mutableMetadataCopy[0] = 0;
            Assert.That(scan.Header.Metadata[0], Is.EqualTo(9));

            using var reader =
                new DgrReplayFileReader(finalPath);
            Assert.That(
                reader.ReadChunkPayload(0),
                Is.EqualTo(commandPayload));
            Assert.That(
                reader.ReadChunkPayload(1),
                Is.EqualTo(checkpointPayload));
        }

        [Test]
        public void DgrRecovery_DropsTruncatedTailAndFinalizesValidPrefix()
        {
            var finalPath = System.IO.Path.Combine(
                _temporaryDirectory,
                "recover.dgr");
            var payload = new byte[4096];
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] = (byte)(i % 13);
            }

            using (var writer =
                   new DgrReplayFileWriter(
                       finalPath,
                       CreateHeader6F31()))
            {
                writer.EnqueueChunk(
                    "commands",
                    completedTick: 12,
                    cursor: 13,
                    payload: payload);
            }

            var temporaryPath =
                DgrReplayFileLifecycle.GetTemporaryPath(
                    finalPath);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.Append,
                       FileAccess.Write,
                       FileShare.None))
            using (var binaryWriter =
                   new BinaryWriter(stream))
            {
                binaryWriter.Write(
                    DgrReplayFormat.CHUNK_MAGIC);
                binaryWriter.Write(
                    DgrReplayFormat.CHUNK_VERSION);
                binaryWriter.Write(1024);
                binaryWriter.Write((byte)0x7f);
            }

            var damagedScan =
                DgrReplayFileScanner.Scan(temporaryPath);
            Assert.That(damagedScan.HasValidFooter, Is.False);
            Assert.That(damagedScan.Chunks.Count, Is.EqualTo(1));
            Assert.That(damagedScan.Failure, Is.Not.Null);
            Assert.That(
                damagedScan.DataLength,
                Is.LessThan(
                    new FileInfo(temporaryPath).Length));

            var recovered =
                DgrReplayRecovery.RecoverTemporaryFile(
                    finalPath);
            Assert.That(recovered.IsComplete, Is.True);
            Assert.That(recovered.Chunks.Count, Is.EqualTo(1));
            Assert.That(File.Exists(temporaryPath), Is.False);
            using var reader =
                new DgrReplayFileReader(finalPath);
            Assert.That(
                reader.ReadChunkPayload(0),
                Is.EqualTo(payload));
        }

        [Test]
        public void DgrWriter_RejectsChunkAboveBoundedQueueBudget()
        {
            var finalPath = System.IO.Path.Combine(
                _temporaryDirectory,
                "queue-budget.dgr");
            using var writer =
                new DgrReplayFileWriter(
                    finalPath,
                    CreateHeader6F31());
            var oversizedPayload =
                new byte[
                    DgrReplayFileWriter.MAX_QUEUED_BYTES
                    + 1];

            Assert.Throws<DgrReplayQueueOverflowException>(
                () => writer.EnqueueChunk(
                    "commands",
                    completedTick: 1,
                    cursor: 2,
                    payload: oversizedPayload));
            Assert.That(writer.QueuedBytes, Is.Zero);
        }

        [Test]
        public void VersionMigrationRegistries_MigrateContainerAndTracks()
        {
            var container =
                new RuntimeReplayContainerMigrationRegistry(
                    currentVersion: 2);
            container.Register(
                fromVersion: 1,
                toVersion: 2,
                migration: (source, destination) =>
                {
                    destination.WriteInt32(
                        source.ReadInt32() + 1);
                });
            container.Seal();

            var tracks =
                new RuntimeReplayTrackMigrationRegistry();
            tracks.RegisterTrack("commands", 2);
            tracks.RegisterMigration(
                trackId: "commands",
                fromVersion: 1,
                toVersion: 2,
                migration: (source, destination) =>
                {
                    destination.WriteInt32(
                        source.ReadInt32() * 2);
                });
            tracks.Seal();

            var sourcePayload = EncodeInt32Payload6F31(8);
            Assert.That(
                DecodeInt32Payload6F31(
                    container.Migrate(1, sourcePayload)),
                Is.EqualTo(9));
            Assert.That(
                DecodeInt32Payload6F31(
                    tracks.Migrate(
                        "commands",
                        1,
                        sourcePayload)),
                Is.EqualTo(16));
            Assert.That(
                container.SchemaHash,
                Has.Length.EqualTo(64));
            Assert.That(
                tracks.SchemaHash,
                Has.Length.EqualTo(64));
            Assert.That(
                tracks.GetTrackSchemaHash("commands"),
                Has.Length.EqualTo(64));
            Assert.That(
                container.Assess(1).RequiresMigration,
                Is.True);
            Assert.That(
                container.Assess(2).RequiresMigration,
                Is.False);
            Assert.That(
                tracks.Assess("commands", 1).CanMigrate,
                Is.True);
            Assert.That(
                tracks.Assess("missing", 1).FailureKind,
                Is.EqualTo(
                    RuntimeReplayMigrationFailureKind.UnknownTrack));
        }

        [Test]
        public void VersionMigrationAssessment_ClassifiesMissingAndNewerPaths()
        {
            var registry =
                new RuntimeReplayContainerMigrationRegistry(
                    currentVersion: 3);
            registry.Register(
                fromVersion: 1,
                toVersion: 2,
                migration: (source, destination) =>
                {
                    destination.WriteInt32(source.ReadInt32());
                });
            registry.Seal();

            var missing = registry.Assess(1);
            Assert.That(missing.CanMigrate, Is.False);
            Assert.That(
                missing.FailureKind,
                Is.EqualTo(
                    RuntimeReplayMigrationFailureKind
                        .MissingSequentialStep));
            Assert.That(
                registry.Assess(4).FailureKind,
                Is.EqualTo(
                    RuntimeReplayMigrationFailureKind
                        .SourceNewerThanRuntime));
            var exception =
                Assert.Throws<RuntimeReplayMigrationException>(
                    () => registry.Migrate(
                        1,
                        EncodeInt32Payload6F31(8)));
            Assert.That(
                exception.FailureKind,
                Is.EqualTo(
                    RuntimeReplayMigrationFailureKind
                        .MissingSequentialStep));
        }

        private static RuntimeReplayCheckpointRegistry
            CreateFingerprintRegistry6F31(
                int section10Fingerprint,
                int section20Fingerprint)
        {
            var registry =
                new RuntimeReplayCheckpointRegistry();
            registry.RegisterParticipant(
                new ReplayCheckpointTestParticipant6F31(
                    10,
                    1,
                    0,
                    section10Fingerprint));
            registry.RegisterParticipant(
                new ReplayCheckpointTestParticipant6F31(
                    20,
                    1,
                    0,
                    section20Fingerprint));
            registry.Seal();
            return registry;
        }

        private static DgrReplayFileHeader CreateHeader6F31()
        {
            return new DgrReplayFileHeader(
                recordingId: Guid.NewGuid(),
                createdUtcTicks: DateTime.UtcNow.Ticks,
                registrySchemaHash: new string('a', 64),
                metadata:
                new byte[]
                {
                    9,
                    8,
                    7,
                    6,
                },
                tracks: new[]
                {
                    new DgrReplayTrackDescriptor(
                        "commands",
                        1,
                        new string('b', 64)),
                    new DgrReplayTrackDescriptor(
                        "checkpoints",
                        1,
                        new string('c', 64)),
                });
        }

        private static byte[] EncodeInt32Payload6F31(int value)
        {
            using var writer =
                new RuntimeReplayCheckpointWriter();
            writer.WriteInt32(value);
            return writer.ToArray();
        }

        private static int DecodeInt32Payload6F31(
            byte[] payload)
        {
            using var reader =
                new RuntimeReplayCheckpointReader(payload);
            var value = reader.ReadInt32();
            reader.RequireEnd();
            return value;
        }
    }
}
