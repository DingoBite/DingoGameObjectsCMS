using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public readonly struct RuntimeStateStreamCollectorEntry<TSample>
    {
        public readonly ulong Version;
        public readonly TSample Sample;

        public RuntimeStateStreamCollectorEntry(ulong version, TSample sample)
        {
            Version = version;
            Sample = sample;
        }
    }

    public class RuntimeStateStreamCollectorSnapshot<TSample>
    {
        public readonly ulong Version;
        public readonly IReadOnlyList<TSample> Samples;
        public readonly IReadOnlyList<RuntimeStateStreamKey> RemovedKeys;

        public RuntimeStateStreamCollectorSnapshot(
            ulong version,
            IReadOnlyList<TSample> samples,
            IReadOnlyList<RuntimeStateStreamKey> removedKeys)
        {
            Version = version;
            Samples = samples ?? throw new ArgumentNullException(nameof(samples));
            RemovedKeys = removedKeys ?? throw new ArgumentNullException(nameof(removedKeys));
        }
    }

    /// <summary>
    /// Source-agnostic typed hot-state collector. ECS systems, a RuntimeStore
    /// bridge or another simulation source publish complete typed samples.
    /// The collector never computes semantic field/component diffs.
    /// </summary>
    public class RuntimeStateStreamCollector<TSample>
    {
        private readonly RuntimeStateStreamProfile<TSample> _profile;
        private readonly Dictionary<RuntimeStateStreamKey, RuntimeStateStreamCollectorEntry<TSample>> _latest = new();
        private readonly Dictionary<RuntimeStateStreamKey, ulong> _removedAtVersion = new();
        private readonly List<RuntimeStateStreamKey> _pruneWork = new();
        private ulong _version;

        public ulong Version => _version;

        public RuntimeStateStreamCollector(RuntimeStateStreamProfile<TSample> profile)
        {
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        }

        public ulong Commit(IReadOnlyList<TSample> samples, IReadOnlyList<RuntimeStateStreamKey> removedKeys)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            if (removedKeys == null)
                throw new ArgumentNullException(nameof(removedKeys));
            if (samples.Count == 0 && removedKeys.Count == 0)
                return _version;
            if (_version == ulong.MaxValue)
                throw new InvalidOperationException($"State stream '{_profile.StreamName}' exhausted its collector version range.");

            var nextVersion = _version + 1;
            var seen = new HashSet<RuntimeStateStreamKey>();
            for (var i = 0; i < removedKeys.Count; i++)
            {
                var key = removedKeys[i];
                if (!key.IsValid || !seen.Add(key))
                    throw new ArgumentException($"State stream removal key '{key}' is invalid or duplicated.", nameof(removedKeys));
            }
            for (var i = 0; i < samples.Count; i++)
            {
                var key = _profile.TakeKey(samples[i]);
                if (!key.IsValid || !seen.Add(key))
                    throw new ArgumentException($"State stream sample key '{key}' is invalid, duplicated or also removed.", nameof(samples));
                _profile.Validate(samples[i]);
            }

            for (var i = 0; i < removedKeys.Count; i++)
            {
                var key = removedKeys[i];
                _latest.Remove(key);
                _removedAtVersion[key] = nextVersion;
            }
            for (var i = 0; i < samples.Count; i++)
            {
                var sample = samples[i];
                var key = _profile.TakeKey(sample);
                _removedAtVersion.Remove(key);
                _latest[key] = new RuntimeStateStreamCollectorEntry<TSample>(nextVersion, sample);
            }

            _version = nextVersion;
            return nextVersion;
        }

        public RuntimeStateStreamCollectorSnapshot<TSample> TakeSnapshotAfter(ulong version)
        {
            if (version > _version)
                throw new ArgumentOutOfRangeException(nameof(version));
            var samples = new List<TSample>();
            foreach (var pair in _latest)
            {
                if (pair.Value.Version > version)
                    samples.Add(pair.Value.Sample);
            }
            samples.Sort((first, second) => _profile.TakeKey(first).Value.CompareTo(_profile.TakeKey(second).Value));
            var removed = new List<RuntimeStateStreamKey>();
            foreach (var pair in _removedAtVersion)
            {
                if (pair.Value > version)
                    removed.Add(pair.Key);
            }
            removed.Sort((first, second) => first.Value.CompareTo(second.Value));
            return new RuntimeStateStreamCollectorSnapshot<TSample>(
                _version,
                Array.AsReadOnly(samples.ToArray()),
                Array.AsReadOnly(removed.ToArray()));
        }

        /// <summary>
        /// Captures the complete current stream state without historical
        /// tombstones. A connection uses this after baseline/rebaseline because
        /// hot state is deliberately absent from the reliable baseline.
        /// </summary>
        public RuntimeStateStreamCollectorSnapshot<TSample> TakeCurrentSnapshot()
        {
            var samples = new List<TSample>(_latest.Count);
            foreach (var pair in _latest)
                samples.Add(pair.Value.Sample);
            samples.Sort((first, second) => _profile.TakeKey(first).Value.CompareTo(_profile.TakeKey(second).Value));
            return new RuntimeStateStreamCollectorSnapshot<TSample>(
                _version,
                Array.AsReadOnly(samples.ToArray()),
                Array.Empty<RuntimeStateStreamKey>());
        }

        public void PruneRemovedThrough(ulong version)
        {
            if (version > _version)
                throw new ArgumentOutOfRangeException(nameof(version));
            _pruneWork.Clear();
            foreach (var pair in _removedAtVersion)
            {
                if (pair.Value <= version)
                    _pruneWork.Add(pair.Key);
            }
            for (var i = 0; i < _pruneWork.Count; i++)
            {
                _removedAtVersion.Remove(_pruneWork[i]);
            }
            _pruneWork.Clear();
        }

        public void Clear()
        {
            _latest.Clear();
            _removedAtVersion.Clear();
            _pruneWork.Clear();
            _version = 0;
        }
    }
}
