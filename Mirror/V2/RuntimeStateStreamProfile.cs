using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeStateStreamMembership : byte
    {
        Independent = 1,
        AcknowledgedRuntimeObject = 2,
    }

    public enum RuntimeStateStreamLifetime : byte
    {
        StructuralRuntimeObject = 1,
        EphemeralStreamEntity = 2,
    }

    public abstract class RuntimeStateStreamProfile
    {
        public readonly uint StreamTypeId;
        public readonly string StreamName;
        public readonly string StableKey;
        public readonly uint CodecSchemaVersion;
        public readonly string CodecSchemaHash;
        public readonly RuntimeStateStreamMembership Membership;
        public readonly RuntimeStateStreamLifetime Lifetime;

        protected RuntimeStateStreamProfile(
            uint streamTypeId,
            string streamName,
            string stableKey,
            uint codecSchemaVersion,
            string codecSchemaHash,
            RuntimeStateStreamMembership membership,
            RuntimeStateStreamLifetime lifetime)
        {
            if (streamTypeId == 0)
                throw new ArgumentOutOfRangeException(nameof(streamTypeId));
            if (string.IsNullOrWhiteSpace(streamName))
                throw new ArgumentException("State stream name is required.", nameof(streamName));
            if (string.IsNullOrWhiteSpace(stableKey))
                throw new ArgumentException("State stream stable key is required.", nameof(stableKey));
            if (codecSchemaVersion == 0)
                throw new ArgumentOutOfRangeException(nameof(codecSchemaVersion));
            if (!IsSha256(codecSchemaHash))
                throw new ArgumentException("State stream codec schema hash must be lowercase SHA-256 hex.", nameof(codecSchemaHash));
            if (!Enum.IsDefined(typeof(RuntimeStateStreamMembership), membership))
                throw new ArgumentOutOfRangeException(nameof(membership));
            if (!Enum.IsDefined(typeof(RuntimeStateStreamLifetime), lifetime))
                throw new ArgumentOutOfRangeException(nameof(lifetime));
            if (lifetime == RuntimeStateStreamLifetime.EphemeralStreamEntity
                && membership != RuntimeStateStreamMembership.Independent)
            {
                throw new ArgumentException("Ephemeral state streams cannot require RuntimeObject membership.", nameof(membership));
            }
            StreamTypeId = streamTypeId;
            StreamName = streamName;
            StableKey = stableKey;
            CodecSchemaVersion = codecSchemaVersion;
            CodecSchemaHash = codecSchemaHash;
            Membership = membership;
            Lifetime = lifetime;
        }

        public abstract void ValidatePackedSample(in RuntimePackedStateStreamSample sample);

        private static bool IsSha256(string value)
        {
            if (value == null || value.Length != 64)
                return false;
            for (var i = 0; i < value.Length; i++)
            {
                var character = value[i];
                if ((character < '0' || character > '9') && (character < 'a' || character > 'f'))
                    return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Typed hot-state contract. The profile owns quantization and complete
    /// sample packing. RuntimeObjectPatch, component semantic diff and
    /// per-connection component shadows are deliberately absent.
    /// </summary>
    public abstract class RuntimeStateStreamProfile<TSample> : RuntimeStateStreamProfile
    {
        protected RuntimeStateStreamProfile(
            uint streamTypeId,
            string streamName,
            string stableKey,
            uint codecSchemaVersion,
            string codecSchemaHash,
            RuntimeStateStreamMembership membership,
            RuntimeStateStreamLifetime lifetime)
            : base(streamTypeId, streamName, stableKey, codecSchemaVersion, codecSchemaHash, membership, lifetime) { }

        public abstract RuntimeStateStreamKey TakeKey(in TSample sample);
        public abstract bool IsStop(in TSample sample);
        public abstract void Validate(in TSample sample);
        public abstract byte[] Pack(in TSample sample);
        public abstract TSample Unpack(RuntimeStateStreamKey key, byte[] packedState);

        public RuntimePackedStateStreamSample PackSample(in TSample sample)
        {
            Validate(sample);
            var key = TakeKey(sample);
            var packed = Pack(sample);
            return new RuntimePackedStateStreamSample(
                key,
                IsStop(sample) ? RuntimeStateStreamSampleFlags.Stop : RuntimeStateStreamSampleFlags.None,
                packed);
        }

        public TSample UnpackSample(in RuntimePackedStateStreamSample sample)
        {
            if (sample.IsDespawn)
            {
                ValidateDespawnSample(sample);
                throw new FormatException(
                    $"{StreamName} despawn sample '{sample.Key}' cannot be unpacked as typed state.");
            }
            return UnpackAndValidate(sample);
        }

        public override void ValidatePackedSample(in RuntimePackedStateStreamSample sample)
        {
            if (sample.IsDespawn)
            {
                ValidateDespawnSample(sample);
                return;
            }

            UnpackAndValidate(sample);
        }

        private TSample UnpackAndValidate(in RuntimePackedStateStreamSample sample)
        {
            var decoded = Unpack(sample.Key, sample.PackedState);
            Validate(decoded);
            if (TakeKey(decoded) != sample.Key)
                throw new FormatException($"{StreamName} decoder changed sample key '{sample.Key}'.");
            var expectedFlags = IsStop(decoded)
                ? RuntimeStateStreamSampleFlags.Stop
                : RuntimeStateStreamSampleFlags.None;
            if (expectedFlags != sample.Flags)
                throw new FormatException($"{StreamName} sample '{sample.Key}' stop flag is not canonical.");
            var canonical = Pack(decoded);
            if (!BytesEqual(sample.PackedState, canonical))
                throw new FormatException($"{StreamName} sample '{sample.Key}' packed payload is not canonical.");
            return decoded;
        }

        private void ValidateDespawnSample(in RuntimePackedStateStreamSample sample)
        {
            if (sample.PackedState == null || sample.PackedState.Length != 0 || sample.IsStop)
                throw new FormatException($"{StreamName} despawn sample '{sample.Key}' is not canonical.");
        }

        private static bool BytesEqual(byte[] first, byte[] second)
        {
            if (ReferenceEquals(first, second))
                return true;
            if (first == null || second == null || first.Length != second.Length)
                return false;
            for (var i = 0; i < first.Length; i++)
            {
                if (first[i] != second[i])
                    return false;
            }
            return true;
        }
    }

    public class RuntimeStateStreamProfileRegistry
    {
        private readonly Dictionary<uint, RuntimeStateStreamProfile> _profiles = new();
        private readonly Dictionary<string, RuntimeStateStreamProfile> _profilesByStableKey =
            new(StringComparer.Ordinal);

        public int Count => _profiles.Count;
        public bool IsSealed { get; private set; }
        public string CatalogHash { get; private set; }

        public void Register(RuntimeStateStreamProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (IsSealed)
                throw new InvalidOperationException("State stream profile registry is sealed.");
            if (!_profiles.TryAdd(profile.StreamTypeId, profile))
            {
                throw new InvalidOperationException(
                    $"State stream type id {profile.StreamTypeId} is already registered as '{_profiles[profile.StreamTypeId].StreamName}'.");
            }
            if (!_profilesByStableKey.TryAdd(profile.StableKey, profile))
            {
                _profiles.Remove(profile.StreamTypeId);
                throw new InvalidOperationException(
                    $"State stream stable key '{profile.StableKey}' is already registered as '{_profilesByStableKey[profile.StableKey].StreamName}'.");
            }
        }

        public void Unregister(RuntimeStateStreamProfile profile)
        {
            if (profile == null)
                throw new ArgumentNullException(nameof(profile));
            if (IsSealed)
                throw new InvalidOperationException("State stream profile registry is sealed.");
            if (_profiles.TryGetValue(profile.StreamTypeId, out var current) && ReferenceEquals(current, profile))
            {
                _profiles.Remove(profile.StreamTypeId);
                _profilesByStableKey.Remove(profile.StableKey);
            }
        }

        public bool TryGet(uint streamTypeId, out RuntimeStateStreamProfile profile)
        {
            return _profiles.TryGetValue(streamTypeId, out profile);
        }

        public RuntimeStateStreamProfile GetRequired(uint streamTypeId)
        {
            if (_profiles.TryGetValue(streamTypeId, out var profile))
                return profile;
            throw new KeyNotFoundException($"State stream type id {streamTypeId} is not registered.");
        }

        public void Seal()
        {
            if (IsSealed)
                return;
            var profiles = new List<RuntimeStateStreamProfile>(_profiles.Values);
            profiles.Sort((first, second) => first.StreamTypeId.CompareTo(second.StreamTypeId));
            var canonical = new StringBuilder();
            for (var i = 0; i < profiles.Count; i++)
            {
                var profile = profiles[i];
                Append(canonical, profile.StreamTypeId.ToString(CultureInfo.InvariantCulture));
                Append(canonical, profile.StableKey);
                Append(canonical, profile.StreamName);
                Append(canonical, profile.CodecSchemaVersion.ToString(CultureInfo.InvariantCulture));
                Append(canonical, profile.CodecSchemaHash);
                Append(canonical, ((byte)profile.Membership).ToString(CultureInfo.InvariantCulture));
                Append(canonical, ((byte)profile.Lifetime).ToString(CultureInfo.InvariantCulture));
            }
            using var sha = SHA256.Create();
            var digest = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
            var result = new StringBuilder(digest.Length * 2);
            for (var i = 0; i < digest.Length; i++)
            {
                result.Append(digest[i].ToString("x2"));
            }
            CatalogHash = result.ToString();
            IsSealed = true;
        }

        public void Clear()
        {
            if (IsSealed)
                throw new InvalidOperationException("State stream profile registry is sealed.");
            _profiles.Clear();
            _profilesByStableKey.Clear();
        }

        public static RuntimeStateStreamProfileRegistry CreateEmptySealed()
        {
            var result = new RuntimeStateStreamProfileRegistry();
            result.Seal();
            return result;
        }

        private static void Append(StringBuilder target, string value)
        {
            value ??= string.Empty;
            target.Append(value.Length).Append(':').Append(value).Append('|');
        }
    }

    public static class RuntimeStateStreamQuantization
    {
        public static ushort PackUnit(float value)
        {
            RequireFinite(value, nameof(value));
            if (value < 0f || value > 1f)
                throw new ArgumentOutOfRangeException(nameof(value), "Unit value must be in 0..1.");
            return (ushort)Math.Round(value * ushort.MaxValue, MidpointRounding.AwayFromZero);
        }

        public static float UnpackUnit(ushort value) => value / (float)ushort.MaxValue;

        public static short PackSigned(float value, float unitsPerStep)
        {
            RequireFinite(value, nameof(value));
            RequirePositiveFinite(unitsPerStep, nameof(unitsPerStep));
            var quantized = Math.Round(value / unitsPerStep, MidpointRounding.AwayFromZero);
            if (quantized < short.MinValue || quantized > short.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} exceeds signed stream quantization range.");
            return (short)quantized;
        }

        public static float UnpackSigned(short value, float unitsPerStep)
        {
            RequirePositiveFinite(unitsPerStep, nameof(unitsPerStep));
            return value * unitsPerStep;
        }

        public static ushort PackUnsigned(float value, float unitsPerStep)
        {
            RequireFinite(value, nameof(value));
            RequirePositiveFinite(unitsPerStep, nameof(unitsPerStep));
            var quantized = Math.Round(value / unitsPerStep, MidpointRounding.AwayFromZero);
            if (quantized < 0 || quantized > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), $"Value {value} exceeds unsigned stream quantization range.");
            return (ushort)quantized;
        }

        public static float UnpackUnsigned(ushort value, float unitsPerStep)
        {
            RequirePositiveFinite(unitsPerStep, nameof(unitsPerStep));
            return value * unitsPerStep;
        }

        private static void RequireFinite(float value, string parameterName)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
                throw new ArgumentOutOfRangeException(parameterName, "State stream quantization requires a finite value.");
        }

        private static void RequirePositiveFinite(float value, string parameterName)
        {
            RequireFinite(value, parameterName);
            if (value <= 0f)
                throw new ArgumentOutOfRangeException(parameterName, "State stream quantization step must be positive.");
        }
    }
}
