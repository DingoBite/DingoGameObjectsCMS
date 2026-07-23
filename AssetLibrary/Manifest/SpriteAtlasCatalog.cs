using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine.Scripting;

namespace DingoGameObjectsCMS.AssetLibrary.Manifest
{
    [Serializable, Preserve]
    public struct SpriteAtlasPixelRect : IEquatable<SpriteAtlasPixelRect>
    {
        public int X;
        public int Y;
        public int Width;
        public int Height;

        public int XMax => checked(X + Width);
        public int YMax => checked(Y + Height);

        public SpriteAtlasPixelRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public void ValidateOrThrow()
        {
            if (X < 0 || Y < 0 || Width <= 0 || Height <= 0)
            {
                throw new InvalidDataException(
                    $"Sprite atlas rect ({X}, {Y}, {Width}, {Height}) must have a non-negative origin and positive size.");
            }

            _ = XMax;
            _ = YMax;
        }

        public bool Overlaps(in SpriteAtlasPixelRect other)
        {
            return X < other.XMax && XMax > other.X && Y < other.YMax && YMax > other.Y;
        }

        public bool Equals(SpriteAtlasPixelRect other)
        {
            return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
        }

        public override bool Equals(object obj)
        {
            return obj is SpriteAtlasPixelRect other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(X, Y, Width, Height);
        }
    }

    [Serializable, Preserve]
    public class SpriteAtlasPage
    {
        public GameAssetResourceRef Resource;
        public int Width;
        public int Height;
    }

    [Serializable, Preserve]
    public class SpriteAtlasEntry
    {
        public GameAssetResourceRef SourceResource;
        public GameAssetResourceRef PageResource;
        public SpriteAtlasPixelRect PixelRect;
    }

    [Serializable, Preserve]
    public class SpriteAtlasCatalog
    {
        public const int CURRENT_FORMAT_VERSION = 1;
        public const string DEFAULT_RELATIVE_PATH = "render_atlas/catalog.json";

        public int FormatVersion = CURRENT_FORMAT_VERSION;
        public string ModuleId;
        public int PaddingPixels;
        public List<SpriteAtlasPage> Pages = new();
        public List<SpriteAtlasEntry> Entries = new();

        public SpriteAtlasEntry RequireEntry(in GameAssetResourceRef sourceResource)
        {
            for (var index = 0; index < Entries.Count; index++)
            {
                if (Entries[index].SourceResource.Equals(sourceResource))
                {
                    return Entries[index];
                }
            }

            throw new InvalidDataException($"Sprite atlas catalog has no entry for '{sourceResource}'.");
        }

        public SpriteAtlasPage RequirePage(in GameAssetResourceRef pageResource)
        {
            for (var index = 0; index < Pages.Count; index++)
            {
                if (Pages[index].Resource.Equals(pageResource))
                {
                    return Pages[index];
                }
            }

            throw new InvalidDataException($"Sprite atlas catalog has no page '{pageResource}'.");
        }

        public void ValidateOrThrow(GameAssetVerifiedPackage package, int maximumTextureSize)
        {
            if (package == null)
            {
                throw new ArgumentNullException(nameof(package));
            }
            if (FormatVersion != CURRENT_FORMAT_VERSION)
            {
                throw new InvalidDataException(
                    $"Sprite atlas catalog format {FormatVersion} does not match required format {CURRENT_FORMAT_VERSION}.");
            }
            if (!string.Equals(ModuleId, package.ModuleId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Sprite atlas catalog module '{ModuleId}' does not match verified module '{package.ModuleId}'.");
            }
            if (maximumTextureSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumTextureSize));
            }
            if (PaddingPixels < 0)
            {
                throw new InvalidDataException("Sprite atlas padding cannot be negative.");
            }
            if (Pages == null || Pages.Count == 0)
            {
                throw new InvalidDataException("Sprite atlas catalog requires at least one page.");
            }
            if (Entries == null || Entries.Count == 0)
            {
                throw new InvalidDataException("Sprite atlas catalog requires at least one entry.");
            }

