#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Linq;
using DingoGameObjectsCMS.AssetLibrary.Manifest;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.Tests.Editor
{
    [Serializable, Preserve]
    public class DiscoveryResourceFixture_GAC : GameAssetComponent
    {
        public GameAssetResourceRef Sprite;
    }

    /// <summary>
    /// A derived asset states its content only as an override of its base, so
    /// resource discovery has to read the composed document. Reading the
    /// authored one loses every resource the override introduces — the sprite
    /// of a variant becomes invisible to the atlas, which then cannot pack it.
    /// </summary>
    public class GameAssetComposedResourceDiscoveryTests
    {
        private const string MODULE = "base";
        private const string BASE_SPRITE = "gameplay_sprites/plain.png";
        private const string VARIANT_SPRITE = "gameplay_sprites/variant.png";

        private static readonly GameAssetKey PLAIN = new(MODULE, "static", "plain", "1.0.0");
        private static readonly GameAssetKey VARIANT = new(MODULE, "static", "variant", "1.0.0");

        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                Path.GetTempPath(),
                $"{nameof(GameAssetComposedResourceDiscoveryTests)}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }

        [Test]
        public void Discovery_FindsTheSpriteAVariantIntroducesThroughItsOverride()
        {
            var package = WriteModule();

            var resources = GameAssetModuleResourceDiscovery
                .CollectLocalResources(package, "sprite")
                .Select(resource => resource.RelativePath)
                .ToList();

            Assert.That(resources, Does.Contain(BASE_SPRITE));
            Assert.That(
                resources,
                Does.Contain(VARIANT_SPRITE),
                "the override names it and nothing else in the module does");
        }

        [Test]
        public void Discovery_AttributesAnInheritedResourceToBothAssets()
        {
            var package = WriteModule();

            var owners = GameAssetModuleResourceDiscovery
                .CollectLocalResourceUses(package, "sprite")
                .Where(use => string.Equals(
                    use.Resource.RelativePath,
                    VARIANT_SPRITE,
                    StringComparison.Ordinal))
                .Select(use => use.AssetKey)
                .ToList();

            Assert.That(owners, Is.EqualTo(new[] { VARIANT }));
        }

        [Test]
        public void ComposedDocument_KeepsTheAuthoredOneReadableAsWritten()
        {
            var package = WriteModule();

            Assert.That(package.TryGetComposedDocument(VARIANT, out var composed), Is.True);
            Assert.That(package.TryGetDocument(VARIANT, out var authored), Is.True);

            Assert.That(
                composed["Components"],
                Is.Not.Null,
                "composition materializes the inherited component list");
            Assert.That(
                authored["Components"],
                Is.Null,
                "the authored document still states only the override");
            Assert.That(authored["Prefab"], Is.Not.Null);
        }

        private ModPackage WriteModule()
        {
            var moduleRoot = Path.Combine(_root, MODULE);
            WriteSprite(moduleRoot, BASE_SPRITE);
            WriteSprite(moduleRoot, VARIANT_SPRITE);

            var plain = new JObject
            {
                ["$type"] = nameof(GameAsset),
                ["Components"] = new JArray
                {
                    new JObject
                    {
                        ["$type"] = nameof(DiscoveryResourceFixture_GAC),
                        ["Sprite"] = new JObject
                        {
                            ["ModuleId"] = MODULE,
                            ["RelativePath"] = BASE_SPRITE,
                        },
                    },
                },
                ["Key"] = JObject.FromObject(PLAIN, GameAssetJson.JsonSerializer),
                ["GUID"] = Guid.NewGuid().ToString("N"),
            };
            var variant = new JObject
            {
                ["$type"] = nameof(GameAsset),
                ["Prefab"] = new JObject
                {
                    ["Base"] = JObject.FromObject(PLAIN, GameAssetJson.JsonSerializer),
                    ["OverrideFields"] = new JObject
                    {
                        [$"/{nameof(DiscoveryResourceFixture_GAC)}/Sprite/RelativePath"] = VARIANT_SPRITE,
                    },
                },
                ["Key"] = JObject.FromObject(VARIANT, GameAssetJson.JsonSerializer),
                ["GUID"] = Guid.NewGuid().ToString("N"),
            };

            var manifestAssets = new JArray();
            foreach (var (key, document) in new[] { (PLAIN, plain), (VARIANT, variant) })
            {
                var relativeJsonPath = $"{key.Type}/{key.Key}/{key.Key}@{key.Version}.json";
                var assetPath = Path.Combine(
                    moduleRoot,
                    relativeJsonPath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(assetPath));
                File.WriteAllText(assetPath, document.ToString());
                manifestAssets.Add(new JObject
                {
                    ["Key"] = JObject.FromObject(key, GameAssetJson.JsonSerializer),
                    ["GUID"] = document["GUID"],
                    ["RelativeJsonPath"] = relativeJsonPath,
                });
            }

            File.WriteAllText(
                Path.Combine(moduleRoot, "manifest.json"),
                new JObject
                {
                    ["Mod"] = MODULE,
                    ["ManifestVersion"] = 1,
                    ["GeneratedUtc"] = "2026-01-01T00:00:00Z",
                    ["Assets"] = manifestAssets,
                }.ToString());

            return new ModPackage(GameAssetModuleContentScanner.Scan(moduleRoot, MODULE));
        }

        private static void WriteSprite(string moduleRoot, string relativePath)
        {
            var path = Path.Combine(
                moduleRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, Array.Empty<byte>());
        }
    }
}
#endif
