using System;
using DingoGameObjectsCMS.RuntimeObjects;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    /// <summary>
    /// Canonical context for immutable GameAsset blueprints. A GA baseline may
    /// contain an empty reference, but it may never capture a runtime object.
    /// Instance-specific references belong in the sparse override patch.
    /// </summary>
    public sealed class RuntimeTemplatePatchCodecContext : RuntimePatchCodecContext
    {
        public static readonly RuntimeTemplatePatchCodecContext Instance = new();

        private RuntimeTemplatePatchCodecContext() { }

        public override void WriteRuntimeInstance(CanonicalPatchBinaryWriter writer, in RuntimeInstance value)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (!IsDefault(value))
                throw new InvalidOperationException("A GameAsset baseline cannot contain a runtime object reference.");
            writer.WriteBoolean(false);
        }

        public override RuntimeInstance ReadRuntimeInstance(CanonicalPatchBinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (reader.ReadBoolean())
                throw new FormatException("A GameAsset baseline contains a forbidden runtime object reference.");
            return default;
        }

        public override bool RuntimeInstancesEqual(in RuntimeInstance first, in RuntimeInstance second)
        {
            if (!IsDefault(first) || !IsDefault(second))
                throw new InvalidOperationException("GameAsset template comparison cannot resolve runtime object references.");
            return true;
        }

        private static bool IsDefault(in RuntimeInstance value)
        {
            return value.StoreId.Length == 0 && value.Id == 0 && value.Epoch == 0;
        }
    }
}
