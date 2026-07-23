using System;
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
    public class SpriteRenderRecipe
    {
        public GameAssetResourceRef SourceResource;
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

        public void ValidateOrThrow(GameAssetVerifiedPackage package)
        {
            if (package == null)
                throw new ArgumentNullException(nameof(package));
            if (!SourceResource.IsDefined)
                throw new InvalidOperationException("Sprite render recipe requires a source resource.");
            _ = package.Resolve(SourceResource);
            if (!string.Equals(package.RequireKind(SourceResource), "sprite", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Sprite render recipe source must be declared as a sprite.");
            }
            if (DepthMode == SpriteRenderDepthMode.DepthMap)
            {
                if (!DepthMapResource.IsDefined)
                    throw new InvalidOperationException("DepthMap sprite render recipe requires a depth-map resource.");
                _ = package.Resolve(DepthMapResource);
                if (!string.Equals(package.RequireKind(DepthMapResource), "depthMap", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Sprite render recipe depth resource must remain a separate depth map.");
                }
                if (DepthMapMinimum >= DepthMapMaximum)
                    throw new InvalidOperationException("Sprite render recipe depth-map range must increase.");
            }
            if (VisualBand > 15)
                throw new InvalidOperationException("Sprite render recipe visual band must fit the four-bit depth band.");
            if (float.IsNaN(PivotX)
                || float.IsInfinity(PivotX)
                || float.IsNaN(PivotY)
                || float.IsInfinity(PivotY))
                throw new InvalidOperationException("Sprite render recipe pivot must be finite.");
            if (float.IsNaN(AlphaCutoff) || AlphaCutoff < 0f || AlphaCutoff > 1f)
                throw new InvalidOperationException("Sprite render recipe alpha cutoff must be in [0, 1].");
        }
    }
}
