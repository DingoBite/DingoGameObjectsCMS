using System;
using System.Collections.Generic;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    public enum SpriteRenderDepthMode : byte
    {
        None = 0,
        PivotPlane = 1,
        ConstantBand = 2,
        DepthMap = 3,
    }

    [Serializable, Preserve]
    public class SpriteRenderSettings
    {
        public GameAssetResourceRef DepthMapResource;
        public SpriteRenderDepthMode DepthMode = SpriteRenderDepthMode.PivotPlane;
        public float PivotX = 0.5f;
        public float PivotY;
        public byte VisualBand = 8;
        public short DepthBias;
        public float AlphaCutoff = 0.5f;
        public bool ParticipatesInDepth = true;
        public short DepthMapMinimum = -128;
        public short DepthMapMaximum = 127;

        public SpriteRenderSettings Copy()
        {
            return new SpriteRenderSettings
            {
                DepthMapResource = DepthMapResource,
                DepthMode = DepthMode,
                PivotX = PivotX,
                PivotY = PivotY,
                VisualBand = VisualBand,
                DepthBias = DepthBias,
                AlphaCutoff = AlphaCutoff,
                ParticipatesInDepth = ParticipatesInDepth,
                DepthMapMinimum = DepthMapMinimum,
                DepthMapMaximum = DepthMapMaximum,
            };
        }

        public void ValidateOrThrow(GameAssetVerifiedPackage package, string context)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }
            if (VisualBand > 15)
            {
                throw new InvalidOperationException($"{context} visual band must fit the four-bit depth band.");
            }
            if (!IsNormalized(PivotX) || !IsNormalized(PivotY))
            {
                throw new InvalidOperationException($"{context} pivot must be normalized to [0, 1].");
            }
            if (!IsNormalized(AlphaCutoff))
            {
                throw new InvalidOperationException($"{context} alpha cutoff must be in [0, 1].");
            }
            if (DepthMode == SpriteRenderDepthMode.DepthMap)
            {
                if (!DepthMapResource.IsDefined)
                {
                    throw new InvalidOperationException($"{context} depth-map mode requires a depth-map resource.");
                }
                _ = package.Resolve(DepthMapResource);
                if (!string.Equals(package.RequireKind(DepthMapResource), "depthMap", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"{context} depth resource must remain a separate depth map.");
                }
                if (DepthMapMinimum >= DepthMapMaximum)
                {
                    throw new InvalidOperationException($"{context} depth-map range must increase.");
                }
            }
            else if (DepthMapResource.IsDefined)
            {
                throw new InvalidOperationException($"{context} declares an unused depth-map resource.");
            }
        }

        private static bool IsNormalized(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f && value <= 1f;
        }
    }

    [Serializable, Preserve]
    public class SpriteRenderPreset
    {
        public string Id;
        public SpriteRenderSettings Settings = new();
    }

    [Serializable, Preserve]
    public class SpriteRenderModulePresets
    {
        public const int CURRENT_FORMAT_VERSION = 1;
        public const string RESOURCE_PATH = "render.presets.json";

        public int FormatVersion = CURRENT_FORMAT_VERSION;
        public string DefaultPresetId;
        public List<SpriteRenderPreset> Presets = new();
    }

    /// <summary>
    /// Sparse per-sprite exception applied over a named module preset.
    /// Null fields inherit the selected preset value.
    /// </summary>
    [Serializable, Preserve]
    public class SpriteRenderRecipe
    {
        public GameAssetResourceRef? DepthMapResource;
        public SpriteRenderDepthMode? DepthMode;
        public float? PivotX;
        public float? PivotY;
        public byte? VisualBand;
        public short? DepthBias;
        public float? AlphaCutoff;
        public bool? ParticipatesInDepth;
        public short? DepthMapMinimum;
        public short? DepthMapMaximum;

        public SpriteRenderSettings ApplyTo(SpriteRenderSettings preset)
        {
            if (preset == null)
            {
                throw new ArgumentNullException(nameof(preset));
            }

            var result = preset.Copy();
            if (DepthMapResource.HasValue)
            {
                result.DepthMapResource = DepthMapResource.Value;
            }
            if (DepthMode.HasValue)
            {
                result.DepthMode = DepthMode.Value;
            }
            if (PivotX.HasValue)
            {
                result.PivotX = PivotX.Value;
            }
            if (PivotY.HasValue)
            {
                result.PivotY = PivotY.Value;
            }
            if (VisualBand.HasValue)
            {
                result.VisualBand = VisualBand.Value;
            }
            if (DepthBias.HasValue)
            {
                result.DepthBias = DepthBias.Value;
            }
            if (AlphaCutoff.HasValue)
            {
                result.AlphaCutoff = AlphaCutoff.Value;
            }
            if (ParticipatesInDepth.HasValue)
            {
                result.ParticipatesInDepth = ParticipatesInDepth.Value;
            }
            if (DepthMapMinimum.HasValue)
            {
                result.DepthMapMinimum = DepthMapMinimum.Value;
            }
            if (DepthMapMaximum.HasValue)
            {
                result.DepthMapMaximum = DepthMapMaximum.Value;
            }
            return result;
        }
    }
}
