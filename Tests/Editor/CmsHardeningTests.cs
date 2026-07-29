#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using DingoGameObjectsCMS.AssetLibrary.AssetsEdit;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.RuntimeObjects.Commands;
using DingoGameObjectsCMS.RuntimeObjects.Objects;
using DingoGameObjectsCMS.RuntimeObjects.Stores;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class CmsHardeningRuntimeComponent : GameRuntimeComponent
    {
    }

    public class CmsHardeningSecondRuntimeComponent : GameRuntimeComponent
    {
    }

    public class CmsHardeningTests
    {
        [Test]
        public void TypeAliasBinder_RejectsUnknownTypesInBothDirections()
        {
            var binder = new TypeAliasBinder(
                new[] { typeof(GameAsset) },
                type => type.Name);

            Assert.That(
                binder.BindToType(null, nameof(GameAsset)),
                Is.EqualTo(typeof(GameAsset)));
            Assert.Throws<JsonSerializationException>(
                () => binder.BindToType("System.Private.CoreLib", "System.Version"));
            Assert.Throws<JsonSerializationException>(() =>
            {
                binder.BindToName(typeof(Version), out _, out _);
            });
        }

        [Test]
        public void GameAssetJson_RejectsUnknownRootTypeAlias()
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            try
            {
                asset.ResetToDefault(
                    new GameAssetKey("test", "asset", "strict_alias", "0.0.0"));
                var json = JObject.Parse(asset.ToJson());
                json["$type"] = "System.Version";

                Assert.Throws<JsonSerializationException>(
                    () => GameAssetJson.FromJObject(json));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void GameAssetJson_RejectsRegisteredRuntimeTypeOutsideAssetHierarchy()
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            try
            {
                asset.ResetToDefault(
                    new GameAssetKey("test", "asset", "wrong_domain", "0.0.0"));
                var json = JObject.Parse(asset.ToJson());
                json["$type"] = nameof(CmsHardeningRuntimeComponent);

                Assert.Throws<JsonSerializationException>(
                    () => GameAssetJson.FromJObject(json));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void GameRuntimeJson_RejectsUnknownComponentTypeAlias()
        {
            const string json =
                "{\"Components\":[{\"$type\":\"System.Version\"}]}";

            Assert.Throws<JsonSerializationException>(
                () => JsonConvert.DeserializeObject<GameRuntimeObject>(
                    json,
                    GameRuntimeJson.Settings));
        }

        [Test]
        public void PlainManifestSerialization_DoesNotUsePolymorphicTypeMetadata()
        {
            var manifest = new ModManifest
            {
                Mod = "test",
                Assets = new List<ModManifestEntry>()
            };

            var json = JsonConvert.SerializeObject(
                manifest,
                Formatting.None,
                GameAssetJson.DataSettings);

            Assert.That(json, Does.Not.Contain("\"$type\""));
            Assert.That(
                JsonConvert.DeserializeObject<ModManifest>(
                    json,
                    GameAssetJson.DataSettings),
                Is.Not.Null);
        }

        [Test]
        public void ModPaths_RejectTraversalRootedAndNonCanonicalPaths()
        {
            var root = CreateTempDirectory();
            try
            {
                Assert.That(
                    GameAssetPathPolicy.CombineAbsolute(
                        root,
                        "assets/type/key/key@0.0.0.json"),
                    Does.StartWith(root).IgnoreCase);
                Assert.Throws<InvalidDataException>(
                    () => GameAssetPathPolicy.CombineAbsolute(root, "../escape.json"));
                Assert.Throws<InvalidDataException>(
                    () => GameAssetPathPolicy.CombineAbsolute(
                        root,
                        Path.Combine(root, "escape.json")));
                Assert.Throws<InvalidDataException>(
                    () => GameAssetPathPolicy.CombineAbsolute(
                        root,
                        "assets\\type\\escape.json"));
                Assert.Throws<InvalidDataException>(
                    () => GameAssetPathPolicy.CombineAbsolute(
                        root,
                        "assets//escape.json"));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void ModPackage_RejectsEscapingManifestEntryBeforeRead()
        {
            var root = CreateTempDirectory();
            try
            {
                var manifest = new ModManifest
                {
                    Mod = "test",
                    Assets = new List<ModManifestEntry>
                    {
                        new()
                        {
                            Key = new GameAssetKey(
                                "test",
                                "asset",
                                "escape",
                                "0.0.0"),
                            RelativeJsonPath = "../escape.json"
                        }
                    }
                };

                Assert.Throws<InvalidDataException>(
                    () => new ModPackage(root, manifest));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void ModPackage_RejectsNonCanonicalAssetVersion()
        {
            var root = CreateTempDirectory();
            try
            {
                var manifest = new ModManifest
                {
                    Mod = "test",
                    Assets = new List<ModManifestEntry>
                    {
                        new()
                        {
                            Key = new GameAssetKey(
                                "test",
                                "asset",
                                "prerelease",
                                "1.0.0-beta.2"),
                            RelativeJsonPath =
                                "assets/asset/prerelease/prerelease@1.0.0-beta.2.json"
                        }
                    }
                };

                Assert.Throws<InvalidDataException>(
                    () => new ModPackage(root, manifest));
            }
            finally
            {
                Directory.Delete(root, recursive: true);
            }
        }

        [Test]
        public void NumericAssetVersions_CompareAndIncrementWithoutStringFallback()
        {
            Assert.That(
                GameAssetVersionUtils.Compare("1.0.11", "1.0.2"),
                Is.GreaterThan(0));
            Assert.That(
                GameAssetVersionUtils.IncrementPatch("2.7.9"),
                Is.EqualTo("2.7.10"));
            Assert.Throws<ArgumentException>(
                () => GameAssetVersionUtils.Compare("1.0.0-beta.2", "1.0.0"));
            Assert.Throws<ArgumentException>(
                () => GameAssetVersionUtils.Compare("1.0.0+build", "1.0.0"));
            Assert.Throws<ArgumentException>(
                () => GameAssetVersionUtils.IncrementPatch("invalid"));
        }

        [Test]
        public void RuntimeObjectDeserialize_RejectsDuplicateConcreteComponentTypes()
        {
            var runtimeObject = new GameRuntimeObject();
            SetComponents(
                runtimeObject,
                new List<GameRuntimeComponent>
                {
                    new CmsHardeningRuntimeComponent(),
                    new CmsHardeningRuntimeComponent()
                });

            var exception = Assert.Throws<InvalidOperationException>(
                () => ((ISerializationCallbackReceiver)runtimeObject)
                    .OnAfterDeserialize());
            Assert.That(exception.Message, Does.Contain("duplicate runtime component type"));
        }

        [Test]
        public void RuntimeCommandDeserialize_RejectsDuplicateConcreteComponentTypes()
        {
            var command = new GameRuntimeCommand();
            SetComponents(
                command,
                new List<GameRuntimeComponent>
                {
                    new CmsHardeningRuntimeComponent(),
                    new CmsHardeningRuntimeComponent()
                });

            var exception = Assert.Throws<InvalidOperationException>(
                () => ((ISerializationCallbackReceiver)command)
                    .OnAfterDeserialize());
            Assert.That(exception.Message, Does.Contain("duplicate runtime component type"));
        }

        [Test]
        public void AddOrReplaceById_RejectsConflictingRuntimeTypeIds()
        {
            var runtimeObject = new GameRuntimeObject();
            runtimeObject.AddOrReplaceById(
                uint.MaxValue,
                new CmsHardeningRuntimeComponent());

            Assert.Throws<InvalidOperationException>(
                () => runtimeObject.AddOrReplaceById(
                    uint.MaxValue,
                    new CmsHardeningSecondRuntimeComponent()));
            Assert.Throws<InvalidOperationException>(
                () => runtimeObject.AddOrReplaceById(
                    uint.MaxValue - 1,
                    new CmsHardeningRuntimeComponent()));
        }

        [Test]
        public void EmptyGameAsset_AlwaysCreatesRuntimeCommand()
        {
            var asset = ScriptableObject.CreateInstance<GameAsset>();
            try
            {
                asset.ResetToDefault(
                    new GameAssetKey("test", "command", "empty", "0.0.0"));
                asset.SetComponents(null);

                var command = asset.CreateRuntimeCommand();

                Assert.That(command, Is.Not.Null);
                Assert.That(command.Components, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void ChildrenView_IsLiveButCannotBeCastBackToMutableList()
        {
            using var world = new World(nameof(ChildrenView_IsLiveButCannotBeCastBackToMutableList));
            var store = new RuntimeStore(
                new FixedString32Bytes("readonly-children"),
                StoreRealm.Server,
                world);
            var list = new List<long> { 2, 3 };
            TakePrivateField<Dictionary<long, List<long>>>(
                store,
                "_childrenByParent")[1] = list;

            Assert.That(store.TryTakeChildren(1, out var children), Is.True);
            Assert.That(children, Is.Not.InstanceOf<List<long>>());
            Assert.Throws<NotSupportedException>(
                () => ((IList<long>)children)[0] = 9);

            list.Add(4);
            Assert.That(children, Is.EqualTo(new long[] { 2, 3, 4 }));
        }

        [Test]
        public void RootLookup_HandlesHierarchyDeeperThanPreviousHardLimit()
        {
            using var world = new World(nameof(RootLookup_HandlesHierarchyDeeperThanPreviousHardLimit));
            var store = new RuntimeStore(
                new FixedString32Bytes("deep-hierarchy"),
                StoreRealm.Server,
                world);
            var parents = TakePrivateField<Dictionary<long, long>>(
                store,
                "_parentByChild");
            const int depth = 2048;
            for (var id = 1; id <= depth; id++)
            {
                parents[id] = id - 1;
            }

            var root = (long)TakePrivateMethod(
                store,
                "GetRootId").Invoke(store, new object[] { (long)depth });

            Assert.That(root, Is.Zero);
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"{nameof(CmsHardeningTests)}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return Path.GetFullPath(path)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        }

        private static void SetComponents(
            object target,
            List<GameRuntimeComponent> components)
        {
            var field = target.GetType().GetField(
                "_components",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, components);
        }

        private static T TakePrivateField<T>(object target, string fieldName)
        {
            var field = target.GetType().GetField(
                fieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (T)field.GetValue(target);
        }

        private static MethodInfo TakePrivateMethod(
            object target,
            string methodName)
        {
            var method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return method;
        }
    }
}
#endif
