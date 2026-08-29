#if UNITY_EDITOR

using DingoGameObjectsCMS.AssetLibrary;
using DingoGameObjectsCMS.Mirror.Protocol;
using NUnit.Framework;

namespace DingoGameObjectsCMS.Tests.Editor
{
    public class RuntimeSessionAssetCatalogTests
    {
        [Test]
        public void FromLock_EmptySealedLock_CreatesEmptyCatalog()
        {
            var assetLock = new GameAssetLibraryLock().Seal();

            var catalog = RuntimeSessionAssetCatalog.FromLock(assetLock);

            Assert.That(catalog.ManifestEntries, Is.Empty);
        }
    }
}

#endif
