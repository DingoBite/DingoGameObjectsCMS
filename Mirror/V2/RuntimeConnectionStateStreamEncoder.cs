using System;
using System.Collections.Generic;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public class RuntimeConnectionStateStreamEncoder<TSample>
    {
        private readonly RuntimeStateStreamProfile<TSample> _profile;
        private readonly RuntimeConnectionStateStreamCoalescer _coalescer;
        private readonly Func<RuntimeStateStreamKey, bool> _isEligible;

        public readonly NetStoreRef Store;
        public RuntimeStateStreamSequenceCursor SequenceCursor => _coalescer.SequenceCursor;

        public RuntimeConnectionStateStreamEncoder(
            NetStoreRef store,
            RuntimeStateStreamProfile<TSample> profile,
            Func<RuntimeStateStreamKey, bool> isEligible,
            double startTimeSeconds = 0d,
            RuntimeStateStreamSequenceCursor sequenceCursor = null)
        {
            if (!store.IsValid)
                throw new ArgumentException("State stream encoder requires a valid store reference.", nameof(store));
            Store = store;
            _profile = profile ?? throw new ArgumentNullException(nameof(profile));
            _isEligible = isEligible ?? throw new ArgumentNullException(nameof(isEligible));
            _coalescer = new RuntimeConnectionStateStreamCoalescer(
                store,
                profile,
                startTimeSeconds,
                sequenceCursor);
        }

        public RuntimeStateStreamSubmitResult Submit(uint simulationTick, IReadOnlyList<TSample> samples)
        {
            if (samples == null)
                throw new ArgumentNullException(nameof(samples));
            var packed = new RuntimePackedStateStreamSample[samples.Count];
            for (var i = 0; i < samples.Count; i++)
            {
                packed[i] = _profile.PackSample(samples[i]);
            }
            return _coalescer.Submit(simulationTick, packed);
        }

        public RuntimeStateStreamFrameBuildResult TryBuildFrame(
            double nowSeconds,
            uint simulationTick,
            out RuntimeStateStreamFrame frame)
        {
            return _coalescer.TryBuildFrame(nowSeconds, simulationTick, _isEligible, out frame);
        }

        public bool Forget(RuntimeStateStreamKey key) => _coalescer.Forget(key);
        public void Despawn(RuntimeStateStreamKey key) => _coalescer.Despawn(key);
        public void RequestKeyframe() => _coalescer.RequestKeyframe();
    }
}
