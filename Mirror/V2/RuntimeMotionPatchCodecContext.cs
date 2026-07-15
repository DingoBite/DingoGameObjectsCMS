using System;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;

namespace DingoGameObjectsCMS.Mirror.V2
{
    /// <summary>
    /// Canonical component-state context for the unreliable motion channel.
    /// Motion samples are self-contained state values and cannot carry object
    /// references whose lifetime or membership may differ at interpolation
    /// time.
    /// </summary>
    public sealed class RuntimeMotionPatchCodecContext : RuntimePatchCodecContext
    {
        public static readonly RuntimeMotionPatchCodecContext Instance = new();

        private RuntimeMotionPatchCodecContext() { }

        public override void WriteRuntimeInstance(CanonicalPatchBinaryWriter writer, in RuntimeInstance value)
        {
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (!IsDefault(value))
            {
                throw new InvalidOperationException(
                    "Unreliable motion component state cannot contain a non-default RuntimeInstance reference.");
            }

            writer.WriteBoolean(false);
        }

        public override RuntimeInstance ReadRuntimeInstance(CanonicalPatchBinaryReader reader)
        {
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));
            if (reader.ReadBoolean())
            {
                throw new FormatException(
                    "Unreliable motion component state cannot contain a RuntimeInstance reference.");
            }

            return default;
        }

        public override bool RuntimeInstancesEqual(in RuntimeInstance first, in RuntimeInstance second)
        {
            if (!IsDefault(first) || !IsDefault(second))
            {
                throw new InvalidOperationException(
                    "Unreliable motion component state cannot compare non-default RuntimeInstance references.");
            }

            return true;
        }

        private static bool IsDefault(in RuntimeInstance value)
        {
            return value.StoreId.Length == 0 && value.Id == 0 && value.Epoch == 0;
        }
    }
}
