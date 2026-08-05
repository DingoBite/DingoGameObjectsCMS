using System;
using System.Linq;
using DingoGameObjectsCMS.Editor;
using DingoGameObjectsCMS.RuntimeObjects.DotsState;
using DingoGameObjectsCMS.RuntimeObjects.Replay;
using NUnit.Framework;
using Unity.Entities;

namespace DingoGameObjectsCMS.Tests.Editor
{
    [RuntimeDotsPersisted]
    public struct RuntimeDotsPersistedTestComponent : IComponentData
    {
        public int Value;
    }

    [RuntimeDotsPersisted]
    public struct RuntimeDotsPersistedEnableableTag :
        IComponentData,
        IEnableableComponent
    {
    }

    [RuntimeDotsDerived]
    public struct RuntimeDotsDerivedTestComponent : IComponentData
    {
        public float Value;
    }

    [RuntimeDotsTransient]
    public struct RuntimeDotsTransientTestComponent :
        IComponentData,
        IEnableableComponent
    {
        public int Value;
    }

    [RuntimeDotsPresentation]
    public struct RuntimeDotsPresentationTestBuffer : IBufferElementData
    {
        public int Value;
    }

    public struct RuntimeDotsUnclassifiedTestComponent : IComponentData
    {
        public int Value;
    }

    [RuntimeDotsPersisted]
    [RuntimeDotsDerived]
    public struct RuntimeDotsMultiplyClassifiedTestComponent : IComponentData
    {
        public int Value;
    }

    [RuntimeDotsPersisted]
    public struct RuntimeDotsEntityReferenceTestComponent : IComponentData
    {
        public Entity Value;
    }

    [RuntimeDotsPersisted]
    public struct RuntimeDotsRuntimeInstanceReferenceTestComponent :
        IComponentData
    {
        public global::DingoGameObjectsCMS.RuntimeObjects.RuntimeInstance Value;
    }

    [RuntimeDotsPersisted]
    public struct RuntimeDotsUnsupportedPointerTestComponent : IComponentData
    {
        public IntPtr Value;
    }

    [RuntimeDotsPersisted]
    public struct RuntimeDotsEntityKeyTestComponent : IComponentData
    {
        public RuntimeDotsStateEntityKey Value;
    }

    public class RuntimeDotsStateSchemaTests
    {
        private static readonly Type[] CLASSIFIED_TYPES =
        {
            typeof(RuntimeDotsPersistedTestComponent),
            typeof(RuntimeDotsDerivedTestComponent),
            typeof(RuntimeDotsTransientTestComponent),
            typeof(RuntimeDotsPresentationTestBuffer),
        };

        [Test]
        public void Discovery_ClassifiesComponentsAndBuffersDeterministically()
        {
            var result = RuntimeDotsStateSchemaDiscovery.Discover(
                CLASSIFIED_TYPES.Reverse());
            var forward = RuntimeDotsStateSchemaDiscovery.Discover(
                CLASSIFIED_TYPES);

            Assert.That(result.Components.Count, Is.EqualTo(4));
            Assert.That(
                result.Components.Select(
                        descriptor => descriptor.RuntimeType)
                    .ToArray(),
                Is.EqualTo(forward.Components.Select(
                        descriptor => descriptor.RuntimeType)
                    .ToArray()));

            var persisted = result.Components.Single(
                descriptor => descriptor.RuntimeType
                              == typeof(RuntimeDotsPersistedTestComponent));
            Assert.That(
                persisted.Schema.RuntimeType,
                Is.EqualTo(typeof(RuntimeDotsPersistedTestComponent)));
            Assert.That(
                persisted.Schema.Classification,
                Is.EqualTo(RuntimeDotsStateClassification.Persisted));
            Assert.That(
                persisted.Schema.Kind,
                Is.EqualTo(RuntimeDotsStateComponentKind.Component));

            var transient = result.Components.Single(
                descriptor => descriptor.RuntimeType
                              == typeof(RuntimeDotsTransientTestComponent));
            Assert.That(transient.Schema.Enableable, Is.True);

            var buffer = result.Components.Single(
                descriptor => descriptor.RuntimeType
                              == typeof(RuntimeDotsPresentationTestBuffer));
            Assert.That(
                buffer.Schema.Kind,
                Is.EqualTo(RuntimeDotsStateComponentKind.Buffer));
            Assert.That(
                buffer.Schema.Classification,
                Is.EqualTo(RuntimeDotsStateClassification.Presentation));
        }

