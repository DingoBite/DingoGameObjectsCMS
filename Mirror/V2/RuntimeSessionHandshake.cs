using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DingoGameObjectsCMS.Mirror.V2
{
    public enum RuntimeSessionHandshakeState : byte
    {
        Created = 0,
        HelloSent = 1,
        ManifestSent = 2,
        Ready = 3,
        Rejected = 4,
    }

    public readonly struct RuntimeSessionHandshakeResult
    {
        public readonly RuntimeProtocolRejectCode RejectCode;
        public readonly string Detail;

        public RuntimeSessionHandshakeResult(RuntimeProtocolRejectCode rejectCode, string detail)
        {
            RejectCode = rejectCode;
            Detail = detail ?? string.Empty;
        }

        public bool Accepted => RejectCode == RuntimeProtocolRejectCode.None;

        public static RuntimeSessionHandshakeResult Success()
        {
            return new RuntimeSessionHandshakeResult(RuntimeProtocolRejectCode.None, string.Empty);
        }

        public static RuntimeSessionHandshakeResult Reject(RuntimeProtocolRejectCode code, string detail)
        {
            if (code == RuntimeProtocolRejectCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(code), "A rejected handshake step requires a reject code.");
            }

            return new RuntimeSessionHandshakeResult(code, detail);
        }

#if MIRROR
        public RtProtocolReject ToWireReject()
        {
            if (Accepted)
            {
                throw new InvalidOperationException("An accepted handshake result cannot be encoded as a protocol reject.");
            }

            return new RtProtocolReject
            {
                Code = RejectCode,
                Detail = Detail,
            };
        }
#endif
    }

    /// <summary>
    /// Immutable local expectation used by every connection in one network session.
    /// Asset and store arrays are normalized and copied during construction.
    /// </summary>
    public class RuntimeSessionManifestTemplate
    {
        private readonly RuntimeAssetCatalogEntry[] _assets;
        private readonly RuntimeStoreCatalogEntry[] _stores;
        private readonly ReadOnlyCollection<RuntimeAssetCatalogEntry> _assetView;
        private readonly ReadOnlyCollection<RuntimeStoreCatalogEntry> _storeView;

        public readonly RuntimeSessionDescriptor Descriptor;

        public IReadOnlyList<RuntimeAssetCatalogEntry> Assets => _assetView;
        public IReadOnlyList<RuntimeStoreCatalogEntry> Stores => _storeView;

        public RuntimeSessionManifestTemplate(
            in RuntimeSessionDescriptor descriptor,
            IEnumerable<RuntimeAssetCatalogEntry> assets,
            IEnumerable<RuntimeStoreCatalogEntry> stores)
        {
            if (descriptor.ProtocolVersion != RuntimeProtocolV2.VERSION)
            {
                throw new InvalidOperationException(
                    $"Local protocol version {descriptor.ProtocolVersion} does not match required version {RuntimeProtocolV2.VERSION}.");
            }

            if (!RuntimeSessionManifestSnapshot.TryCreate(
                    1,
                    descriptor,
                    assets,
                    stores,
                    out var validated,
                    out var error))
            {
                throw new InvalidOperationException($"Invalid local protocol-v2 manifest: {error.Detail}");
            }

            Descriptor = validated.Descriptor;
            _assets = validated.CopyAssets();
            _stores = validated.CopyStores();
            _assetView = Array.AsReadOnly(_assets);
            _storeView = Array.AsReadOnly(_stores);
        }

        public RuntimeSessionManifestSnapshot CreateSnapshot(ulong sessionId)
        {
            if (!RuntimeSessionManifestSnapshot.TryCreate(
                    sessionId,
                    Descriptor,
                    _assets,
                    _stores,
                    out var snapshot,
                    out var error))
            {
                throw new InvalidOperationException($"Unable to create protocol-v2 session manifest: {error.Detail}");
            }

            return snapshot;
        }
    }

    /// <summary>
    /// Validated immutable manifest for one connection/session id.
    /// </summary>
    public class RuntimeSessionManifestSnapshot
    {
        private readonly RuntimeAssetCatalogEntry[] _assets;
        private readonly RuntimeStoreCatalogEntry[] _stores;
        private readonly Dictionary<uint, RuntimeAssetCatalogEntry> _assetsByNetId;
        private readonly Dictionary<string, RuntimeStoreCatalogEntry> _storesById;
        private readonly ReadOnlyCollection<RuntimeAssetCatalogEntry> _assetView;
        private readonly ReadOnlyCollection<RuntimeStoreCatalogEntry> _storeView;

        public readonly ulong SessionId;
        public readonly RuntimeSessionDescriptor Descriptor;

        public IReadOnlyList<RuntimeAssetCatalogEntry> Assets => _assetView;
        public IReadOnlyList<RuntimeStoreCatalogEntry> Stores => _storeView;

        private RuntimeSessionManifestSnapshot(
            ulong sessionId,
            in RuntimeSessionDescriptor descriptor,
            RuntimeAssetCatalogEntry[] assets,
            RuntimeStoreCatalogEntry[] stores)
        {
            SessionId = sessionId;
            Descriptor = descriptor;
            _assets = assets;
            _stores = stores;
            _assetsByNetId = assets.ToDictionary(value => value.AssetNetId);
            _storesById = stores.ToDictionary(value => value.StoreId.ToString(), StringComparer.Ordinal);
            _assetView = Array.AsReadOnly(_assets);
            _storeView = Array.AsReadOnly(_stores);
        }

        public static bool TryCreate(
            ulong sessionId,
            in RuntimeSessionDescriptor descriptor,
            IEnumerable<RuntimeAssetCatalogEntry> assets,
            IEnumerable<RuntimeStoreCatalogEntry> stores,
            out RuntimeSessionManifestSnapshot snapshot,
            out RuntimeSessionHandshakeResult error)
        {
            snapshot = null;
            if (sessionId == 0)
            {
                error = RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, "Session id must be non-zero.");
                return false;
            }

            if (!HasRequiredDescriptorValues(descriptor))
            {
                error = RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, "Manifest descriptor is incomplete.");
                return false;
            }

            if (assets == null || stores == null)
            {
                error = RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, "Manifest asset and store catalogs are required.");
                return false;
            }

            var assetArray = assets.ToArray();
            var storeArray = stores.ToArray();
            try
            {
                var calculatedHash = RuntimeSessionCatalogHasher.Calculate(assetArray, storeArray);
                if (!string.Equals(calculatedHash, descriptor.AssetCatalogHash, StringComparison.Ordinal))
                {
                    error = RuntimeSessionHandshakeResult.Reject(
                        RuntimeProtocolRejectCode.InvalidManifest,
                        "Manifest catalog contents do not match its declared catalog hash.");
                    return false;
                }

                ValidateUniqueAssetIdentity(assetArray);
            }
            catch (Exception exception) when (exception is InvalidOperationException || exception is ArgumentException)
            {
                error = RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, exception.Message);
                return false;
            }

            Array.Sort(assetArray, (left, right) => left.AssetNetId.CompareTo(right.AssetNetId));
            Array.Sort(storeArray, CompareStores);
            snapshot = new RuntimeSessionManifestSnapshot(sessionId, descriptor, assetArray, storeArray);
            error = RuntimeSessionHandshakeResult.Success();
            return true;
        }

        public RuntimeSessionHandshakeResult ValidateAgainst(RuntimeSessionManifestTemplate expected)
        {
            if (expected == null)
            {
                throw new ArgumentNullException(nameof(expected));
            }

            var compatibility = RuntimeSessionCompatibility.Validate(expected.Descriptor, Descriptor);
            if (compatibility != RuntimeProtocolRejectCode.None
                && compatibility != RuntimeProtocolRejectCode.AssetCatalogMismatch)
            {
                return RuntimeSessionHandshakeResult.Reject(compatibility, $"Session descriptor mismatch: {compatibility}.");
            }

            if (_assets.Length != expected.Assets.Count)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, "Manifest does not contain the exact required asset catalog.");
            }

            foreach (var expectedAsset in expected.Assets)
            {
                if (!_assetsByNetId.TryGetValue(expectedAsset.AssetNetId, out var actualAsset)
                    || !AssetEntriesEqual(expectedAsset, actualAsset))
                {
                    return RuntimeSessionHandshakeResult.Reject(
                        RuntimeProtocolRejectCode.InvalidManifest,
                        $"Manifest asset {expectedAsset.AssetNetId} does not match the exact local GameAsset catalog entry.");
                }
            }

            if (_stores.Length != expected.Stores.Count)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, "Manifest does not contain the exact required store catalog.");
            }

            foreach (var expectedStore in expected.Stores)
            {
                var storeId = expectedStore.StoreId.ToString();
                if (!_storesById.TryGetValue(storeId, out var actualStore)
                    || actualStore.StoreGeneration != expectedStore.StoreGeneration)
                {
                    return RuntimeSessionHandshakeResult.Reject(
                        RuntimeProtocolRejectCode.InvalidManifest,
                        $"Manifest store '{expectedStore.StoreId}' does not match the required generation {expectedStore.StoreGeneration}.");
                }
            }

            if (compatibility == RuntimeProtocolRejectCode.AssetCatalogMismatch)
            {
                return RuntimeSessionHandshakeResult.Reject(compatibility, $"Session descriptor mismatch: {compatibility}.");
            }

            return RuntimeSessionHandshakeResult.Success();
        }

        public bool ContainsStore(in NetStoreRef store)
        {
            return store.IsValid
                   && _storesById.TryGetValue(store.StoreId.ToString(), out var entry)
                   && entry.StoreGeneration == store.StoreGeneration;
        }

        public RuntimeAssetCatalogEntry[] CopyAssets()
        {
            return (RuntimeAssetCatalogEntry[])_assets.Clone();
        }

        public RuntimeStoreCatalogEntry[] CopyStores()
        {
            return (RuntimeStoreCatalogEntry[])_stores.Clone();
        }

