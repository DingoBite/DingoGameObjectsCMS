using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeReplicationPolicy : byte
    {
        Never = 0,
        BaselineAndReliableOverrides = 1,
        UnreliableState = 2,
    }

    public readonly struct RuntimeReplicationPolicyEntry
    {
        public readonly Type ComponentType;
        public readonly string ComponentTypeKey;
        public readonly RuntimeReplicationPolicy Policy;

        public RuntimeReplicationPolicyEntry(Type componentType, RuntimeReplicationPolicy policy)
        {
            if (componentType == null)
                throw new ArgumentNullException(nameof(componentType));
            if (componentType.IsAbstract || !typeof(GameRuntimeComponent).IsAssignableFrom(componentType))
            {
                throw new ArgumentException(
                    $"Replication policy type '{componentType.FullName}' must be a concrete {nameof(GameRuntimeComponent)}.",
                    nameof(componentType));
            }

            ComponentType = componentType;
            ComponentTypeKey = RuntimeComponentTypeRegistry.GetKey(componentType);
            Policy = policy;
        }

        public static RuntimeReplicationPolicyEntry For<TComponent>(RuntimeReplicationPolicy policy)
            where TComponent : GameRuntimeComponent
        {
            return new RuntimeReplicationPolicyEntry(typeof(TComponent), policy);
        }
    }

    public sealed class RuntimeReplicationPolicyRegistry
    {
        private readonly Dictionary<uint, RuntimeReplicationPolicy> _policies = new();
        private bool _sealed;

        public bool IsSealed => _sealed;
        public string PolicyHash { get; private set; }

        public static RuntimeReplicationPolicyRegistry CreateAndSeal(
            IReadOnlyList<RuntimeReplicationPolicyEntry> entries)
        {
            if (!RuntimeComponentTypeRegistry.IsInitialized)
            {
                throw new InvalidOperationException(
                    "Runtime component type registry must be initialized before replication policies are installed.");
            }
            if (entries == null)
                throw new ArgumentNullException(nameof(entries));

            var registry = new RuntimeReplicationPolicyRegistry();
            var registeredTypes = new HashSet<Type>();
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                ValidateEntry(entry, registeredTypes);
                if (!RuntimeComponentTypeRegistry.TryGetId(entry.ComponentType, out var componentTypeId))
                {
                    throw new InvalidOperationException(
                        $"Runtime component '{entry.ComponentTypeKey}' has a replication policy but is missing from the active runtime type manifest.");
                }
                registry.Register(componentTypeId, entry.Policy);
            }

            registry.SealAndValidateRuntimeSchema();
            return registry;
        }

        public void Register<TComponent>(RuntimeReplicationPolicy policy)
            where TComponent : GameRuntimeComponent
        {
            if (!RuntimeComponentTypeRegistry.TryGetId(typeof(TComponent), out var componentTypeId))
                throw new InvalidOperationException($"Runtime component '{typeof(TComponent).FullName}' is missing from the runtime type manifest.");
            Register(componentTypeId, policy);
        }

        public void Register(uint componentTypeId, RuntimeReplicationPolicy policy)
        {
            if (_sealed)
                throw new InvalidOperationException("Replication policy registry is already sealed.");
            if (!_policies.TryAdd(componentTypeId, policy))
                throw new InvalidOperationException($"Runtime component id '{componentTypeId}' has duplicate replication policy.");
        }

        public void SealAndValidateRuntimeSchema()
        {
            if (!RuntimeComponentTypeRegistry.IsInitialized)
                throw new InvalidOperationException("Runtime component type registry must be initialized before replication policies are sealed.");

            var required = new List<uint>();
            var types = RuntimeComponentTypeRegistry.TypesById;
            for (var i = 0; i < types.Count; i++)
            {
                if (types[i] != null)
                    required.Add((uint)i);
            }

            Seal(required);
        }

        public void Seal(IEnumerable<uint> requiredComponentTypeIds)
        {
            if (_sealed)
                throw new InvalidOperationException("Replication policy registry is already sealed.");
            if (requiredComponentTypeIds == null)
                throw new ArgumentNullException(nameof(requiredComponentTypeIds));

            foreach (var componentTypeId in requiredComponentTypeIds.Distinct())
            {
                if (!_policies.ContainsKey(componentTypeId))
                    throw new InvalidOperationException($"Runtime component id '{componentTypeId}' has no explicit replication policy.");
            }

            _sealed = true;
            PolicyHash = CalculateHash(_policies);
        }

        public RuntimeReplicationPolicy GetRequired(uint componentTypeId)
        {
            if (!_sealed)
                throw new InvalidOperationException("Replication policy registry is not sealed.");
            if (!_policies.TryGetValue(componentTypeId, out var policy))
                throw new InvalidOperationException($"Runtime component id '{componentTypeId}' has no explicit replication policy.");
            return policy;
        }

        public bool TryGet(uint componentTypeId, out RuntimeReplicationPolicy policy)
        {
            if (!_sealed)
                throw new InvalidOperationException("Replication policy registry is not sealed.");
            return _policies.TryGetValue(componentTypeId, out policy);
        }

        public bool TryGetDataClass(
            uint componentTypeId,
            out RuntimeReplicationDataClass dataClass)
        {
            return RuntimeReplicationDataClassification.TryClassifyState(
                GetRequired(componentTypeId),
                out dataClass);
        }

        public void ValidateExactCoverage(
            RuntimeReplicationPolicy policy,
            IEnumerable<uint> coveredComponentTypeIds,
            string coverageName)
        {
            if (!_sealed)
                throw new InvalidOperationException("Replication policy registry is not sealed.");

            var expected = new HashSet<uint>();
            foreach (var pair in _policies)
            {
                if (pair.Value == policy)
                    expected.Add(pair.Key);
            }

            RuntimeReplicationPolicyConfiguration.ValidateExactCoverage(
                expected,
                coveredComponentTypeIds,
                policy,
                coverageName);
        }

        private static string CalculateHash(IReadOnlyDictionary<uint, RuntimeReplicationPolicy> policies)
        {
            var builder = new StringBuilder();
            foreach (var pair in policies.OrderBy(pair => pair.Key))
                builder.Append(pair.Key).Append('|').Append((byte)pair.Value).Append('\n');

            using var sha = SHA256.Create();
            return RuntimeSessionCatalogHasher.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static void ValidateEntry(
            in RuntimeReplicationPolicyEntry entry,
            HashSet<Type> registeredTypes)
        {
            if (entry.ComponentType == null || string.IsNullOrWhiteSpace(entry.ComponentTypeKey))
                throw new InvalidOperationException("Replication policy entries cannot contain a default value.");
            if (!registeredTypes.Add(entry.ComponentType))
            {
                throw new InvalidOperationException(
                    $"Runtime component '{entry.ComponentTypeKey}' has duplicate replication policy entries.");
            }
        }
    }

    public static class RuntimeReplicationPolicyConfiguration
    {
        public static void ValidateManifest(
            Manifest runtimeManifest,
            IReadOnlyList<RuntimeReplicationPolicyEntry> policyEntries)
        {
            CreateManifestPolicyMap(runtimeManifest, policyEntries);
        }

        public static void ValidateExactCoverage(
            Manifest runtimeManifest,
            IReadOnlyList<RuntimeReplicationPolicyEntry> policyEntries,
            RuntimeReplicationPolicy policy,
            IEnumerable<Type> coveredComponentTypes,
            string coverageName)
        {
            var policiesById = CreateManifestPolicyMap(runtimeManifest, policyEntries);
            var componentIdByKey = CreateManifestComponentIdByKey(runtimeManifest);
            if (coveredComponentTypes == null)
                throw new ArgumentNullException(nameof(coveredComponentTypes));

            var coveredIds = new List<uint>();
            var coveredKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var componentType in coveredComponentTypes)
            {
                if (componentType == null)
                    throw new InvalidOperationException($"{RequireCoverageName(coverageName)} contains a null component type.");
                var componentTypeKey = RuntimeComponentTypeRegistry.GetKey(componentType);
                if (!coveredKeys.Add(componentTypeKey))
                {
                    throw new InvalidOperationException(
                        $"{RequireCoverageName(coverageName)} contains duplicate component '{componentTypeKey}'.");
                }
                if (!componentIdByKey.TryGetValue(componentTypeKey, out var componentTypeId))
                {
                    throw new InvalidOperationException(
                        $"{RequireCoverageName(coverageName)} contains component '{componentTypeKey}' that is missing from the runtime type manifest.");
                }
                coveredIds.Add(componentTypeId);
            }

            var expectedIds = new List<uint>();
            foreach (var pair in policiesById)
            {
                if (pair.Value == policy)
                    expectedIds.Add(pair.Key);
            }
            ValidateExactCoverage(expectedIds, coveredIds, policy, coverageName);
        }

        public static void ValidateExactCoverage(
            IEnumerable<uint> expectedComponentTypeIds,
            IEnumerable<uint> coveredComponentTypeIds,
            RuntimeReplicationPolicy policy,
            string coverageName)
        {
            if (expectedComponentTypeIds == null)
                throw new ArgumentNullException(nameof(expectedComponentTypeIds));
            if (coveredComponentTypeIds == null)
                throw new ArgumentNullException(nameof(coveredComponentTypeIds));

            var expected = new HashSet<uint>(expectedComponentTypeIds);
            var covered = new HashSet<uint>();
            foreach (var componentTypeId in coveredComponentTypeIds)
            {
                if (!covered.Add(componentTypeId))
                {
                    throw new InvalidOperationException(
                        $"{RequireCoverageName(coverageName)} contains duplicate runtime component id {componentTypeId}.");
                }
            }

            if (expected.SetEquals(covered))
                return;

            var missing = expected.Where(componentTypeId => !covered.Contains(componentTypeId)).OrderBy(value => value);
            var extra = covered.Where(componentTypeId => !expected.Contains(componentTypeId)).OrderBy(value => value);
            throw new InvalidOperationException(
                $"{RequireCoverageName(coverageName)} must exactly cover {policy} runtime components. "
                + $"Missing=[{string.Join(",", missing)}], Extra=[{string.Join(",", extra)}].");
        }

        private static Dictionary<uint, RuntimeReplicationPolicy> CreateManifestPolicyMap(
            Manifest runtimeManifest,
            IReadOnlyList<RuntimeReplicationPolicyEntry> policyEntries)
        {
            if (runtimeManifest?.Types == null)
                throw new ArgumentNullException(nameof(runtimeManifest), "Runtime type manifest and its Types collection are required.");
            if (policyEntries == null)
                throw new ArgumentNullException(nameof(policyEntries));

            var componentIdByKey = CreateManifestComponentIdByKey(runtimeManifest);
            var result = new Dictionary<uint, RuntimeReplicationPolicy>();
            var policyKeys = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < policyEntries.Count; i++)
            {
                var entry = policyEntries[i];
                if (entry.ComponentType == null || string.IsNullOrWhiteSpace(entry.ComponentTypeKey))
                    throw new InvalidOperationException("Replication policy entries cannot contain a default value.");
                if (!policyKeys.Add(entry.ComponentTypeKey))
                {
                    throw new InvalidOperationException(
                        $"Runtime component '{entry.ComponentTypeKey}' has duplicate replication policy entries.");
                }
                if (!componentIdByKey.TryGetValue(entry.ComponentTypeKey, out var componentTypeId))
                {
                    throw new InvalidOperationException(
                        $"Runtime component '{entry.ComponentTypeKey}' has a replication policy but is missing from the runtime type manifest.");
                }
                result.Add(componentTypeId, entry.Policy);
            }

            foreach (var pair in componentIdByKey)
            {
                if (!result.ContainsKey(pair.Value))
                {
                    throw new InvalidOperationException(
                        $"Runtime component '{pair.Key}' (id {pair.Value}) has no explicit replication policy.");
                }
            }
            return result;
        }

        private static Dictionary<string, uint> CreateManifestComponentIdByKey(Manifest runtimeManifest)
        {
            if (runtimeManifest?.Types == null)
                throw new ArgumentNullException(nameof(runtimeManifest), "Runtime type manifest and its Types collection are required.");

            var result = new Dictionary<string, uint>(StringComparer.Ordinal);
            var ids = new HashSet<int>();
            for (var i = 0; i < runtimeManifest.Types.Count; i++)
            {
                var entry = runtimeManifest.Types[i]
                            ?? throw new InvalidOperationException($"Runtime type manifest contains a null entry at index {i}.");
                if (entry.Id < 0)
                    throw new InvalidOperationException($"Runtime type manifest contains negative component id {entry.Id}.");
                if (string.IsNullOrWhiteSpace(entry.Key))
                    throw new InvalidOperationException($"Runtime type manifest component id {entry.Id} has no stable key.");
                if (!ids.Add(entry.Id))
                    throw new InvalidOperationException($"Runtime type manifest contains duplicate component id {entry.Id}.");
                if (!result.TryAdd(entry.Key, (uint)entry.Id))
                    throw new InvalidOperationException($"Runtime type manifest contains duplicate component key '{entry.Key}'.");
            }
            return result;
        }

        private static string RequireCoverageName(string coverageName)
        {
            if (string.IsNullOrWhiteSpace(coverageName))
                throw new ArgumentException("Coverage name is required.", nameof(coverageName));
            return coverageName.Trim();
        }
    }

    public static class RuntimeReplicationPolicies
    {
        public static RuntimeReplicationPolicyRegistry Current { get; private set; }

        public static void Install(RuntimeReplicationPolicyRegistry registry)
        {
            if (registry == null)
                throw new ArgumentNullException(nameof(registry));
            if (!registry.IsSealed)
                throw new InvalidOperationException("Cannot install an unsealed replication policy registry.");
            Current = registry;
        }

        public static void Clear() => Current = null;
    }
}