        [Test]
        public void Discovery_RequiresClassificationWhenProjectPredicateMatches()
        {
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeDotsStateSchemaDiscovery.Discover(
                    new[] { typeof(RuntimeDotsUnclassifiedTestComponent) },
                    type => type ==
                            typeof(RuntimeDotsUnclassifiedTestComponent)));
        }

        [Test]
        public void Discovery_RejectsMultipleClassifications()
        {
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeDotsStateSchemaDiscovery.DescribeComponent(
                    typeof(RuntimeDotsMultiplyClassifiedTestComponent)));
        }

        [Test]
        public void Discovery_RejectsEntityReferencesInPersistedComponents()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RuntimeDotsStateSchemaDiscovery.DescribeComponent(
                    typeof(RuntimeDotsEntityReferenceTestComponent)));
            Assert.That(
                exception.Message,
                Does.Contain(nameof(RuntimeDotsStateEntityKey)));
        }

        [Test]
        public void Discovery_RejectsRuntimeInstanceInPersistedComponents()
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                RuntimeDotsStateSchemaDiscovery.Discover(
                    new[]
                    {
                        typeof(
                            RuntimeDotsRuntimeInstanceReferenceTestComponent)
                    }));

            Assert.That(
                exception.Message,
                Does.Contain(nameof(RuntimeDotsStateEntityKey)));
        }

        [Test]
        public void Discovery_RejectsUnsupportedPersistedFieldCodecs()
        {
            Assert.Throws<InvalidOperationException>(() =>
                RuntimeDotsStateSchemaDiscovery.DescribeComponent(
                    typeof(RuntimeDotsUnsupportedPointerTestComponent)));
        }

        [Test]
        public void Discovery_EmitsCanonicalStableEntityKeyCodec()
        {
            var discovery = RuntimeDotsStateSchemaDiscovery.Discover(
                new[] { typeof(RuntimeDotsEntityKeyTestComponent) });
            var manifest = RuntimeDotsStateSchemaReconciler.Reconcile(
                null,
                discovery.Components.Select(value => value.Schema).ToArray(),
                codecVersion: 1);
            RuntimeDotsStateSchemaGenerationCore.BindReconciledSchema(
                discovery.Components,
                manifest);

            var source = RuntimeDotsStateCodeEmitter.Generate(
                manifest,
                discovery.Components,
                new RuntimeDotsStateCodeEmissionProfile(
                    "Generated.Tests",
                    "GeneratedRuntimeDotsStateSchema"));

            Assert.That(
                source,
                Does.Contain("writer.WriteString(value.@Value.@StoreId.ToString());"));
            Assert.That(
                source,
                Does.Contain(
                    "value.@Value.@StoreId = new global::Unity.Collections.FixedString32Bytes(reader.ReadString(global::Unity.Collections.FixedString32Bytes.UTF8MaxLengthInBytes));"));
        }

        [Test]
        public void Reconcile_PreservesTypeIdsAndReservesRemovedIds()
        {
            var discovery = RuntimeDotsStateSchemaDiscovery.Discover(
                new[]
                {
                    typeof(RuntimeDotsPersistedTestComponent),
                    typeof(RuntimeDotsDerivedTestComponent),
                });
            var first = RuntimeDotsStateSchemaReconciler.Reconcile(
                null,
                discovery.Components.Select(value => value.Schema).ToArray(),
                codecVersion: 1);
            var persistedId = first.Components.Single(value =>
                    value.RuntimeType ==
                    typeof(RuntimeDotsPersistedTestComponent))
                .ComponentTypeId;
            var derivedId = first.Components.Single(value =>
                    value.RuntimeType ==
                    typeof(RuntimeDotsDerivedTestComponent))
                .ComponentTypeId;

            var replacement = RuntimeDotsStateSchemaDiscovery.Discover(
                new[]
                {
                    typeof(RuntimeDotsPersistedTestComponent),
                    typeof(RuntimeDotsPresentationTestBuffer),
                });
            var second = RuntimeDotsStateSchemaReconciler.Reconcile(
                first,
                replacement.Components.Select(value => value.Schema).ToArray(),
                codecVersion: 1);
            Assert.That(
                second.Components.Single(value =>
                        value.RuntimeType ==
                        typeof(RuntimeDotsPersistedTestComponent))
                    .ComponentTypeId,
                Is.EqualTo(persistedId));
            Assert.That(
                second.Components.Any(value =>
                    value.RuntimeType ==
                    typeof(RuntimeDotsDerivedTestComponent)),
                Is.False);
            Assert.That(
                second.ReservedComponentTypeIds,
                Does.Contain(derivedId));
            Assert.That(
                second.Components.All(value => value.RuntimeType != null),
                Is.True);
            Assert.That(
                RuntimeDotsStateSchemaReconciler.CalculateSchemaHash(second),
                Is.EqualTo(second.SchemaHash));

            var repeated = RuntimeDotsStateSchemaReconciler.Reconcile(
                second,
                replacement.Components.Select(value => value.Schema).ToArray(),
                codecVersion: 1);
            Assert.That(repeated.SchemaHash, Is.EqualTo(second.SchemaHash));
            Assert.That(
                repeated.ReservedComponentTypeIds,
                Is.EqualTo(second.ReservedComponentTypeIds));

            var reintroduced = RuntimeDotsStateSchemaReconciler.Reconcile(
                repeated,
                discovery.Components.Select(value => value.Schema).ToArray(),
                codecVersion: 1);
            Assert.That(
                reintroduced.Components.Single(value =>
                        value.RuntimeType ==
                        typeof(RuntimeDotsDerivedTestComponent))
                    .ComponentTypeId,
                Is.Not.EqualTo(derivedId));
        }

        [Test]
        public void Emitter_GeneratesTypedRegistryWithoutRuntimeReflection()
        {
            var discovery = RuntimeDotsStateSchemaDiscovery.Discover(
                CLASSIFIED_TYPES);
            var manifest = RuntimeDotsStateSchemaReconciler.Reconcile(
                null,
                discovery.Components.Select(value => value.Schema).ToArray(),
                codecVersion: 1);
            RuntimeDotsStateSchemaGenerationCore.BindReconciledSchema(
                discovery.Components,
                manifest);

            var source = RuntimeDotsStateCodeEmitter.Generate(
                manifest,
                discovery.Components,
                new RuntimeDotsStateCodeEmissionProfile(
                    "Generated.Tests",
                    "GeneratedRuntimeDotsStateSchema"));

            Assert.That(source, Does.Contain("SchemaHash"));
            Assert.That(source, Does.Contain("CreateSchema()"));
            Assert.That(
                source,
                Does.Contain("CreateManifest()"));
            Assert.That(
                source,
                Does.Contain("RuntimeType = typeof(global::"));
            Assert.That(
                source,
                Does.Not.Contain("ManifestProvider"));
            Assert.That(source, Does.Contain("Register("));
            Assert.That(source, Does.Contain(".Component<"));
            Assert.That(source, Does.Contain(".Buffer<"));
            Assert.That(source, Does.Contain("RuntimeDotsStateComponentCodec<"));
            Assert.That(source, Does.Contain("WritePersistedEntityState("));
            Assert.That(source, Does.Contain("PrevalidatePersistedEntityState("));
            Assert.That(
                source,
                Does.Contain(
                    "PrevalidatePersistedEntityState("
                    + Environment.NewLine
                    + "            RuntimeReplayCheckpointReader reader)"));
            Assert.That(source, Does.Contain("ReadPersistedEntityState("));
            Assert.That(source, Does.Contain("writer.WriteBoolean(present"));
            Assert.That(source, Does.Contain(" != targetPresent"));
            Assert.That(
                source,
                Does.Contain(
                    "Factory topology/signature mismatch for persisted component"));
            Assert.That(source, Does.Contain("writer.WriteInt32(value.@Value);"));
            Assert.That(source, Does.Contain("value.@Value = reader.ReadInt32();"));
            Assert.That(source, Does.Not.Contain("GetCustomAttribute"));
            Assert.That(source, Does.Not.Contain("Type.GetType"));
            Assert.That(source, Does.Not.Contain("JsonUtility"));
            Assert.That(source, Does.Not.Contain("ComponentTypeKey"));
            Assert.That(source, Does.Not.Contain("RuntimeTypeName"));
            Assert.That(source, Does.Not.Contain("AssemblyName"));
            Assert.That(source, Does.Not.Contain("ValueSignature"));
            Assert.That(source, Does.Not.Contain("Tombstone"));
        }

        [Test]
        public void Emitter_PersistsZeroSizedEnableableTagWithoutDataAccess()
        {
            var discovery = RuntimeDotsStateSchemaDiscovery.Discover(
                new[] { typeof(RuntimeDotsPersistedEnableableTag) });
            var descriptor = discovery.Components.Single();
            var manifest = RuntimeDotsStateSchemaReconciler.Reconcile(
                null,
                new[] { descriptor.Schema },
                codecVersion: 1);
            RuntimeDotsStateSchemaGenerationCore.BindReconciledSchema(
                discovery.Components,
                manifest);

            var source = RuntimeDotsStateCodeEmitter.Generate(
                manifest,
                discovery.Components,
                new RuntimeDotsStateCodeEmissionProfile(
                    "Generated.Tests",
                    "GeneratedRuntimeDotsStateSchema"));
            var typeName =
                "global::DingoGameObjectsCMS.Tests.Editor."
                + nameof(RuntimeDotsPersistedEnableableTag);

            Assert.That(descriptor.IsZeroSized, Is.True);
            Assert.That(
                source,
                Does.Contain($"IsComponentEnabled<{typeName}>"));
            Assert.That(
                source,
                Does.Contain($"SetComponentEnabled<{typeName}>"));
            Assert.That(
                source,
                Does.Not.Contain($"GetComponentData<{typeName}>"));
            Assert.That(
                source,
                Does.Not.Contain("entityManager.SetComponentData(entity"));
        }

        [Test]
        public void Reconciler_RejectsActiveManifestEntryWithoutDirectRuntimeType()
        {
            var discovery = RuntimeDotsStateSchemaDiscovery.Discover(
                new[] { typeof(RuntimeDotsPersistedTestComponent) });
            var schema = discovery.Components.Single().Schema;
            schema.RuntimeType = null;

            var exception = Assert.Throws<InvalidOperationException>(() =>
                RuntimeDotsStateSchemaReconciler.Reconcile(
                    null,
                    new[] { schema },
                    codecVersion: 1));

            Assert.That(exception.Message, Does.Contain("RuntimeType = typeof(T)"));
        }

        [Test]
        public void Registry_RejectsDuplicateIdsAndRuntimeTypes()
        {
            var registry = new RuntimeDotsStateSchemaRegistry(
                new string('a', 64));
            var first = RuntimeDotsStateComponentDescriptor.Component<
                RuntimeDotsPersistedTestComponent>(
                1,
                RuntimeDotsStateClassification.Persisted,
                enableable: false);
            Assert.Throws<InvalidOperationException>(() =>
                registry.Register(first));
            registry.Register(
                first,
                new RuntimeDotsStateComponentCodec<
                    RuntimeDotsPersistedTestComponent>(
                    WritePersistedTestComponent,
                    ReadPersistedTestComponent));

            Assert.That(
                registry.TakeById(1).RuntimeType,
                Is.EqualTo(typeof(RuntimeDotsPersistedTestComponent)));
            Assert.That(
                registry.TakeByType(
                    typeof(RuntimeDotsPersistedTestComponent))
                    .ComponentTypeId,
                Is.EqualTo(1));

            var duplicateId = RuntimeDotsStateComponentDescriptor.Component<
                RuntimeDotsDerivedTestComponent>(
                1,
                RuntimeDotsStateClassification.Derived,
                enableable: false);
            Assert.Throws<InvalidOperationException>(() =>
                registry.Register(duplicateId));
            var duplicateType = RuntimeDotsStateComponentDescriptor.Component<
                RuntimeDotsPersistedTestComponent>(
                3,
                RuntimeDotsStateClassification.Derived,
                enableable: false);
            Assert.Throws<InvalidOperationException>(() =>
                registry.Register(duplicateType));
        }

        [Test]
        public void TypedCodec_RoundTripsCanonicalPersistedValue()
        {
            var descriptor = RuntimeDotsStateComponentDescriptor.Component<
                RuntimeDotsPersistedTestComponent>(
                1,
                RuntimeDotsStateClassification.Persisted,
                enableable: false);
            var codec = new RuntimeDotsStateComponentCodec<
                RuntimeDotsPersistedTestComponent>(
                WritePersistedTestComponent,
                ReadPersistedTestComponent);
            var registry = new RuntimeDotsStateSchemaRegistry(
                new string('b', 64));
            registry.Register(descriptor, codec);

            using var writer = new RuntimeReplayCheckpointWriter();
            var source = new RuntimeDotsPersistedTestComponent
            {
                Value = 37,
            };
            registry.TakeCodec<RuntimeDotsPersistedTestComponent>()
                .Write(writer, in source);
            using var reader = new RuntimeReplayCheckpointReader(
                writer.ToArray());
            var restored = registry
                .TakeCodec<RuntimeDotsPersistedTestComponent>()
                .Read(reader);
            reader.RequireEnd();

            Assert.That(restored.Value, Is.EqualTo(source.Value));
        }

        [Test]
        public void FixedStringRead_RejectsPayloadBeyondFixedCapacity()
        {
            using var writer = new RuntimeReplayCheckpointWriter();
            writer.WriteString(
                new string(
                    'x',
                    Unity.Collections.FixedString32Bytes
                        .UTF8MaxLengthInBytes + 1));
            using var reader = new RuntimeReplayCheckpointReader(
                writer.ToArray());

            Assert.Throws<FormatException>(() => reader.ReadString(
                Unity.Collections.FixedString32Bytes
                    .UTF8MaxLengthInBytes));
        }

        [Test]
        public void EntityKey_UsesStoreFactoryAndProductWithoutEpoch()
        {
            var first = new RuntimeDotsStateEntityKey(
                "store",
                factoryId: 7,
                productId: 11);
            var same = new RuntimeDotsStateEntityKey(
                "store",
                factoryId: 7,
                productId: 11);
            var otherProduct = new RuntimeDotsStateEntityKey(
                "store",
                factoryId: 7,
                productId: 12);

            Assert.That(first, Is.EqualTo(same));
            Assert.That(first.GetHashCode(), Is.EqualTo(same.GetHashCode()));
            Assert.That(first, Is.Not.EqualTo(otherProduct));
        }

        private static void WritePersistedTestComponent(
            RuntimeReplayCheckpointWriter writer,
            in RuntimeDotsPersistedTestComponent value)
        {
            writer.WriteInt32(value.Value);
        }

        private static RuntimeDotsPersistedTestComponent
            ReadPersistedTestComponent(
                RuntimeReplayCheckpointReader reader)
        {
            return new RuntimeDotsPersistedTestComponent
            {
                Value = reader.ReadInt32(),
            };
        }
    }
}