#if MIRROR
        public RtSessionManifest ToWireManifest()
        {
            return new RtSessionManifest
            {
                SessionId = SessionId,
                Descriptor = Descriptor,
                Assets = CopyAssets(),
                Stores = CopyStores(),
            };
        }
#endif

        private static bool HasRequiredDescriptorValues(in RuntimeSessionDescriptor descriptor)
        {
            return descriptor.ProtocolVersion != 0
                   && !string.IsNullOrWhiteSpace(descriptor.BuildId)
                   && !string.IsNullOrWhiteSpace(descriptor.RuntimeSchemaHash)
                   && !string.IsNullOrWhiteSpace(descriptor.AssetCatalogHash)
                   && !string.IsNullOrWhiteSpace(descriptor.StateStreamCatalogHash);
        }

        private static void ValidateUniqueAssetIdentity(IEnumerable<RuntimeAssetCatalogEntry> assets)
        {
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in assets)
            {
                var identity = $"{asset.ExactKey.Length}:{asset.ExactKey}|{asset.AssetGuid.Length}:{asset.AssetGuid}|{asset.MaterializedContentHash.Length}:{asset.MaterializedContentHash}";
                if (!identities.Add(identity))
                {
                    throw new InvalidOperationException($"Asset catalog contains duplicate exact GameAsset identity '{asset.ExactKey}'.");
                }
            }
        }

        private static int CompareStores(RuntimeStoreCatalogEntry left, RuntimeStoreCatalogEntry right)
        {
            return string.Compare(left.StoreId.ToString(), right.StoreId.ToString(), StringComparison.Ordinal);
        }

        private static bool AssetEntriesEqual(in RuntimeAssetCatalogEntry left, in RuntimeAssetCatalogEntry right)
        {
            return left.AssetNetId == right.AssetNetId
                   && string.Equals(left.ExactKey, right.ExactKey, StringComparison.Ordinal)
                   && string.Equals(left.AssetGuid, right.AssetGuid, StringComparison.Ordinal)
                   && string.Equals(left.MaterializedContentHash, right.MaterializedContentHash, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Client-side immutable expectation. Store generations are authoritative
    /// session data and are therefore intentionally absent here; the client
    /// validates only the exact required StoreId set and freezes the non-zero
    /// generations received in the accepted server manifest.
    /// </summary>
    public class RuntimeSessionClientExpectation
    {
        private readonly RuntimeAssetCatalogEntry[] _assets;
        private readonly string[] _storeIds;

        public readonly RuntimeSessionDescriptor Descriptor;
        public IReadOnlyList<RuntimeAssetCatalogEntry> Assets => Array.AsReadOnly(_assets);
        public IReadOnlyList<string> StoreIds => Array.AsReadOnly(_storeIds);

        public RuntimeSessionClientExpectation(
            in RuntimeSessionDescriptor descriptor,
            IEnumerable<RuntimeAssetCatalogEntry> assets,
            IEnumerable<string> requiredStoreIds)
        {
            if (descriptor.ProtocolVersion != RuntimeProtocolV2.VERSION)
                throw new InvalidOperationException($"Client protocol version must be {RuntimeProtocolV2.VERSION}.");
            if (string.IsNullOrWhiteSpace(descriptor.BuildId)
                || string.IsNullOrWhiteSpace(descriptor.RuntimeSchemaHash)
                || string.IsNullOrWhiteSpace(descriptor.AssetCatalogHash)
                || string.IsNullOrWhiteSpace(descriptor.StateStreamCatalogHash))
            {
                throw new InvalidOperationException("Client protocol-v2 descriptor is incomplete.");
            }
            if (assets == null)
                throw new ArgumentNullException(nameof(assets));
            if (requiredStoreIds == null)
                throw new ArgumentNullException(nameof(requiredStoreIds));

            _assets = assets.OrderBy(value => value.AssetNetId).ToArray();
            _storeIds = requiredStoreIds
                .Select(value => value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (_storeIds.Length == 0 || _storeIds.Distinct(StringComparer.Ordinal).Count() != _storeIds.Length)
                throw new InvalidOperationException("Client protocol-v2 expectation requires unique StoreIds.");

            var assetHash = RuntimeSessionCatalogHasher.CalculateAssets(_assets);
            if (!string.Equals(assetHash, descriptor.AssetCatalogHash, StringComparison.Ordinal))
                throw new InvalidOperationException("Client protocol-v2 asset catalog does not match its descriptor hash.");

            Descriptor = descriptor;
        }

        public static RuntimeSessionClientExpectation FromServerTemplate(RuntimeSessionManifestTemplate template)
        {
            if (template == null)
                throw new ArgumentNullException(nameof(template));
            return new RuntimeSessionClientExpectation(
                template.Descriptor,
                template.Assets,
                template.Stores.Select(value => value.StoreId.ToString()));
        }

        public RuntimeSessionHandshakeResult Validate(RuntimeSessionManifestSnapshot manifest)
        {
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));
            var compatibility = RuntimeSessionCompatibility.Validate(Descriptor, manifest.Descriptor);
            // A manifest carries the exact catalog entries, unlike Hello. If
            // its declared catalog is internally consistent but differs from
            // the local immutable catalog, report the exact-manifest contract
            // violation below instead of stopping at the descriptor hash.
            if (compatibility != RuntimeProtocolRejectCode.None
                && compatibility != RuntimeProtocolRejectCode.AssetCatalogMismatch)
            {
                return RuntimeSessionHandshakeResult.Reject(compatibility, $"Session descriptor mismatch: {compatibility}.");
            }
            if (manifest.Assets.Count != _assets.Length)
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, "Manifest does not contain the exact required asset catalog.");
            for (var i = 0; i < _assets.Length; i++)
            {
                var expected = _assets[i];
                var actual = manifest.Assets[i];
                if (expected.AssetNetId != actual.AssetNetId
                    || !string.Equals(expected.ExactKey, actual.ExactKey, StringComparison.Ordinal)
                    || !string.Equals(expected.AssetGuid, actual.AssetGuid, StringComparison.Ordinal)
                    || !string.Equals(expected.MaterializedContentHash, actual.MaterializedContentHash, StringComparison.Ordinal))
                {
                    return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, $"Manifest asset {expected.AssetNetId} does not match the exact local catalog.");
                }
            }

            var actualStoreIds = manifest.Stores
                .Select(value => value.StoreId.ToString())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            if (!_storeIds.SequenceEqual(actualStoreIds, StringComparer.Ordinal))
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidManifest, "Manifest StoreId set does not match the required client store set.");

            // Defensive fallback. With canonical validated catalogs, equal
            // exact entries necessarily have the same hash, but preserve the
            // descriptor error if a future hash contract changes that rule.
            if (compatibility == RuntimeProtocolRejectCode.AssetCatalogMismatch)
                return RuntimeSessionHandshakeResult.Reject(compatibility, $"Session descriptor mismatch: {compatibility}.");
            return RuntimeSessionHandshakeResult.Success();
        }
    }

    public class RuntimeSessionServerHandshake
    {
        private readonly RuntimeSessionManifestTemplate _manifestTemplate;
        private readonly RuntimeSessionManifestSnapshot _manifest;

        public readonly ulong SessionId;

        public RuntimeSessionHandshakeState State { get; private set; }
        public ulong ClientNonce { get; private set; }
        public RuntimeSessionManifestSnapshot Manifest => _manifest;
        public bool CanCreateReplica => State == RuntimeSessionHandshakeState.Ready;

        public RuntimeSessionServerHandshake(ulong sessionId, RuntimeSessionManifestTemplate manifestTemplate)
        {
            _manifestTemplate = manifestTemplate ?? throw new ArgumentNullException(nameof(manifestTemplate));
            _manifest = manifestTemplate.CreateSnapshot(sessionId);
            SessionId = sessionId;
            State = RuntimeSessionHandshakeState.Created;
        }

        public RuntimeSessionHandshakeResult ReceiveHello(in RuntimeSessionDescriptor descriptor, ulong clientNonce)
        {
            if (State != RuntimeSessionHandshakeState.Created)
            {
                return Reject(RuntimeProtocolRejectCode.InvalidEnvelope, $"Hello is not valid while handshake state is {State}.");
            }

            if (clientNonce == 0)
            {
                return Reject(RuntimeProtocolRejectCode.InvalidEnvelope, "Client nonce must be non-zero.");
            }

            var compatibility = RuntimeSessionCompatibility.Validate(_manifestTemplate.Descriptor, descriptor);
            if (compatibility != RuntimeProtocolRejectCode.None)
            {
                return Reject(compatibility, $"Hello descriptor mismatch: {compatibility}.");
            }

            ClientNonce = clientNonce;
            State = RuntimeSessionHandshakeState.ManifestSent;
            return RuntimeSessionHandshakeResult.Success();
        }

        public RuntimeSessionHandshakeResult ReceiveReady(ulong sessionId)
        {
            if (State != RuntimeSessionHandshakeState.ManifestSent)
            {
                return Reject(RuntimeProtocolRejectCode.InvalidEnvelope, $"Ready is not valid while handshake state is {State}.");
            }

            if (sessionId != SessionId)
            {
                return Reject(RuntimeProtocolRejectCode.InvalidEnvelope, "Ready references a forged or stale session id.");
            }

            State = RuntimeSessionHandshakeState.Ready;
            return RuntimeSessionHandshakeResult.Success();
        }

        public RuntimeSessionHandshakeResult AuthorizeStoreAccess(ulong sessionId, in NetStoreRef store)
        {
            if (State != RuntimeSessionHandshakeState.Ready)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.SessionNotReady, "Session has not completed the protocol-v2 handshake.");
            }

            if (sessionId != SessionId)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidEnvelope, "Envelope references a forged or stale session id.");
            }

            if (!_manifest.ContainsStore(store))
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidStore, $"Store '{store}' is not present in the immutable session manifest.");
            }

            return RuntimeSessionHandshakeResult.Success();
        }

        public RuntimeSessionHandshakeResult AuthorizeResync(ulong sessionId, in NetStoreRef store)
        {
            return AuthorizeStoreAccess(sessionId, store);
        }

        public RuntimeSessionHandshakeResult ReceiveReject(RuntimeProtocolRejectCode code, string detail)
        {
            return Reject(code, detail);
        }