            var pages = new Dictionary<GameAssetResourceRef, SpriteAtlasPage>();
            var previousPagePath = string.Empty;
            for (var pageIndex = 0; pageIndex < Pages.Count; pageIndex++)
            {
                var page = Pages[pageIndex] ?? throw new InvalidDataException("Sprite atlas catalog contains a null page.");
                ValidateResource(page.Resource, package, "page");
                if (!string.Equals(package.RequireKind(page.Resource), "atlasPage", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"Sprite atlas page '{page.Resource}' has an invalid package kind.");
                }
                if (page.Width <= 0 || page.Height <= 0
                    || page.Width > maximumTextureSize || page.Height > maximumTextureSize)
                {
                    throw new InvalidDataException(
                        $"Sprite atlas page '{page.Resource}' size {page.Width}x{page.Height} exceeds the supported "
                        + $"maximum {maximumTextureSize} or is not positive.");
                }
                if (pageIndex > 0 && string.CompareOrdinal(previousPagePath, page.Resource.RelativePath) >= 0)
                {
                    throw new InvalidDataException("Sprite atlas pages must be strictly ordered by canonical relative path.");
                }
                if (!pages.TryAdd(page.Resource, page))
                {
                    throw new InvalidDataException($"Sprite atlas page '{page.Resource}' is duplicated.");
                }

                previousPagePath = page.Resource.RelativePath;
            }

            var sources = new HashSet<GameAssetResourceRef>();
            var entriesByPage = new Dictionary<GameAssetResourceRef, List<SpriteAtlasEntry>>();
            var previousSourcePath = string.Empty;
            for (var entryIndex = 0; entryIndex < Entries.Count; entryIndex++)
            {
                var entry = Entries[entryIndex]
                    ?? throw new InvalidDataException("Sprite atlas catalog contains a null entry.");
                ValidateResource(entry.SourceResource, package, "source");
                ValidateResource(entry.PageResource, package, "page");
                if (!string.Equals(package.RequireKind(entry.SourceResource), "sprite", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Sprite atlas source '{entry.SourceResource}' has an invalid package kind.");
                }
                entry.PixelRect.ValidateOrThrow();
                if (entryIndex > 0 && string.CompareOrdinal(previousSourcePath, entry.SourceResource.RelativePath) >= 0)
                {
                    throw new InvalidDataException("Sprite atlas entries must be strictly ordered by source relative path.");
                }
                if (!sources.Add(entry.SourceResource))
                {
                    throw new InvalidDataException($"Sprite atlas source '{entry.SourceResource}' is duplicated.");
                }
                if (!pages.TryGetValue(entry.PageResource, out var page))
                {
                    throw new InvalidDataException(
                        $"Sprite atlas entry '{entry.SourceResource}' refers to undeclared page '{entry.PageResource}'.");
                }
                if (entry.PixelRect.XMax > page.Width || entry.PixelRect.YMax > page.Height)
                {
                    throw new InvalidDataException(
                        $"Sprite atlas entry '{entry.SourceResource}' rect lies outside page '{entry.PageResource}'.");
                }
                if (!entriesByPage.TryGetValue(entry.PageResource, out var pageEntries))
                {
                    pageEntries = new List<SpriteAtlasEntry>();
                    entriesByPage.Add(entry.PageResource, pageEntries);
                }
                for (var placedIndex = 0; placedIndex < pageEntries.Count; placedIndex++)
                {
                    if (entry.PixelRect.Overlaps(pageEntries[placedIndex].PixelRect))
                    {
                        throw new InvalidDataException(
                            $"Sprite atlas entries '{entry.SourceResource}' and "
                            + $"'{pageEntries[placedIndex].SourceResource}' overlap on '{entry.PageResource}'.");
                    }
                }

                pageEntries.Add(entry);
                previousSourcePath = entry.SourceResource.RelativePath;
            }
        }

        private static void ValidateResource(
            in GameAssetResourceRef resource,
            GameAssetVerifiedPackage package,
            string role)
        {
            if (!resource.IsDefined)
            {
                throw new InvalidDataException($"Sprite atlas {role} resource is not defined.");
            }
            if (!string.Equals(resource.ModuleId, package.ModuleId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Sprite atlas {role} resource '{resource}' belongs to a different module.");
            }

            _ = GameAssetModulePackageFileUtils.RequireCanonicalRelativePath(resource.RelativePath);
            _ = package.Resolve(resource);
        }
    }
}
