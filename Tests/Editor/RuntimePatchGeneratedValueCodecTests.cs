using System;
using System.Collections.Generic;
using System.Reflection;
using DingoGameObjectsCMS.Editor;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using NUnit.Framework;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public struct RuntimePatchPlacementFixture
    {
        public Hash128 PlacementId;
        public GameAssetInstance Instance;
        public Vector2Int Cell;
    }

    public sealed class RuntimePatchCollectionFixture_GRC : GameRuntimeComponent
    {
        public List<RuntimePatchPlacementFixture> Placements;
    }

    public sealed class RuntimePatchLegacyListFixture_GRC : GameRuntimeComponent
    {
        public List<Vector2Int> Cells;
    }

    public abstract class RuntimePatchInheritedFixtureBase_GRC :
        GameRuntimeComponent
    {
        public int BaseValue;
    }

    public sealed class RuntimePatchInheritedFixture_GRC :
        RuntimePatchInheritedFixtureBase_GRC
    {
        public float LeafValue;
    }

    public struct RuntimePatchLayoutIntPair
    {
        public int X, Y;
    }

    public struct RuntimePatchLayoutFloatPair
    {
        public int X;
        public float Y;
    }

    public struct RuntimePatchLayoutRenamedPair
    {
        public int X, Z;
    }

    public struct RuntimePatchLayoutEquivalentPair
    {
        public int Y, X;
    }

    public enum RuntimePatchLayoutByteMode : byte { Idle, Active }
    public enum RuntimePatchLayoutWideMode : int { Idle, Active }
    public enum RuntimePatchLayoutRemappedMode : byte { Idle, Active = 2 }

    public class RuntimePatchLayoutInt_GRC : GameRuntimeComponent { public int Value; }
    public class RuntimePatchLayoutFloat_GRC : GameRuntimeComponent { public float Value; }
    public class RuntimePatchLayoutRenamed_GRC : GameRuntimeComponent { public int Amount; }
    public class RuntimePatchLayoutStruct_GRC : GameRuntimeComponent { public RuntimePatchLayoutIntPair Value; }
    public class RuntimePatchLayoutFloatStruct_GRC : GameRuntimeComponent { public RuntimePatchLayoutFloatPair Value; }
    public class RuntimePatchLayoutRenamedStruct_GRC : GameRuntimeComponent { public RuntimePatchLayoutRenamedPair Value; }
    public class RuntimePatchLayoutEquivalentStruct_GRC : GameRuntimeComponent { public RuntimePatchLayoutEquivalentPair Value; }
    public class RuntimePatchLayoutIntList_GRC : GameRuntimeComponent { public List<int> Value; }
    public class RuntimePatchLayoutFloatList_GRC : GameRuntimeComponent { public List<float> Value; }
    public class RuntimePatchLayoutStructList_GRC : GameRuntimeComponent { public List<RuntimePatchLayoutIntPair> Value; }
    public class RuntimePatchLayoutFloatStructList_GRC : GameRuntimeComponent { public List<RuntimePatchLayoutFloatPair> Value; }
    public class RuntimePatchLayoutEnum_GRC : GameRuntimeComponent { public RuntimePatchLayoutByteMode Value; }
    public class RuntimePatchLayoutWideEnum_GRC : GameRuntimeComponent { public RuntimePatchLayoutWideMode Value; }
    public class RuntimePatchLayoutRemappedEnum_GRC : GameRuntimeComponent { public RuntimePatchLayoutRemappedMode Value; }
    public class RuntimePatchLayoutIntArray_GRC : GameRuntimeComponent { public int[] Value; }
    public class RuntimePatchLayoutFloatArray_GRC : GameRuntimeComponent { public float[] Value; }
    public class RuntimePatchLayoutJaggedArray_GRC : GameRuntimeComponent { public int[][] Value; }
    public class RuntimePatchLayoutMultidimensionalArray_GRC : GameRuntimeComponent { public int[,] Value; }

    public struct RuntimePatchArrayValue
    {
        public int[] Indices;
        public float Weight;
    }

    public class RuntimePatchArrayFixture_GRC : GameRuntimeComponent
    {
        public float[] Floats;
        public int[] Integers;
        public RuntimePatchArrayValue[] Nested;
    }

    // Observes values handed to the binary lane, keeping authoring JSON tests
    // independent from generated fixture files.
    public class RuntimePatchArrayObservationCodec : RuntimeComponentPatchCodec<RuntimePatchArrayFixture_GRC>
    {
        public RuntimePatchArrayFixture_GRC LastEncoded;
        public override uint FieldCount => 3;

        public RuntimePatchArrayObservationCodec() : base(9905) { }
        public override bool TryGetFieldInfo(uint fieldId, out FieldInfo field)
        {
            var names = new[] { "Floats", "Integers", "Nested" };
            field = fieldId < names.Length ? typeof(RuntimePatchArrayFixture_GRC).GetField(names[fieldId]) : null;
            return field != null;
        }
        protected override RuntimePatchArrayFixture_GRC CloneTyped(RuntimePatchArrayFixture_GRC value) => throw new NotSupportedException();
        protected override void WriteCanonicalTyped(CanonicalPatchBinaryWriter writer, RuntimePatchArrayFixture_GRC value, RuntimePatchCodecContext context)
        {
            LastEncoded = value;
            writer.WriteByte(0);
        }
        protected override RuntimePatchArrayFixture_GRC ReadCanonicalTyped(CanonicalPatchBinaryReader reader, RuntimePatchCodecContext context) => throw new NotSupportedException();
        protected override void CollectFieldPatchesTyped(RuntimePatchArrayFixture_GRC baseline, RuntimePatchArrayFixture_GRC current, List<FieldPatch> fields, RuntimePatchCodecContext context) => throw new NotSupportedException();
        protected override void ApplyFieldPatchTyped(RuntimePatchArrayFixture_GRC target, FieldPatch fieldPatch, RuntimePatchCodecContext context) => throw new NotSupportedException();
    }

    public class RuntimePatchArrayTestContext : RuntimePatchCodecContext
    {
        public override void WriteRuntimeInstance(CanonicalPatchBinaryWriter writer, in RuntimeInstance value) => throw new NotSupportedException();
        public override RuntimeInstance ReadRuntimeInstance(CanonicalPatchBinaryReader reader) => throw new NotSupportedException();
        public override bool RuntimeInstancesEqual(in RuntimeInstance first, in RuntimeInstance second) => throw new NotSupportedException();
    }

    public class RuntimePatchGeneratedValueCodecTests
    {
        [TestCase(typeof(RuntimePatchLayoutInt_GRC), typeof(RuntimePatchLayoutFloat_GRC))]
        [TestCase(typeof(RuntimePatchLayoutInt_GRC), typeof(RuntimePatchLayoutRenamed_GRC))]
        [TestCase(typeof(RuntimePatchLayoutStruct_GRC), typeof(RuntimePatchLayoutFloatStruct_GRC))]
        [TestCase(typeof(RuntimePatchLayoutStruct_GRC), typeof(RuntimePatchLayoutRenamedStruct_GRC))]
        [TestCase(typeof(RuntimePatchLayoutIntList_GRC), typeof(RuntimePatchLayoutFloatList_GRC))]
        [TestCase(typeof(RuntimePatchLayoutStructList_GRC), typeof(RuntimePatchLayoutFloatStructList_GRC))]
        [TestCase(typeof(RuntimePatchLayoutIntArray_GRC), typeof(RuntimePatchLayoutFloatArray_GRC))]
        [TestCase(typeof(RuntimePatchLayoutIntArray_GRC), typeof(RuntimePatchLayoutIntList_GRC))]
        [TestCase(typeof(RuntimePatchLayoutEnum_GRC), typeof(RuntimePatchLayoutWideEnum_GRC))]
        [TestCase(typeof(RuntimePatchLayoutEnum_GRC), typeof(RuntimePatchLayoutRemappedEnum_GRC))]
        public void SchemaHashChangesWithWireLayoutOrSemanticMappingAtTheSameNumericIdentity(Type firstType, Type secondType)
        {
            var first = RuntimePatchSchemaDiscovery.DescribeComponent(firstType, 9904);
            var second = RuntimePatchSchemaDiscovery.DescribeComponent(secondType, 9904);
            Assert.That(first.Schema.Fields.Count, Is.EqualTo(second.Schema.Fields.Count));
            Assert.That(first.Schema.Fields[0].FieldId, Is.EqualTo(second.Schema.Fields[0].FieldId));
            Assert.That(first.Schema.Fields[0].Encoding, Is.EqualTo(second.Schema.Fields[0].Encoding));
            var before = RuntimePatchSchemaReconciler.Reconcile(new[] { first.Schema }, "fixed-layout-registry", 1);
            var after = RuntimePatchSchemaReconciler.Reconcile(new[] { second.Schema }, "fixed-layout-registry", 1);
            Assert.That(after.ComponentRegistryHash, Is.EqualTo(before.ComponentRegistryHash));
            Assert.That(after.SchemaHash, Is.Not.EqualTo(before.SchemaHash));
        }

        [Test]
        public void SchemaHashUsesCanonicalMemberLayoutWithoutDependingOnFixtureClrNamesOrDeclarationOrder()
        {
            var first = RuntimePatchSchemaDiscovery.DescribeComponent(typeof(RuntimePatchLayoutStruct_GRC), 9904);
            var equivalent = RuntimePatchSchemaDiscovery.DescribeComponent(typeof(RuntimePatchLayoutEquivalentStruct_GRC), 9904);
            var before = RuntimePatchSchemaReconciler.Reconcile(new[] { first.Schema }, "fixed-layout-registry", 1);
            var after = RuntimePatchSchemaReconciler.Reconcile(new[] { equivalent.Schema }, "fixed-layout-registry", 1);
            Assert.That(after.SchemaHash, Is.EqualTo(before.SchemaHash));
        }

        [Test]
        public void SchemaHashAndEmittedSourceRemainDeterministicAcrossRepeatedAndReorderedDiscovery()
        {
            var first = RuntimePatchSchemaDiscovery.DescribeComponent(typeof(RuntimePatchInheritedFixture_GRC), 9903);
            var second = RuntimePatchSchemaDiscovery.DescribeComponent(typeof(RuntimePatchLayoutInt_GRC), 9904);
            var manifest = RuntimePatchSchemaReconciler.Reconcile(new[] { first.Schema, second.Schema }, "fixed-layout-registry", 1);
            RuntimePatchSchemaGenerationCore.BindReconciledSchema(new[] { first, second }, manifest);
            var profile = new RuntimePatchCodeEmissionProfile("DingoGameObjectsCMS.Tests.Generated", "CmsLayoutPatchRegistry");
            var source = RuntimePatchCodeEmitter.Generate(manifest, new[] { first, second }, profile);
            first.Schema.Fields.Reverse();
            var repeated = RuntimePatchSchemaReconciler.Reconcile(new[] { second.Schema, first.Schema }, "fixed-layout-registry", 1);
            RuntimePatchSchemaGenerationCore.BindReconciledSchema(new[] { first, second }, repeated);
            Assert.That(repeated.SchemaHash, Is.EqualTo(manifest.SchemaHash));
            Assert.That(RuntimePatchSchemaReconciler.CalculateSchemaHash(repeated), Is.EqualTo(repeated.SchemaHash));
            Assert.That(RuntimePatchCodeEmitter.Generate(repeated, new[] { first, second }, profile), Is.EqualTo(source));
        }

        [Test]
        public void SchemaAndEmitter_SupportGenericValueListsHash128AndGameAssetInstance()
        {
            var descriptor = RuntimePatchSchemaDiscovery.DescribeComponent(
                typeof(RuntimePatchCollectionFixture_GRC),
                9901);
            var manifest = RuntimePatchSchemaReconciler.Reconcile(
                new[] { descriptor.Schema },
                "cms-tests",
                1);
            RuntimePatchSchemaGenerationCore.BindReconciledSchema(new[] { descriptor }, manifest);

            var field = descriptor.Fields[0];
            Assert.That(field.ValueType.Kind, Is.EqualTo(RuntimePatchGeneratedValueKind.List));
            Assert.That(field.ValueType.ElementType.RuntimeType, Is.EqualTo(typeof(RuntimePatchPlacementFixture)));
            Assert.That(field.Schema.Encoding, Is.EqualTo(RuntimePatchFieldEncoding.CustomList));
            Assert.That(field.Schema.FieldId, Is.Zero);

            var source = RuntimePatchCodeEmitter.Generate(
                manifest,
                new[] { descriptor },
                new RuntimePatchCodeEmissionProfile(
                    "DingoGameObjectsCMS.Tests.Generated",
                    "CmsGenericListPatchRegistry"));

            Assert.That(source, Does.Contain("RuntimePatchGeneratedValueCodec.RequireCollectionCountForWrite"));
            Assert.That(source, Does.Contain("RuntimePatchGeneratedValueCodec.ReadHash128"));
            Assert.That(source, Does.Contain("RuntimePatchGeneratedValueCodec.WriteRuntimeObjectPatch"));
            Assert.That(source, Does.Contain("RuntimePatchGeneratedValueCodec.CloneRuntimeObjectPatch"));
            Assert.That(source, Does.Contain(
                "global::System.Collections.Generic.List<global::DingoGameObjectsCMS.Tests.Editor.RuntimePatchPlacementFixture>"));
            Assert.That(source, Does.Not.Contain("Newtonsoft"));
        }

        [Test]
        public void SchemaAndEmitterSupportArraysWithoutListCountOrAddCalls()
        {
            var descriptor = RuntimePatchSchemaDiscovery.DescribeComponent(typeof(RuntimePatchArrayFixture_GRC), 9905);
            var manifest = RuntimePatchSchemaReconciler.Reconcile(new[] { descriptor.Schema }, "cms-array-tests", 1);
            RuntimePatchSchemaGenerationCore.BindReconciledSchema(new[] { descriptor }, manifest);
            var source = RuntimePatchCodeEmitter.Generate(manifest, new[] { descriptor }, new RuntimePatchCodeEmissionProfile("DingoGameObjectsCMS.Tests.Generated", "CmsArrayPatchRegistry"));
            Assert.That(descriptor.Fields.ConvertAll(field => field.ValueType.Kind), Is.All.EqualTo(RuntimePatchGeneratedValueKind.Array));
            Assert.That(descriptor.Fields.ConvertAll(field => field.Schema.Encoding), Is.All.EqualTo(RuntimePatchFieldEncoding.CustomList));
            Assert.That(source, Does.Contain("new global::System.Int32[count"));
            Assert.That(source, Does.Contain("new global::System.Single[count"));
            Assert.That(source, Does.Contain("new global::DingoGameObjectsCMS.Tests.Editor.RuntimePatchArrayValue[count"));
            Assert.That(source, Does.Contain("RuntimePatchGeneratedValueCodec.RequireCollectionCountForWrite(value.Length)"));
            Assert.That(source, Does.Contain("first.Length != second.Length"));
            Assert.That(source, Does.Contain("writer.WriteInt32(-1)"));
            Assert.That(source, Does.Contain(".@Indices.Length"));
            Assert.That(source, Does.Contain("RuntimePatchGeneratedValueCodec.ReadCollectionCount"));
            Assert.That(source, Does.Contain("RuntimePatchGeneratedValueCodec.RequireCollectionCountForWrite"));
            Assert.That(source, Does.Not.Contain(".@Integers.Count"));
            Assert.That(source, Does.Not.Contain(".@Floats.Count"));
            Assert.That(source, Does.Not.Contain(".Add(item"));
        }

        [TestCase(typeof(RuntimePatchLayoutJaggedArray_GRC))]
        [TestCase(typeof(RuntimePatchLayoutMultidimensionalArray_GRC))]
        public void SchemaRejectsDirectJaggedAndMultidimensionalArrays(Type type)
        {
            Assert.Throws<InvalidOperationException>(() => RuntimePatchSchemaDiscovery.DescribeComponent(type, 9905));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CanonicalAuthoringRoundTripsArraysNullAndNestedValueArrays(bool useNull)
        {
            var codec = new RuntimePatchArrayObservationCodec();
            var registry = new RuntimePatchCodecRegistry("cms-array-tests");
            registry.Register(codec);
            var context = new RuntimePatchArrayTestContext();
            var authoring = new RuntimeObjectPatchAuthoringCodec(registry);
            var current = new RuntimePatchArrayFixture_GRC
            {
                Floats = useNull ? null : new[] { 0.25f, -2.5f },
                Integers = useNull ? null : Array.Empty<int>(),
                Nested = useNull ? null : new[] { new RuntimePatchArrayValue { Indices = new[] { 17, -23 }, Weight = 0.5f }, new RuntimePatchArrayValue { Indices = null, Weight = 1f } },
            };
            var baseline = new Dictionary<uint, GameRuntimeComponent>();
            var patch = authoring.BuildAuthoringPatch(baseline, new Dictionary<uint, GameRuntimeComponent> { [9905] = current }, context);
            var runtime = authoring.MaterializeRuntimePatch(baseline, patch, context);
            Assert.That(runtime.Components[0].Kind, Is.EqualTo(ComponentPatchKind.Add));
            Assert.That(codec.LastEncoded, Is.Not.SameAs(current));
            Assert.That(codec.LastEncoded.Floats, Is.EqualTo(current.Floats));
            Assert.That(codec.LastEncoded.Integers, Is.EqualTo(current.Integers));
            if (useNull)
                Assert.That(codec.LastEncoded.Nested, Is.Null);
            else
            {
                Assert.That(codec.LastEncoded.Floats, Is.Not.SameAs(current.Floats));
                Assert.That(codec.LastEncoded.Nested[0].Indices, Is.EqualTo(new[] { 17, -23 }));
                Assert.That(codec.LastEncoded.Nested[0].Indices, Is.Not.SameAs(current.Nested[0].Indices));
                Assert.That(codec.LastEncoded.Nested[1].Indices, Is.Null);
                Assert.That(codec.LastEncoded.Nested[0].Weight, Is.EqualTo(0.5f));
            }
        }

        [Test]
        public void CanonicalAuthoringRejectsOversizedArraysBeforeSerializingItems()
        {
            var codec = new RuntimePatchArrayObservationCodec();
            var registry = new RuntimePatchCodecRegistry("cms-array-size-tests");
            registry.Register(codec);
            var authoring = new RuntimeObjectPatchAuthoringCodec(registry);
            var current = new RuntimePatchArrayFixture_GRC { Integers = new int[RuntimePatchGeneratedValueCodec.MAX_COLLECTION_ELEMENTS + 1] };
            Assert.Throws<InvalidOperationException>(() => authoring.BuildAuthoringPatch(new Dictionary<uint, GameRuntimeComponent>(), new Dictionary<uint, GameRuntimeComponent> { [9905] = current }, new RuntimePatchArrayTestContext()));
        }

        [Test]
        public void Vector2IntList_KeepsItsSpecializedEncoding()
        {
            var descriptor = RuntimePatchSchemaDiscovery.DescribeComponent(
                typeof(RuntimePatchLegacyListFixture_GRC),
                9902);
            var field = descriptor.Fields[0];

            Assert.That(field.ValueType.Kind, Is.EqualTo(RuntimePatchGeneratedValueKind.ListVector2Int));
            Assert.That(field.Schema.FieldId, Is.Zero);
            Assert.That(field.Schema.Encoding, Is.EqualTo(RuntimePatchFieldEncoding.CustomListVector2Int));
        }

        [Test]
        public void SchemaAndEmitter_IncludeInheritedSerializedFields()
        {
            var descriptor = RuntimePatchSchemaDiscovery.DescribeComponent(
                typeof(RuntimePatchInheritedFixture_GRC),
                9903);

            Assert.That(
                descriptor.Fields.ConvertAll(field => field.Field.Name),
                Is.EqualTo(new[]
                {
                    nameof(RuntimePatchInheritedFixtureBase_GRC.BaseValue),
                    nameof(RuntimePatchInheritedFixture_GRC.LeafValue),
                }));

            var manifest = RuntimePatchSchemaReconciler.Reconcile(
                new[] { descriptor.Schema },
                "cms-tests-inheritance",
                1);
            RuntimePatchSchemaGenerationCore.BindReconciledSchema(
                new[] { descriptor },
                manifest);
            var source = RuntimePatchCodeEmitter.Generate(
                manifest,
                new[] { descriptor },
                new RuntimePatchCodeEmissionProfile(
                    "DingoGameObjectsCMS.Tests.Generated",
                    "CmsInheritedPatchRegistry"));

            Assert.That(source, Does.Contain("value.@BaseValue"));
            Assert.That(source, Does.Contain("value.@LeafValue"));
        }

        [TestCase(RuntimeObjectPatchRepresentation.RuntimeBinary)]
        [TestCase(RuntimeObjectPatchRepresentation.AuthoringCanonicalJson)]
        public void NestedPatchCodec_RoundTripsAndDeepClones(RuntimeObjectPatchRepresentation representation)
        {
            var patch = representation == RuntimeObjectPatchRepresentation.RuntimeBinary
                ? RuntimePatch()
                : AuthoringPatch();

            var encoded = RuntimePatchGeneratedValueCodec.EncodeRuntimeObjectPatch(patch);
            var decoded = RuntimePatchGeneratedValueCodec.DecodeRuntimeObjectPatch(encoded);
            var clone = RuntimePatchGeneratedValueCodec.CloneRuntimeObjectPatch(patch);

            Assert.That(RuntimePatchGeneratedValueCodec.RuntimeObjectPatchesEqual(patch, decoded), Is.True);
            Assert.That(RuntimePatchGeneratedValueCodec.RuntimeObjectPatchesEqual(patch, clone), Is.True);
            Assert.That(clone, Is.Not.SameAs(patch));
            Assert.That(clone.Components[0], Is.Not.SameAs(patch.Components[0]));

            if (representation == RuntimeObjectPatchRepresentation.RuntimeBinary)
                clone.Components[0].Payload[0] ^= 0xff;
            else
                clone.Components[0].CanonicalJson = "{\"changed\":true}";

            Assert.That(RuntimePatchGeneratedValueCodec.RuntimeObjectPatchesEqual(patch, clone), Is.False);
        }

        [Test]
        public void CollectionAndHashReaders_RejectNonCanonicalOrOversizedInput()
        {
            using var listWriter = new CanonicalPatchBinaryWriter();
            listWriter.WriteInt32(RuntimePatchGeneratedValueCodec.MAX_COLLECTION_ELEMENTS + 1);
            Assert.Throws<FormatException>(() =>
                RuntimePatchGeneratedValueCodec.ReadCollectionCount(
                    new CanonicalPatchBinaryReader(listWriter.ToArray())));

            using var hashWriter = new CanonicalPatchBinaryWriter();
            hashWriter.WriteString("ABCDEF0123456789abcdef0123456789");
            Assert.Throws<FormatException>(() =>
                RuntimePatchGeneratedValueCodec.ReadHash128(
                    new CanonicalPatchBinaryReader(hashWriter.ToArray())));
            Assert.Throws<FormatException>(() =>
                new CanonicalPatchBinaryReader(hashWriter.ToArray()).ReadHash128());
        }

        private static RuntimeObjectPatch RuntimePatch()
        {
            var patch = new RuntimeObjectPatch("runtime-schema");
            patch.Components.Add(new ComponentPatch(
                7,
                ComponentPatchKind.Add,
                new byte[] { 1, 2, 3 }));
            return patch;
        }

        private static RuntimeObjectPatch AuthoringPatch()
        {
            var patch = new RuntimeObjectPatch(
                "authoring-schema",
                RuntimeObjectPatchRepresentation.AuthoringCanonicalJson);
            patch.Components.Add(ComponentPatch.Authoring(
                8,
                ComponentPatchKind.Add,
                "{}"));
            return patch;
        }
    }
}