#if MIRROR
        public RuntimeSessionHandshakeResult ReceiveHello(in RtSessionHello hello)
        {
            return ReceiveHello(hello.Descriptor, hello.ClientNonce);
        }

        public RuntimeSessionHandshakeResult ReceiveReady(in RtSessionReady ready)
        {
            return ReceiveReady(ready.SessionId);
        }

        public RuntimeSessionHandshakeResult ReceiveReject(in RtProtocolReject reject)
        {
            return ReceiveReject(reject.Code, reject.Detail);
        }

        public RtSessionManifest CreateManifestMessage()
        {
            if (State != RuntimeSessionHandshakeState.ManifestSent)
            {
                throw new InvalidOperationException($"Manifest cannot be sent while handshake state is {State}.");
            }

            return _manifest.ToWireManifest();
        }
#endif

        private RuntimeSessionHandshakeResult Reject(RuntimeProtocolRejectCode code, string detail)
        {
            var result = RuntimeSessionHandshakeResult.Reject(code, detail);
            State = RuntimeSessionHandshakeState.Rejected;
            return result;
        }
    }

    public class RuntimeSessionClientHandshake
    {
        private readonly RuntimeSessionClientExpectation _expectation;
        private RuntimeSessionManifestSnapshot _manifest;

        public readonly ulong ClientNonce;

        public RuntimeSessionHandshakeState State { get; private set; }
        public RuntimeSessionManifestSnapshot Manifest => _manifest;
        public bool CanCreateReplica => State == RuntimeSessionHandshakeState.Ready;

        public RuntimeSessionClientHandshake(ulong clientNonce, RuntimeSessionClientExpectation expectation)
        {
            if (clientNonce == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(clientNonce), "Client nonce must be non-zero.");
            }

            ClientNonce = clientNonce;
            _expectation = expectation ?? throw new ArgumentNullException(nameof(expectation));
            State = RuntimeSessionHandshakeState.Created;
        }

        public RuntimeSessionClientHandshake(ulong clientNonce, RuntimeSessionManifestTemplate expectedManifest)
            : this(clientNonce, RuntimeSessionClientExpectation.FromServerTemplate(expectedManifest)) { }

        public RuntimeSessionDescriptor BeginHello()
        {
            if (State != RuntimeSessionHandshakeState.Created)
            {
                throw new InvalidOperationException($"Hello cannot be sent while handshake state is {State}.");
            }

            State = RuntimeSessionHandshakeState.HelloSent;
            return _expectation.Descriptor;
        }

        public RuntimeSessionHandshakeResult ReceiveManifest(
            ulong sessionId,
            in RuntimeSessionDescriptor descriptor,
            IEnumerable<RuntimeAssetCatalogEntry> assets,
            IEnumerable<RuntimeStoreCatalogEntry> stores)
        {
            if (State != RuntimeSessionHandshakeState.HelloSent)
            {
                return Reject(RuntimeProtocolRejectCode.InvalidEnvelope, $"Manifest is not valid while handshake state is {State}.");
            }

            if (!RuntimeSessionManifestSnapshot.TryCreate(sessionId, descriptor, assets, stores, out var manifest, out var error))
            {
                return Reject(error.RejectCode, error.Detail);
            }

            var compatibility = _expectation.Validate(manifest);
            if (!compatibility.Accepted)
            {
                return Reject(compatibility.RejectCode, compatibility.Detail);
            }

            _manifest = manifest;
            State = RuntimeSessionHandshakeState.Ready;
            return RuntimeSessionHandshakeResult.Success();
        }

        public RuntimeSessionHandshakeResult AuthorizeStoreAccess(ulong sessionId, in NetStoreRef store)
        {
            if (State != RuntimeSessionHandshakeState.Ready)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.SessionNotReady, "Session has not completed the protocol-v2 handshake.");
            }

            if (sessionId != _manifest.SessionId)
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidEnvelope, "Envelope references a forged or stale session id.");
            }

            if (!_manifest.ContainsStore(store))
            {
                return RuntimeSessionHandshakeResult.Reject(RuntimeProtocolRejectCode.InvalidStore, $"Store '{store}' is not present in the immutable session manifest.");
            }

            return RuntimeSessionHandshakeResult.Success();
        }

        public RuntimeSessionHandshakeResult AuthorizeResync(ulong sessionId, in NetStoreRef store)
        {
            return AuthorizeStoreAccess(sessionId, store);
        }

        public RuntimeSessionHandshakeResult ReceiveReject(RuntimeProtocolRejectCode code, string detail)
        {
            return Reject(code, detail);
        }

#if MIRROR
        public RtSessionHello CreateHelloMessage()
        {
            return new RtSessionHello
            {
                Descriptor = BeginHello(),
                ClientNonce = ClientNonce,
            };
        }

        public RuntimeSessionHandshakeResult ReceiveManifest(in RtSessionManifest manifest)
        {
            return ReceiveManifest(manifest.SessionId, manifest.Descriptor, manifest.Assets, manifest.Stores);
        }

        public RuntimeSessionHandshakeResult ReceiveReject(in RtProtocolReject reject)
        {
            return ReceiveReject(reject.Code, reject.Detail);
        }

        public RtSessionReady CreateReadyMessage()
        {
            if (State != RuntimeSessionHandshakeState.Ready)
            {
                throw new InvalidOperationException($"Ready cannot be sent while handshake state is {State}.");
            }

            return new RtSessionReady { SessionId = _manifest.SessionId };
        }
#endif

        private RuntimeSessionHandshakeResult Reject(RuntimeProtocolRejectCode code, string detail)
        {
            var result = RuntimeSessionHandshakeResult.Reject(code, detail);
            State = RuntimeSessionHandshakeState.Rejected;
            _manifest = null;
            return result;
        }
    }
}
