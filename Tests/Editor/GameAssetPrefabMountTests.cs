#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Scripting;
using Object = UnityEngine.Object;

namespace DingoGameObjectsCMS.Tests.Editor
{
    [Serializable, Preserve]
    public class PrefabMountFixture_GAC : GameAssetComponent
    {
        public int Value;
        public string Label;
        public PrefabMountFixtureVisual Visual;
    }

    [Serializable, Preserve]
    public class PrefabMountFixtureVisual
    {
        public string Path;
        public int PixelsPerUnit;
    }

    [Serializable, Preserve, GameAssetSetupOrder(100)]
    public class PrefabMountLateFixture_GAC : GameAssetComponent
    {
    }

    [Serializable, Preserve, GameAssetSetupOrder(-100)]
    public class PrefabMountEarlyFixture_GAC : GameAssetComponent
    {
    }

    [Serializable, Preserve]
    public class PrefabMountInheritedOrderFixture_GAC : PrefabMountEarlyFixture_GAC
    {
    }

    public class GameAssetPrefabMountTests
    {
        private const string ROOT_MODULE = "base";
        private const string MIDDLE_MODULE = "middle";
        private const string LEAF_MODULE = "playground";

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), $"{nameof(GameAssetPrefabMountTests)}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void MountedPackages_ComposePrefabAcrossModules()
        {
            var rootPackage = WriteRootModule();
            var leafPackage = WriteModule(LEAF_MODULE, LeafKey(), Derived(LeafKey(), RootKey(), prefab =>
            {
                prefab["OverrideFields"] = new JObject { ["/PrefabMountFixture_GAC/Value"] = 1 };
            }));
            Bind(rootPackage, leafPackage);

            Assert.That(leafPackage.TryGet(LeafKey(), out var asset), Is.True);

            var derived = (GameAsset)asset;
            var component = (PrefabMountFixture_GAC)derived.Components.Single();
            Assert.That(component.Value, Is.EqualTo(1));
            Assert.That(component.Label, Is.EqualTo("tavern"));
            Assert.That(derived.Key, Is.EqualTo(LeafKey()));
        }

        [Test]
        public void MountedPackages_ComposeAPrefabOfAPrefabAcrossThreeModules()
        {
            var rootPackage = WriteRootModule();
            var middlePackage = WriteModule(MIDDLE_MODULE, MiddleKey(), Derived(MiddleKey(), RootKey(), prefab =>
            {
                prefab["OverrideFields"] = new JObject
                {
                    ["/PrefabMountFixture_GAC/Value"] = 50,
                    ["/PrefabMountFixture_GAC/Visual/PixelsPerUnit"] = 64,
                };
            }));
            var leafPackage = WriteModule(LEAF_MODULE, LeafKey(), Derived(LeafKey(), MiddleKey(), prefab =>
            {
                prefab["OverrideFields"] = new JObject
                {
                    ["/PrefabMountFixture_GAC/Visual/Path"] = "sprites/leaf.png",
                };
                prefab["RemovedFields"] = new JArray { "/PrefabMountFixture_GAC/Label" };
            }));
            Bind(rootPackage, middlePackage, leafPackage);

            Assert.That(leafPackage.TryGet(LeafKey(), out var asset), Is.True);

            var component = (PrefabMountFixture_GAC)((GameAsset)asset).Components.Single();
            Assert.That(component.Value, Is.EqualTo(50), "inherited through the middle asset");
            Assert.That(component.Visual.PixelsPerUnit, Is.EqualTo(64), "inherited through the middle asset");
            Assert.That(component.Visual.Path, Is.EqualTo("sprites/leaf.png"), "overridden by the leaf");
            Assert.That(component.Label, Is.Null, "removed by the leaf");
        }

        [Test]
        public void MountedPackages_ComposeAddedAndRemovedComponents()
        {
            var rootPackage = WriteRootModule();
            var leafPackage = WriteModule(LEAF_MODULE, LeafKey(), Derived(LeafKey(), RootKey(), prefab =>
            {
                prefab["RemovedComponents"] = new JArray { nameof(PrefabMountFixture_GAC) };
                prefab["OverrideComponents"] = new JArray
                {
                    new JObject { ["$type"] = nameof(PrefabMountLateFixture_GAC) },
                };
            }));
            Bind(rootPackage, leafPackage);

            leafPackage.TryGet(LeafKey(), out var asset);

            Assert.That(((GameAsset)asset).Components.Single(), Is.TypeOf<PrefabMountLateFixture_GAC>());
        }

        [Test]
        public void MountedPackage_WithoutCrossModuleBinding_CannotResolveForeignBase()
        {
            _ = WriteRootModule();
            var leafPackage = WriteModule(LEAF_MODULE, LeafKey(), Derived(LeafKey(), RootKey(), _ => { }));

            var exception = Assert.Throws<InvalidDataException>(() => leafPackage.TryGet(LeafKey(), out _));
            Assert.That(exception.Message, Does.Contain("no mounted module provides"));
        }

        [Test]
        public void ComposedAsset_KeepsNoPrefabDeclaration_AndRawDocumentStaysAuthorable()
        {
            var rootPackage = WriteRootModule();
            var leafPackage = WriteModule(LEAF_MODULE, LeafKey(), Derived(LeafKey(), RootKey(), _ => { }));
            Bind(rootPackage, leafPackage);

            leafPackage.TryGet(LeafKey(), out var asset);

            Assert.That(((GameAsset)asset).Prefab, Is.Null);
            Assert.That(leafPackage.TryGetDocument(LeafKey(), out var document), Is.True);
            Assert.That(document["Prefab"], Is.Not.Null);
            Assert.That(document["Components"], Is.Null);
        }

