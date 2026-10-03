#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.AssetObjects.Authoring;
using DingoGameObjectsCMS.Mirror.Protocol;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Overrides;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Serialization;
using DingoGameObjectsCMS.Stores;
using DingoUnityExtensions;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using Unity.Collections;
using UnityEngine;
using Hash128 = UnityEngine.Hash128;
using World = Unity.Entities.World;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class PrefabGameAssetLibraryIntegrationTests
    {
        private PrefabGameAssetTestAssets _assets;
        private PrefabGameAssetIndex _index;
        private PrefabGameAssetDefinitions _definitions;
        private GameAssetTemplateCache _templates;
        private string _packageRoot;
        private readonly List<GameAssetScriptableObject> _moduleAssets = new();

        [SetUp]
        public void SetUp()
        {
            _assets = new PrefabGameAssetTestAssets();
            _index = ScriptableObject.CreateInstance<PrefabGameAssetIndex>();
            _templates = CreateTemplates();
            _packageRoot = Path.Combine(Path.GetTempPath(), nameof(PrefabGameAssetLibraryIntegrationTests) + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_packageRoot);
        }

        [TearDown]
        public void TearDown()
        {
            _templates.Clear();
            _definitions?.Dispose();
            foreach (var asset in _moduleAssets)
            {
                if (asset != null)
                    UnityEngine.Object.DestroyImmediate(asset);
            }
            _moduleAssets.Clear();
            UnityEngine.Object.DestroyImmediate(_index);
            _assets.Dispose();
            var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var packageRoot = Path.GetFullPath(_packageRoot);
            Assert.That(packageRoot.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase), Is.True);
            if (Directory.Exists(packageRoot))
                Directory.Delete(packageRoot, true);
        }

        [Test]
        public void NativeAndJsonDefinitionsMountBeforeSealAndResolveExactAndLatestRequests()
        {
            var authoring = PrepareNative("native");
            var reference = new PrefabGameAssetReference(authoring);
            Assert.That(_definitions.TryResolveReference(reference, out var nativeReference), Is.True);
            var legacyKey = new GameAssetKey("base", "data", "legacy", "1.0.0");
            var package = CreatePackage(legacyKey, Hash128.Compute("legacy-json-definition"));

            var library = GameAssetLibraryLockBuilder.Build(_templates, new[] { package }, _definitions);

            Assert.That(library.IsReadOnly, Is.True);
            Assert.That(GameAssetLibraryLockBuilder.TryResolve(nativeReference.RequestedKey, library, out var native), Is.True);
            Assert.That(native.Key, Is.EqualTo(nativeReference.RequestedKey));
            Assert.That(native.GUID, Is.EqualTo(Hash128.Parse(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring)))));
            var latest = new GameAssetKey(native.Key.Mod, native.Key.Type, native.Key.Key, string.Empty);
            Assert.That(GameAssetLibraryLockBuilder.TryResolve(latest, library, out var latestAsset), Is.True);
            Assert.That(latestAsset, Is.SameAs(native));
            Assert.That(GameAssetLibraryLockBuilder.TryResolve(legacyKey, library, out var legacy), Is.True);
            Assert.That(legacy.Key, Is.EqualTo(legacyKey));
            Assert.That(library.TryGetMod("_p_unity", out var nativeMod), Is.True);
            Assert.That(nativeMod.ContentHash, Has.Length.EqualTo(64));
            var repeated = GameAssetLibraryLockBuilder.Build(CreateTemplates(), new[] { package }, _definitions);
            Assert.That(repeated.TryGetMod("_p_unity", out var repeatedMod), Is.True);
            Assert.That(repeatedMod.ContentHash, Is.EqualTo(nativeMod.ContentHash));
            Assert.That(library.TryGetMod("base", out _), Is.True);

            var catalog = RuntimeSessionAssetCatalog.FromLock(library);
            Assert.That(catalog.ManifestEntries.Count, Is.EqualTo(2), "Exact and latest aliases must share one network asset identity.");
            Assert.That(catalog.ManifestEntries.Any(entry => entry.AssetGuid == native.GUID.ToString()), Is.True);
        }

        [Test]
        public void NativeOnlyLockMaterializesExactPrefabOriginAndIndependentInstances()
        {
            var authoring = PrepareNative("materialization");
            Assert.That(_definitions.TryResolveReference(new PrefabGameAssetReference(authoring), out var reference), Is.True);
            var library = GameAssetLibraryLockBuilder.Build(_templates, Array.Empty<ModPackage>(), _definitions);
            var firstGuid = Hash128.Compute("first-native-runtime-instance");
            var secondGuid = Hash128.Compute("second-native-runtime-instance");

            var first = _templates.Materialize(GameAssetInstanceFactory.Create(reference, firstGuid), library);
            var second = _templates.Materialize(GameAssetInstanceFactory.Create(reference, secondGuid), library);

            Assert.That(first.GUID, Is.EqualTo(firstGuid));
            Assert.That(second.GUID, Is.EqualTo(secondGuid));
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.Origin.InstanceGuid, Is.EqualTo(firstGuid));
            Assert.That(first.Origin.Asset.ExactKey, Is.EqualTo(reference.RequestedKey));
            Assert.That(first.Origin.Asset.AssetGuid, Is.EqualTo(Hash128.Parse(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring)))));
            Assert.That(first.Origin.Asset.MaterializedContentHash, Is.Not.Empty);
            Assert.That(first.RuntimeGameAssetIdentity.AssetIndex, Is.EqualTo(second.RuntimeGameAssetIdentity.AssetIndex));
            Assert.That(_templates.ResolveStrict(reference, library).Asset.MaterializedContentHash, Is.EqualTo(first.Origin.Asset.MaterializedContentHash));
        }

        [Test]
        public void NativePrefabOriginSurvivesBinaryBaselineAndReplicaMaterialization()
        {
            var authoring = PrepareNative("replica");
            Assert.That(_definitions.TryResolveReference(new PrefabGameAssetReference(authoring), out var reference), Is.True);
            var serverLibrary = GameAssetLibraryLockBuilder.Build(_templates, Array.Empty<ModPackage>(), _definitions);
            var serverCatalog = RuntimeSessionAssetCatalog.FromLock(serverLibrary);
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var clientDefinitions, out var error), Is.True, error);
            var clientTemplates = CreateTemplates();
            var clientLibrary = GameAssetLibraryLockBuilder.Build(clientTemplates, Array.Empty<ModPackage>(), clientDefinitions);
            var clientCatalog = RuntimeSessionAssetCatalog.FromLock(clientLibrary);
            GameObject ownedCoroutineParent = null;
            World world = null;
            try
            {
                if (CoroutineParent.GetNoCheck() == null)
                {
                    ownedCoroutineParent = new GameObject(nameof(PrefabGameAssetLibraryIntegrationTests));
                    ownedCoroutineParent.AddComponent<CoroutineParent>();
                }
                RuntimeStores.ResetState();
                world = new World(nameof(PrefabGameAssetLibraryIntegrationTests));
                RuntimeStores.SetupWorld(world);
                world.GetOrCreateSystemManaged<Unity.Entities.EndSimulationEntityCommandBufferSystem>();
                var storeId = new FixedString32Bytes("native-prefab-replica");
                var server = RuntimeStores.GetOrAddRuntimeStore(storeId, realm: StoreRealm.Server);
                var instanceGuid = Hash128.Compute("native-prefab-replica-instance");
                var original = server.Spawn(GameAssetInstanceFactory.Create(reference, instanceGuid), serverLibrary, _templates);
                server.FlushToQuiescence();
                var policies = new RuntimeReplicationPolicyRegistry();
                policies.Seal(Array.Empty<uint>());
                var baseline = RuntimeStoreBaselineBuilder.Build(server, serverCatalog, 1, _templates, serverLibrary, RuntimeTemplatePatchCodecContext.Instance, policies);
                var codec = new RuntimeStoreBaselineCodec(_templates.CodecRegistry);
                var bytes = codec.Encode(baseline);
                var spawnFactory = new RuntimeReplicaBaselineSpawnFactory(clientCatalog, clientLibrary, clientTemplates, policies);
                var staging = new RuntimeReplicaStagingRealms();
                var stager = new RuntimeReplicaBaselineStager(world, codec, spawnFactory, staging);
                var stage = stager.Prepare(bytes);
                stage.Build();
                var replica = stage.Publish();

                Assert.That(replica.TryTakeRO(original.InstanceId, out var restored), Is.True);
                Assert.That(restored.GUID, Is.EqualTo(instanceGuid));
                Assert.That(restored.Origin.InstanceGuid, Is.EqualTo(instanceGuid));
                Assert.That(restored.Origin.Asset.ExactKey, Is.EqualTo(reference.RequestedKey));
                Assert.That(restored.Origin.Asset.AssetGuid, Is.EqualTo(original.Origin.Asset.AssetGuid));
                Assert.That(restored.Origin.Asset.MaterializedContentHash, Is.EqualTo(original.Origin.Asset.MaterializedContentHash));
                Assert.That(restored.RuntimeGameAssetIdentity.AssetIndex, Is.EqualTo(original.RuntimeGameAssetIdentity.AssetIndex));
                Assert.That(restored.HasEntityProjection, Is.True, "Baseline staging projects ordinary replica objects into the supplied ECS world.");
                Assert.That(replica.TryGetEntity(restored.InstanceId, out var entity), Is.True);
                Assert.That(world.EntityManager.Exists(entity), Is.True);
                Assert.That(world.EntityManager.GetComponentData<RuntimeInstance>(entity).Id, Is.EqualTo(restored.InstanceId));
                Assert.That(world.EntityManager.GetComponentData<RuntimeInstance>(entity).StoreId, Is.EqualTo(replica.Id));
            }
            finally
            {
                RuntimeStores.ResetState();
                if (world != null && world.IsCreated)
                    world.Dispose();
                clientTemplates.Clear();
                clientDefinitions.Dispose();
                if (ownedCoroutineParent != null)
                    UnityEngine.Object.DestroyImmediate(ownedCoroutineParent);
            }
        }

        [Test]
        public void PreparedDefinitionValuesAndNativeHashRemainFixedUntilAnotherPreparation()
        {
            var authoring = PrepareNative("frozen", new PrefabAuthoringAlpha_GAC { Value = 5 });
            var originalHash = _definitions.NativeContentHash;
            var originalAsset = _definitions.Assets.Single();
            ((PrefabAuthoringAlpha_GAC)authoring.Components.Single()).Value = 99;
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, authoring, "changed-definition-hash") });

            Assert.That(_definitions.NativeContentHash, Is.EqualTo(originalHash));
            Assert.That(((PrefabAuthoringAlpha_GAC)originalAsset.Components.Single()).Value, Is.EqualTo(5));
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var next, out var error), Is.True, error);
            try
            {
                Assert.That(next.NativeContentHash, Is.Not.EqualTo(originalHash));
                Assert.That(((PrefabAuthoringAlpha_GAC)next.Assets.Single().Components.Single()).Value, Is.EqualTo(99));
                Assert.That(next.Assets.Single(), Is.Not.SameAs(originalAsset));
            }
            finally
            {
                next.Dispose();
            }
        }

        [Test]
        public void NativeHashIsIndependentOfInputOrderAndChangesWhenResourceMetadataChanges()
        {
            var first = _assets.CreatePrefab("hash-first");
            var second = _assets.CreatePrefab("hash-second");
            var firstGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(first));
            var secondGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(second));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(firstGuid, first, "first-resource-hash"), new PrefabGameAssetIndexEntry(secondGuid, second, "second-resource-hash") });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            var before = _definitions.NativeContentHash;
            Assert.That(before, Has.Length.EqualTo(64));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(secondGuid, second, "second-resource-hash"), new PrefabGameAssetIndexEntry(firstGuid, first, "first-resource-hash") });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var reordered, out error), Is.True, error);
            try
            {
                Assert.That(reordered.NativeContentHash, Is.EqualTo(before));
            }
            finally
            {
                reordered.Dispose();
            }
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(firstGuid, first, "changed-resource-hash"), new PrefabGameAssetIndexEntry(secondGuid, second, "second-resource-hash") });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var changed, out error), Is.True, error);
            try
            {
                Assert.That(changed.NativeContentHash, Is.Not.EqualTo(before));
            }
            finally
            {
                changed.Dispose();
            }
        }

        [Test]
        public void ResourceOnlyChangesModifyNativeModuleIdentityWithoutChangingRuntimeBaselineHash()
        {
            var authoring = PrepareNative("resource-only");
            var firstLibrary = GameAssetLibraryLockBuilder.Build(_templates, Array.Empty<ModPackage>(), _definitions);
            var reference = new PrefabGameAssetReference(authoring);
            Assert.That(_definitions.TryResolveReference(reference, out var request), Is.True);
            Assert.That(firstLibrary.TryGet(request.RequestedKey, out var firstEntry), Is.True);
            Assert.That(firstLibrary.TryGetMod("_p_unity", out var firstMod), Is.True);
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, authoring, "resource-only-change") });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out var second, out var error), Is.True, error);
            try
            {
                var secondLibrary = GameAssetLibraryLockBuilder.Build(CreateTemplates(), Array.Empty<ModPackage>(), second);
                Assert.That(secondLibrary.TryGet(request.RequestedKey, out var secondEntry), Is.True);
                Assert.That(secondLibrary.TryGetMod("_p_unity", out var secondMod), Is.True);
                Assert.That(secondEntry.MaterializedContentHash, Is.EqualTo(firstEntry.MaterializedContentHash));
                Assert.That(secondMod.ContentHash, Is.Not.EqualTo(firstMod.ContentHash));
                Assert.That(_definitions.NativeContentHash, Is.Not.EqualTo(second.NativeContentHash));
            }
            finally
            {
                second.Dispose();
            }
        }

        [Test]
        public void NativeMountRequiresCapturedContentHashes()
        {
            var authoring = _assets.CreatePrefab("missing-hash");
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, authoring) });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            Assert.Throws<InvalidOperationException>(() => GameAssetLibraryLockBuilder.Build(_templates, Array.Empty<ModPackage>(), _definitions));
        }

        [TestCase("_p_unity")]
        [TestCase("_P_UNITY")]
        public void JsonModulesCannotUseReservedNativeModuleIdentity(string module)
        {
            PrepareNative("reserved-module");
            var package = CreatePackage(new GameAssetKey(module, "data", "shadow", "1.0.0"), Hash128.Compute("reserved-json-definition"));
            var error = Assert.Throws<InvalidOperationException>(() => GameAssetLibraryLockBuilder.Build(_templates, new[] { package }, _definitions));
            Assert.That(error.Message, Does.Contain("_p_unity").IgnoreCase);
        }

        [Test]
        public void JsonAndNativeDefinitionsCannotShareAnAssetGuid()
        {
            var authoring = PrepareNative("guid-collision");
            var nativeGuid = Hash128.Parse(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring)));
            var package = CreatePackage(new GameAssetKey("base", "data", "shadow", "1.0.0"), nativeGuid);
            var error = Assert.Throws<InvalidOperationException>(() => GameAssetLibraryLockBuilder.Build(_templates, new[] { package }, _definitions));
            Assert.That(error.Message, Does.Contain(nativeGuid.ToString()));
        }

        [Test]
        public void DisposedDefinitionsCannotBeMountedAndDoNotDestroyNativePrefabAssets()
        {
            var authoring = PrepareNative("disposed-mount");
            var owned = _definitions.Assets.ToArray();
            _definitions.Dispose();
            Assert.That(authoring != null, Is.True);
            Assert.That(_index != null, Is.True);
            Assert.That(owned.All(asset => asset == null), Is.True);
            Assert.Throws<ObjectDisposedException>(() => GameAssetLibraryLockBuilder.Build(_templates, Array.Empty<ModPackage>(), _definitions));
        }

        private GameAssetAuthoring PrepareNative(string name, GameAssetComponent component = null)
        {
            var authoring = _assets.CreatePrefab(name, new[] { component ?? new PrefabAuthoringBeta_GAC() });
            var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(authoring));
            _index.ReplaceEntries(new[] { new PrefabGameAssetIndexEntry(guid, authoring, "initial-" + name + "-content-hash") });
            Assert.That(PrefabGameAssetDefinitions.TryPrepare(_index, out _definitions, out var error), Is.True, error);
            return authoring;
        }

        private ModPackage CreatePackage(GameAssetKey key, Hash128 guid)
        {
            var moduleRoot = Path.Combine(_packageRoot, key.Mod);
            var relativePath = $"{key.Type}/{key.Key}/{key.Key}@{key.Version}.json";
            var path = Path.Combine(moduleRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var source = ScriptableObject.CreateInstance<GameAsset>();
            try
            {
                source.ResetToDefault(key, guid);
                File.WriteAllText(path, source.ToJson());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(source);
            }
            var manifest = new JObject
            {
                ["Mod"] = key.Mod,
                ["ManifestVersion"] = 1,
                ["GeneratedUtc"] = "2026-10-03T00:00:00Z",
                ["Assets"] = new JArray { new JObject { ["Key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer), ["GUID"] = guid.ToString(), ["RelativeJsonPath"] = relativePath } },
            };
            File.WriteAllText(Path.Combine(moduleRoot, "manifest.json"), manifest.ToString());
            var package = new ModPackage(GameAssetModuleContentScanner.Scan(moduleRoot, key.Mod));
            Assert.That(package.TryGet(key, out var loaded), Is.True);
            _moduleAssets.Add(loaded);
            return package;
        }

        private static GameAssetTemplateCache CreateTemplates()
        {
            return new GameAssetTemplateCache(new RuntimePatchCodecRegistry("native-prefab-integration-tests"), RuntimeTemplatePatchCodecContext.Instance);
        }
    }
}
#endif
