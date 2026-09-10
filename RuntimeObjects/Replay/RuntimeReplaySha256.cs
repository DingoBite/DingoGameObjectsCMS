using System;
using System.Security.Cryptography;
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
#endif

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public static class RuntimeReplaySha256
    {
        public static SHA256 Create()
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            var native = RuntimeReplayWindowsSha256.TryCreate();
            if (native != null)
            {
                return native;
            }
#endif
            return SHA256.Create();
        }
    }

#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
    public class RuntimeReplayWindowsSha256 : SHA256
    {
        private static readonly Lazy<RuntimeReplayBCryptAlgorithmHandle> SHARED_ALGORITHM = new(
            OpenAlgorithm,
            LazyThreadSafetyMode.ExecutionAndPublication);

        private readonly RuntimeReplayBCryptAlgorithmHandle _algorithm;
        private RuntimeReplayBCryptHashHandle _hash;
        private bool _disposed;

        private RuntimeReplayWindowsSha256(RuntimeReplayBCryptAlgorithmHandle algorithm)
        {
            _algorithm = algorithm;
            HashSizeValue = 256;
            Initialize();
        }

        public static RuntimeReplayWindowsSha256 TryCreate()
        {
            // Capability selection happens before any input is consumed. Hash failures never retry with another backend.
            var algorithm = SHARED_ALGORITHM.Value;
            return algorithm == null ? null : new RuntimeReplayWindowsSha256(algorithm);
        }

        public override void Initialize()
        {
            ThrowIfDisposed();
            _hash?.Dispose();
            _hash = null;
            _hash = RuntimeReplayBCryptHashHandle.Create(_algorithm);
        }

        protected override void HashCore(byte[] array, int ibStart, int cbSize)
        {
            ThrowIfDisposed();
            if (cbSize == 0)
            {
                return;
            }

            var hash = _hash;
            var retained = false;
            var pin = default(GCHandle);
            try
            {
                hash.DangerousAddRef(ref retained);
                pin = GCHandle.Alloc(array, GCHandleType.Pinned);
                var input = IntPtr.Add(pin.AddrOfPinnedObject(), ibStart);
                RuntimeReplayBCryptNative.ThrowIfFailed(
                    RuntimeReplayBCryptNative.BCryptHashData(hash.DangerousGetHandle(), input, checked((uint)cbSize), 0),
                    "HashData");
            }
            finally
            {
                if (pin.IsAllocated)
                {
                    pin.Free();
                }
                if (retained)
                {
                    hash.DangerousRelease();
                }
            }
        }

        protected override byte[] HashFinal()
        {
            ThrowIfDisposed();
            var result = new byte[RuntimeReplayHash.SHA256_BYTES];
            var hash = _hash;
            var retained = false;
            try
            {
                hash.DangerousAddRef(ref retained);
                RuntimeReplayBCryptNative.ThrowIfFailed(
                    RuntimeReplayBCryptNative.BCryptFinishHash(hash.DangerousGetHandle(), result, checked((uint)result.Length), 0),
                    "FinishHash");
                return result;
            }
            finally
            {
                if (retained)
                {
                    hash.DangerousRelease();
                }
                hash.Dispose();
                _hash = null;
            }
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            _hash?.Dispose();
            _hash = null;
            base.Dispose(disposing);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(RuntimeReplayWindowsSha256));
            }
        }

        private static RuntimeReplayBCryptAlgorithmHandle OpenAlgorithm()
        {
            IntPtr algorithm = IntPtr.Zero;
            try
            {
                var status = RuntimeReplayBCryptNative.BCryptOpenAlgorithmProvider(out algorithm, "SHA256", null, 0);
                if (status < 0)
                {
                    if (algorithm != IntPtr.Zero)
                    {
                        RuntimeReplayBCryptNative.BCryptCloseAlgorithmProvider(algorithm, 0);
                        algorithm = IntPtr.Zero;
                    }
                    if (status == RuntimeReplayBCryptNative.STATUS_NOT_FOUND
                        || status == RuntimeReplayBCryptNative.STATUS_NOT_SUPPORTED)
                    {
                        return null;
                    }
                    RuntimeReplayBCryptNative.ThrowIfFailed(status, "OpenAlgorithmProvider");
                }
                return new RuntimeReplayBCryptAlgorithmHandle(algorithm);
            }
            catch (DllNotFoundException)
            {
                return null;
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            catch (PlatformNotSupportedException)
            {
                return null;
            }
        }
    }

    public class RuntimeReplayBCryptAlgorithmHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public RuntimeReplayBCryptAlgorithmHandle(IntPtr value) : base(true)
        {
            SetHandle(value);
        }

        protected override bool ReleaseHandle()
        {
            return RuntimeReplayBCryptNative.BCryptCloseAlgorithmProvider(handle, 0) >= 0;
        }
    }

    public class RuntimeReplayBCryptHashHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly RuntimeReplayBCryptAlgorithmHandle _algorithm;

        private RuntimeReplayBCryptHashHandle(IntPtr value, RuntimeReplayBCryptAlgorithmHandle algorithm) : base(true)
        {
            // Create has transferred an existing DangerousAddRef lease. Keep the provider open until its hash is destroyed.
            _algorithm = algorithm;
            SetHandle(value);
        }

        public static RuntimeReplayBCryptHashHandle Create(RuntimeReplayBCryptAlgorithmHandle algorithm)
        {
            var retained = false;
            var hash = IntPtr.Zero;
            try
            {
                algorithm.DangerousAddRef(ref retained);
                RuntimeReplayBCryptNative.ThrowIfFailed(
                    RuntimeReplayBCryptNative.BCryptCreateHash(algorithm.DangerousGetHandle(), out hash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0),
                    "CreateHash");
                var result = new RuntimeReplayBCryptHashHandle(hash, algorithm);
                hash = IntPtr.Zero;
                retained = false;
                return result;
            }
            finally
            {
                if (hash != IntPtr.Zero)
                {
                    RuntimeReplayBCryptNative.BCryptDestroyHash(hash);
                }
                if (retained)
                {
                    algorithm.DangerousRelease();
                }
            }
        }

        protected override bool ReleaseHandle()
        {
            try
            {
                return RuntimeReplayBCryptNative.BCryptDestroyHash(handle) >= 0;
            }
            finally
            {
                _algorithm.DangerousRelease();
            }
        }
    }

    public static class RuntimeReplayBCryptNative
    {
        public const int STATUS_NOT_FOUND = unchecked((int)0xC0000225);
        public const int STATUS_NOT_SUPPORTED = unchecked((int)0xC00000BB);

        public static void ThrowIfFailed(int status, string operation)
        {
            if (status < 0)
            {
                throw new CryptographicException($"BCrypt{operation} failed with NTSTATUS 0x{status:X8}.");
            }
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string algorithmId, string implementation, uint flags);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        public static extern int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr hashObject, uint hashObjectLength, IntPtr secret, uint secretLength, uint flags);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        public static extern int BCryptHashData(IntPtr hash, IntPtr input, uint inputLength, uint flags);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        public static extern int BCryptFinishHash(IntPtr hash, [Out] byte[] output, uint outputLength, uint flags);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        public static extern int BCryptDestroyHash(IntPtr hash);

        [DllImport("bcrypt.dll", ExactSpelling = true)]
        public static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);
    }
#endif
}