        [Test]
        public void UnrelatedAsset_LoadsUnchanged()
        {
            var rootPackage = WriteRootModule();
            Bind(rootPackage);

            Assert.That(rootPackage.TryGet(RootKey(), out var asset), Is.True);

            var component = (PrefabMountFixture_GAC)((GameAsset)asset).Components.Single();
            Assert.That(component.Value, Is.EqualTo(90));
        }

        [Test]
        public void AssetWithoutPrefab_SerializesWithoutTheOptionalMembers()
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            try
            {
                asset.ResetToDefault(RootKey());

                var json = asset.ToJson();

                Assert.That(json, Does.Not.Contain("\"Prefab\""));
                Assert.That(json, Does.Not.Contain("null"));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void SetupOrder_DecidesMaterializationOrderInsteadOfDocumentOrder()
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            try
            {
                var late = new PrefabMountLateFixture_GAC();
                var early = new PrefabMountEarlyFixture_GAC();
                var firstNeutral = new PrefabMountFixture_GAC { Label = "first" };
                asset.SetComponents(new GameAssetComponent[] { late, firstNeutral, early });

                var ordered = asset.SetupOrdered().ToArray();

                Assert.That(ordered[0], Is.SameAs(early));
                Assert.That(ordered[1], Is.SameAs(firstNeutral));
                Assert.That(ordered[2], Is.SameAs(late));
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void SetupOrder_DefaultsToZeroAndIsInheritedBySubclasses()
        {
            Assert.That(
                GameAssetSetupOrderUtils.GetOrder(typeof(PrefabMountFixture_GAC)),
                Is.EqualTo(GameAssetSetupOrderUtils.DEFAULT_ORDER));
            Assert.That(GameAssetSetupOrderUtils.GetOrder(typeof(PrefabMountLateFixture_GAC)), Is.EqualTo(100));
            Assert.That(
                GameAssetSetupOrderUtils.GetOrder(typeof(PrefabMountInheritedOrderFixture_GAC)),
                Is.EqualTo(-100));
        }

        private static void Bind(params ModPackage[] packages)
        {
            for (var index = 0; index < packages.Length; index++)
            {
                packages[index].BindMountedDocumentResolver(key => Resolve(packages, key));
            }
        }

        private static JObject Resolve(ModPackage[] packages, GameAssetKey key)
        {
            for (var index = 0; index < packages.Length; index++)
            {
                if (packages[index].TryGetDocument(key, out var document))
                    return document;
            }

            return null;
        }

        private static GameAssetKey RootKey()
        {
            return new GameAssetKey(ROOT_MODULE, "static", "tavern", "1.0.0");
        }

        private static GameAssetKey MiddleKey()
        {
            return new GameAssetKey(MIDDLE_MODULE, "static", "tavern_big", "1.0.0");
        }

        private static GameAssetKey LeafKey()
        {
            return new GameAssetKey(LEAF_MODULE, "static", "tavern_million", "1.0.0");
        }

        private static JObject Derived(GameAssetKey key, GameAssetKey baseKey, Action<JObject> configurePrefab)
        {
            var prefab = new JObject { ["Base"] = JObject.FromObject(baseKey, GameAssetJson.JsonSerializer) };
            configurePrefab(prefab);
            return new JObject
            {
                ["$type"] = nameof(GameAsset),
                ["Prefab"] = prefab,
                ["Key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer),
                ["GUID"] = Guid.NewGuid().ToString("N"),
            };
        }

        private ModPackage WriteRootModule()
        {
            var key = RootKey();
            var document = new JObject
            {
                ["$type"] = nameof(GameAsset),
                ["Components"] = new JArray
                {
                    new JObject
                    {
                        ["$type"] = nameof(PrefabMountFixture_GAC),
                        ["Value"] = 90,
                        ["Label"] = "tavern",
                        ["Visual"] = new JObject
                        {
                            ["Path"] = "sprites/tavern.png",
                            ["PixelsPerUnit"] = 32,
                        },
                    },
                },
                ["Key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer),
                ["GUID"] = Guid.NewGuid().ToString("N"),
            };
            return WriteModule(ROOT_MODULE, key, document);
        }

        private ModPackage WriteModule(string moduleId, GameAssetKey key, JObject document)
        {
            var relativeJsonPath = $"{key.Type}/{key.Key}/{key.Key}@{key.Version}.json";
            var moduleRoot = Path.Combine(_root, moduleId);
            var assetPath = Path.Combine(moduleRoot, relativeJsonPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
            File.WriteAllText(assetPath, document.ToString());

            var manifest = new JObject
            {
                ["Mod"] = moduleId,
                ["ManifestVersion"] = 1,
                ["GeneratedUtc"] = "2026-01-01T00:00:00Z",
                ["Assets"] = new JArray
                {
                    new JObject
                    {
                        ["Key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer),
                        ["GUID"] = document["GUID"],
                        ["RelativeJsonPath"] = relativeJsonPath,
                    },
                },
            };
            File.WriteAllText(Path.Combine(moduleRoot, "manifest.json"), manifest.ToString());

            return new ModPackage(GameAssetModuleContentScanner.Scan(moduleRoot, moduleId));
        }
    }
}
#endif
