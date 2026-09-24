using System;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Unity.Collections;
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

    public class RuntimeDeterministicIdentityAllocator : IDisposable
    {
        private static readonly UTF8Encoding UTF8 = new(false, true);
        private static readonly char[] HEX = "0123456789abcdef".ToCharArray();

        private readonly int _seed;
        private readonly string _scope;
        private readonly int _prefixLength;
        private readonly SHA256 _sha;
        private NativeList<byte> _payload;
        private ulong _nextSequence;

        public RuntimeDeterministicIdentityState State => new(_seed, _scope, _nextSequence);

        public RuntimeDeterministicIdentityAllocator(int seed, string scope, ulong nextSequence = 0)
        {
            if (string.IsNullOrWhiteSpace(scope))
                throw new ArgumentException("A deterministic identity scope is required.", nameof(scope));

            _seed = seed;
            _scope = scope;
            _nextSequence = nextSequence;
            _prefixLength = sizeof(int) + UTF8.GetByteCount(scope);
            _sha = SHA256.Create();
            _payload = new NativeList<byte>(_prefixLength + sizeof(ulong), Allocator.Persistent);
            _payload.ResizeUninitialized(_prefixLength + sizeof(ulong));
            var payload = _payload.AsSpan();
            BinaryPrimitives.WriteInt32LittleEndian(payload, seed);
            UTF8.GetBytes(scope.AsSpan(), payload.Slice(sizeof(int), _prefixLength - sizeof(int)));
        }

        public Hash128 Next()
        {
            if (_nextSequence == ulong.MaxValue)
                throw new InvalidOperationException("The deterministic runtime identity sequence is exhausted.");

            var sequence = _nextSequence++;
            BinaryPrimitives.WriteUInt64LittleEndian(_payload.AsSpan().Slice(_prefixLength, sizeof(ulong)), sequence);
            Span<byte> digest = stackalloc byte[32];
            _sha.TryComputeHash(_payload.AsReadOnlySpan(), digest, out _);

            Span<char> text = stackalloc char[32];
            for (var i = 0; i < 16; i++)
            {
                text[i * 2] = HEX[digest[i] >> 4];
                text[i * 2 + 1] = HEX[digest[i] & 0x0f];
            }

            return Hash128.Parse(new string(text));
        }

        public void Dispose()
        {
            if (_payload.IsCreated)
            {
                _payload.Dispose();
            }
            _sha.Dispose();
        }
    }

    public static class RuntimeInstanceIdentity
    {
        private static RuntimeDeterministicIdentityAllocator _allocator;

        public static bool HasDeterministicSession => _allocator != null;

        public static RuntimeDeterministicIdentityState State =>
            _allocator?.State
            ?? throw new InvalidOperationException("No deterministic runtime identity session is active.");

        public static void BeginDeterministicSession(int seed, string scope, ulong nextSequence = 0)
        {
            var next = new RuntimeDeterministicIdentityAllocator(seed, scope, nextSequence);
            var previous = _allocator;
            _allocator = next;
            previous?.Dispose();
        }

        public static void Restore(in RuntimeDeterministicIdentityState state)
        {
            BeginDeterministicSession(state.Seed, state.Scope, state.NextSequence);
        }

        public static void EndDeterministicSession()
        {
            var previous = _allocator;
            _allocator = null;
            previous?.Dispose();
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
