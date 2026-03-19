using System;

namespace DingoGameObjectsCMS.Serialization
{
    public static class RuntimePayloadSerialization
    {
        private static IRuntimePayloadSerializer _serializer = CreateDefaultSerializer();

        public static IRuntimePayloadSerializer Current =>
            _serializer ?? throw new InvalidOperationException(
                "Runtime payload serializer is not configured. Register one via RuntimePayloadSerialization.SetSerializer(...).");

        public static void SetSerializer(IRuntimePayloadSerializer serializer)
        {
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        }

        public static void ResetToDefault()
        {
            _serializer = CreateDefaultSerializer();
        }

        private static IRuntimePayloadSerializer CreateDefaultSerializer()
        {
#if NEWTONSOFT_EXISTS
            return new JsonRuntimePayloadSerializer();
#else
            return null;
#endif
        }
    }
}
