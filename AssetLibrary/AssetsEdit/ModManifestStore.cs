#if NEWTONSOFT_EXISTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMS.Modding;
using DingoGameObjectsCMS.RuntimeObjects;
using DingoGameObjectsCMS.Serialization;
using Newtonsoft.Json;
using UnityEngine;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class ModManifestStore
    {
        public const string MANIFEST_FILE_NAME = "manifest.json";

        public static async Task<ModManifest> LoadAsync(string modRootAbs, string mod, CancellationToken ct = default)
        {
            Directory.CreateDirectory(modRootAbs);
            var path = GetManifestPath(modRootAbs);
            if (!File.Exists(path))
                return CreateEmpty(mod);

            var json = await File.ReadAllTextAsync(path, ct);
            var manifest = JsonConvert.DeserializeObject<ModManifest>(json, GameAssetJson.Settings) ?? CreateEmpty(mod);
            Normalize(manifest, mod);
            return manifest;
        }

        public static async Task SaveAsync(string modRootAbs, ModManifest manifest, CancellationToken ct = default)
        {
            Directory.CreateDirectory(modRootAbs);
            Normalize(manifest, manifest?.Mod);
            var json = JsonConvert.SerializeObject(manifest, Formatting.Indented, GameAssetJson.Settings);
            await GameAssetJsonStore.WriteTextAtomicAsync(GetManifestPath(modRootAbs), json, ct);
        }

        public static ModManifestEntry FindByKey(ModManifest manifest, GameAssetKey key)
        {
            return manifest?.Assets?.FirstOrDefault(entry => entry.Key == key);
        }

        public static ModManifestEntry FindByGuid(ModManifest manifest, Hash128 guid)
        {
            return manifest?.Assets?.FirstOrDefault(entry => entry.GUID == guid);
        }

        public static void Upsert(ModManifest manifest, ModManifestEntry entry)
        {
            Normalize(manifest, manifest.Mod);
            manifest.Assets.RemoveAll(existing => existing.Key == entry.Key);
            manifest.Assets.Add(entry);
            manifest.Assets = manifest.Assets
                .OrderBy(item => item.Key.Type, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Key.Key, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Key.Version, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static bool Remove(ModManifest manifest, GameAssetKey key, out ModManifestEntry removed)
        {
            removed = null;
            if (manifest?.Assets == null)
                return false;

            var index = manifest.Assets.FindIndex(entry => entry.Key == key);
            if (index < 0)
                return false;

            removed = manifest.Assets[index];
            manifest.Assets.RemoveAt(index);
            return true;
        }
        
        public static string GetManifestPath(string modRootAbs)
        {
            return Path.Combine(modRootAbs, MANIFEST_FILE_NAME);
        }

        private static ModManifest CreateEmpty(string mod)
        {
            return new ModManifest
            {
                Mod = string.IsNullOrWhiteSpace(mod) ? GameAssetKey.UNDEFINED : mod,
                GeneratedUtc = DateTime.UtcNow.ToString("O"),
                ManifestVersion = 1,
                Assets = new List<ModManifestEntry>()
            };
        }

        private static void Normalize(ModManifest manifest, string mod)
        {
            if (manifest == null)
                return;

            manifest.Mod = string.IsNullOrWhiteSpace(manifest.Mod)
                ? string.IsNullOrWhiteSpace(mod) ? GameAssetKey.UNDEFINED : mod
                : manifest.Mod;
            manifest.GeneratedUtc = DateTime.UtcNow.ToString("O");
            manifest.ManifestVersion = Math.Max(1, manifest.ManifestVersion);
            manifest.Assets ??= new List<ModManifestEntry>();
        }
    }
}
#endif