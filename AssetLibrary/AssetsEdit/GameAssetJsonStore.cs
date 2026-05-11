#if NEWTONSOFT_EXISTS
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DingoGameObjectsCMS.AssetObjects;
using DingoGameObjectsCMS.Serialization;
using DingoUnityExtensions.Serialization;
using Newtonsoft.Json.Linq;

namespace DingoGameObjectsCMS.AssetLibrary.AssetsEdit
{
    public static class GameAssetJsonStore
    {
        private static readonly SemaphoreSerializer Serializer = new(GameAssetJson.Settings);

        public static async Task WriteAsync(string jsonPathAbs, GameAssetScriptableObject asset, CancellationToken ct = default)
        {
            if (asset == null)
                throw new ArgumentNullException(nameof(asset));

            await WriteTextAtomicAsync(jsonPathAbs, asset.ToJson(), ct);
        }

        public static async Task<GameAssetScriptableObject> ReadAsync(string jsonPathAbs, CancellationToken ct = default)
        {
            using var cts = CreateCancellationTokenSource(ct);
            var json = await Serializer.LoadDeserializeAsync<JObject>(
                jsonPathAbs,
                catchExceptions: false,
                cancellationTokenSource: cts);
            if (json == null)
                throw new InvalidOperationException($"Asset JSON is empty or invalid: {jsonPathAbs}");

            var asset = GameAssetJson.FromJObject(json);
            if (asset == null)
                throw new InvalidOperationException($"Asset JSON root is not a GameAssetScriptableObject: {jsonPathAbs}");

            return asset;
        }

        public static Task DeleteAsync(string jsonPathAbs, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(jsonPathAbs))
                File.Delete(jsonPathAbs);

            return Task.CompletedTask;
        }

        public static async Task WriteTextAtomicAsync(string path, string text, CancellationToken ct)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException($"Invalid JSON path: {path}"));
            using var cts = CreateCancellationTokenSource(ct);
            await Serializer.SaveAsync(path, text, catchExceptions: false, cancellationTokenSource: cts);
        }

        private static CancellationTokenSource CreateCancellationTokenSource(CancellationToken ct)
        {
            return ct.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        }
    }
}
#endif
