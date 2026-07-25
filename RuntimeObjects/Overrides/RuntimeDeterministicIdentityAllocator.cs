using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace DingoGameObjectsCMS.RuntimeObjects.Overrides
{
    public readonly struct RuntimeDeterministicIdentityState
    {
        public readonly int Seed;
        public readonly string Scope;
        public readonly ulong NextSequence;

        public RuntimeDeterministicIdentityState(int seed, string scope, ulong nextSequence)
        {
            Seed = seed;
            Scope = scope;
            NextSequence = nextSequence;
        }
    }

    public class RuntimeDeterministicIdentityAllocator
    {
        private static readonly UTF8Encoding UTF8 = new(false, true);
        private static readonly char[] HEX = "0123456789abcdef".ToCharArray();

        private readonly int _seed;
        private readonly string _scope;
        private readonly byte[] _prefix;
        private ulong _nextSequence;

        public RuntimeDeterministicIdentityState State =>
            new(_seed, _scope, _nextSequence);

        public RuntimeDeterministicIdentityAllocator(
            int seed,
            string scope,
            ulong nextSequence = 0)
        {
            if (string.IsNullOrWhiteSpace(scope))
                throw new ArgumentException("A deterministic identity scope is required.", nameof(scope));

            _seed = seed;
            _scope = scope;
            _nextSequence = nextSequence;
            _prefix = BuildPrefix(seed, scope);
        }

        public Hash128 Next()
        {
            if (_nextSequence == ulong.MaxValue)
                throw new InvalidOperationException("The deterministic runtime identity sequence is exhausted.");

            var sequence = _nextSequence++;
            var payload = new byte[_prefix.Length + sizeof(ulong)];
            Buffer.BlockCopy(_prefix, 0, payload, 0, _prefix.Length);
            for (var i = 0; i < sizeof(ulong); i++)
            {
                payload[_prefix.Length + i] = (byte)(sequence >> (i * 8));
            }

            byte[] digest;
            using (var sha = SHA256.Create())
            {
                digest = sha.ComputeHash(payload);
            }

            var text = new char[32];
            for (var i = 0; i < 16; i++)
            {
                text[i * 2] = HEX[digest[i] >> 4];
                text[i * 2 + 1] = HEX[digest[i] & 0x0f];
            }

            return Hash128.Parse(new string(text));
        }

        private static byte[] BuildPrefix(int seed, string scope)
        {
            var scopeBytes = UTF8.GetBytes(scope);
            var prefix = new byte[sizeof(int) + scopeBytes.Length];
            prefix[0] = (byte)seed;
            prefix[1] = (byte)(seed >> 8);
            prefix[2] = (byte)(seed >> 16);
            prefix[3] = (byte)(seed >> 24);
            Buffer.BlockCopy(scopeBytes, 0, prefix, sizeof(int), scopeBytes.Length);
            return prefix;
        }
    }

    public static class RuntimeInstanceIdentity
    {
        private static RuntimeDeterministicIdentityAllocator _allocator;

        public static bool HasDeterministicSession => _allocator != null;

        public static RuntimeDeterministicIdentityState State =>
            _allocator?.State
            ?? throw new InvalidOperationException("No deterministic runtime identity session is active.");

        public static void BeginDeterministicSession(
            int seed,
            string scope,
            ulong nextSequence = 0)
        {
            _allocator = new RuntimeDeterministicIdentityAllocator(seed, scope, nextSequence);
        }

        public static void Restore(in RuntimeDeterministicIdentityState state)
        {
            BeginDeterministicSession(state.Seed, state.Scope, state.NextSequence);
        }

        public static void EndDeterministicSession()
        {
            _allocator = null;
        }

        public static Hash128 Next()
        {
            return _allocator?.Next() ?? IdUtils.NewHash128FromGuid();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnSubsystemRegistration()
        {
            EndDeterministicSession();
        }
    }
}
