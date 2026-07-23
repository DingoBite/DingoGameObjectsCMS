using System;
using System.Collections.Generic;
using System.IO;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public readonly struct SpriteRenderSettingsResolution
    {
        public readonly GameAssetResourceRef SourceResource;
        public readonly GameAssetResourceRef SettingsResource;
        public readonly SpriteRenderSettings Settings;

        public SpriteRenderSettingsResolution(
            GameAssetResourceRef sourceResource,
            GameAssetResourceRef settingsResource,
            SpriteRenderSettings settings)
        {
            SourceResource = sourceResource;
            SettingsResource = settingsResource;
            Settings = settings;
        }
    }

    /// <summary>
    /// Validated module-level sprite preset catalog plus sparse recipe exceptions.
    /// </summary>
    public class SpriteRenderModuleContent
    {
        private readonly GameAssetVerifiedPackage _package;
        private readonly GameAssetResourceRef _presetsResource;
        private readonly List<GameAssetResourceRef> _spriteResources = new();
        private readonly HashSet<GameAssetResourceRef> _spriteSet = new();
        private readonly Dictionary<string, SpriteRenderSettings> _presets = new(StringComparer.Ordinal);
        private readonly string _defaultPresetId;

        public IReadOnlyList<GameAssetResourceRef> SpriteResources => _spriteResources;

        public SpriteRenderModuleContent(GameAssetVerifiedPackage package)
        {
            _package = package ?? throw new ArgumentNullException(nameof(package));
            _presetsResource = new GameAssetResourceRef(package.ModuleId, SpriteRenderModulePresets.RESOURCE_PATH);
            if (!string.Equals(package.RequireKind(_presetsResource), "spriteRenderPresets", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Module '{package.ModuleId}' must declare '{SpriteRenderModulePresets.RESOURCE_PATH}' as spriteRenderPresets.");
            }

            var catalog = package.LoadJson<SpriteRenderModulePresets>(_presetsResource);
            var discoveredSprites = GameAssetModuleResourceDiscovery.CollectLocalResources(package, "sprite");
            for (var index = 0; index < discoveredSprites.Count; index++)
            {
                var resource = discoveredSprites[index];
                _spriteSet.Add(resource);
                _spriteResources.Add(resource);
            }
            ValidateCatalog(catalog);
            _defaultPresetId = catalog.DefaultPresetId;
        }

        public SpriteRenderSettingsResolution Resolve(in SpriteVisualRef visual)
        {
            if (!visual.IsDefined)
            {
                throw new ArgumentException("A sprite visual resource is required.", nameof(visual));
            }
            var sourceResource = visual.Resource;
            if (!_spriteSet.Contains(sourceResource))
            {
                throw new InvalidDataException(
                    $"Sprite resource '{sourceResource}' is not referenced by a GameAsset in module '{_package.ModuleId}'.");
            }

            var presetId = string.IsNullOrWhiteSpace(visual.PresetId)
                ? _defaultPresetId
                : RequirePresetId(visual.PresetId, $"Visual '{sourceResource}'");
            var settingsResource = _presetsResource;
            SpriteRenderRecipe recipe = null;
            if (visual.RecipeResource.IsDefined)
            {
                if (!string.Equals(visual.RecipeResource.ModuleId, _package.ModuleId, StringComparison.Ordinal)
                    || !string.Equals(
                        _package.RequireKind(visual.RecipeResource),
                        "spriteRecipe",
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Visual '{sourceResource}' recipe '{visual.RecipeResource}' is not a local sprite recipe.");
                }
                recipe = _package.LoadJson<SpriteRenderRecipe>(visual.RecipeResource);
                settingsResource = visual.RecipeResource;
            }

            var settings = _presets[presetId].Copy();
            if (recipe != null)
            {
                settings = recipe.ApplyTo(settings);
            }
            settings.ValidateOrThrow(_package, $"Resolved sprite settings for '{sourceResource}'");
            return new SpriteRenderSettingsResolution(sourceResource, settingsResource, settings);
        }

        public SpriteRenderSettingsResolution Resolve(in GameAssetResourceRef sourceResource)
        {
            return Resolve(new SpriteVisualRef(sourceResource));
        }

        private void ValidateCatalog(SpriteRenderModulePresets catalog)
        {
            if (catalog == null || catalog.FormatVersion != SpriteRenderModulePresets.CURRENT_FORMAT_VERSION)
            {
                throw new InvalidDataException($"Module '{_package.ModuleId}' sprite render preset format is invalid.");
            }
            if (catalog.Presets == null || catalog.Presets.Count == 0)
            {
                throw new InvalidDataException($"Module '{_package.ModuleId}' has no sprite render presets.");
            }
            for (var index = 0; index < catalog.Presets.Count; index++)
            {
                var preset = catalog.Presets[index]
                             ?? throw new InvalidDataException($"Module '{_package.ModuleId}' contains a null sprite preset.");
                var id = RequireId(preset.Id, $"presets[{index}].id");
                if (preset.Settings == null || !_presets.TryAdd(id, preset.Settings.Copy()))
                {
                    throw new InvalidDataException($"Module '{_package.ModuleId}' sprite preset '{id}' is duplicated or null.");
                }
                if (preset.Settings.DepthMode == SpriteRenderDepthMode.DepthMap
                    || preset.Settings.DepthMapResource.IsDefined)
                {
                    throw new InvalidDataException(
                        $"Module preset '{id}' cannot own a per-sprite depth map; use a recipe exception.");
                }
                preset.Settings.ValidateOrThrow(_package, $"Module preset '{id}'");
            }

            _ = RequirePresetId(catalog.DefaultPresetId, nameof(catalog.DefaultPresetId));
            if (_spriteResources.Count == 0)
            {
                throw new InvalidDataException(
                    $"Module '{_package.ModuleId}' manifest GameAssets do not reference any render sprites.");
            }

        }

        private string RequirePresetId(string id, string context)
        {
            var canonical = RequireId(id, context);
            if (!_presets.ContainsKey(canonical))
            {
                throw new InvalidDataException($"{context} references unknown preset '{canonical}'.");
            }
            return canonical;
        }

        private static string RequireId(string id, string context)
        {
            if (string.IsNullOrWhiteSpace(id) || !string.Equals(id, id.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"{context} is empty or not canonical.");
            }
            for (var index = 0; index < id.Length; index++)
            {
                var value = id[index];
                if ((value >= 'a' && value <= 'z')
                    || (value >= '0' && value <= '9')
                    || value == '-'
                    || value == '_')
                {
                    continue;
                }
                throw new InvalidDataException($"{context} '{id}' must use lowercase ASCII tokens.");
            }
            return id;
        }

    }
}
