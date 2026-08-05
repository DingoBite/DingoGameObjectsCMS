using System;
using System.Collections.Generic;
using DingoGameObjectsCMS.Editor;
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

    public class RuntimePatchGeneratedValueCodecTests
    {
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
            var listWriter = new CanonicalPatchBinaryWriter();
            listWriter.WriteInt32(RuntimePatchGeneratedValueCodec.MAX_COLLECTION_ELEMENTS + 1);
            Assert.Throws<FormatException>(() =>
                RuntimePatchGeneratedValueCodec.ReadCollectionCount(
                    new CanonicalPatchBinaryReader(listWriter.ToArray())));

            var hashWriter = new CanonicalPatchBinaryWriter();
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
