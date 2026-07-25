using System;
using System.IO;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    public class DgrReplayFileReader : IDisposable
    {
        private readonly FileStream _stream;
        private bool _disposed;

        public readonly string Path;
        public readonly DgrReplayScanResult ScanResult;

        public DgrReplayFileHeader Header => ScanResult.Header;

        public DgrReplayFileReader(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "Replay path is required.",
                    nameof(path));
            }

            Path = System.IO.Path.GetFullPath(path);
            ScanResult = DgrReplayFileScanner.Scan(Path);
            if (!ScanResult.IsComplete)
            {
                throw new FormatException(
                    ScanResult.Failure
                    ?? "DGR file has no valid footer.");
            }
            _stream = new FileStream(
                Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }

        public byte[] ReadChunkPayload(int chunkIndex)
        {
            ThrowIfDisposed();
            if (chunkIndex < 0
                || chunkIndex >= ScanResult.Chunks.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(chunkIndex));
            }

            var expected = ScanResult.Chunks[chunkIndex];
            _stream.Position = expected.FileOffset;
            var actual = DgrReplayFormat.ReadChunk(
                _stream,
                Header,
                keepPayload: true,
                out var payload);
            if (!DgrReplayFormat.IndexEntriesEqual(
                    expected,
                    actual))
            {
                throw new FormatException(
                    $"DGR chunk {chunkIndex} no longer matches its validated index.");
            }
            return payload;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _stream.Dispose();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(DgrReplayFileReader));
            }
        }
    }

    public static class DgrReplayRecovery
    {
        public static DgrReplayScanResult RecoverTemporaryFile(
            string finalPath)
        {
            return RecoverTemporaryFile(
                DgrReplayFileLifecycle.GetTemporaryPath(finalPath),
                finalPath);
        }

        public static DgrReplayScanResult RecoverTemporaryFile(
            string temporaryPath,
            string finalPath)
        {
            if (string.IsNullOrWhiteSpace(temporaryPath))
            {
                throw new ArgumentException(
                    "Temporary replay path is required.",
                    nameof(temporaryPath));
            }
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                throw new ArgumentException(
                    "Final replay path is required.",
                    nameof(finalPath));
            }

            var temporaryFullPath =
                System.IO.Path.GetFullPath(temporaryPath);
            var finalFullPath =
                System.IO.Path.GetFullPath(finalPath);
            var scan =
                DgrReplayFileScanner.Scan(temporaryFullPath);

            using (var stream = new FileStream(
                       temporaryFullPath,
                       FileMode.Open,
                       FileAccess.ReadWrite,
                       FileShare.None))
            {
                if (scan.HasValidFooter)
                {
                    stream.SetLength(scan.ValidLength);
                    stream.Flush(true);
                }
                else
                {
                    stream.SetLength(scan.DataLength);
                    stream.Position = scan.DataLength;
                    DgrReplayFormat.WriteFooter(
                        stream,
                        scan.Chunks,
                        scan.DataLength);
                    stream.Flush(true);
                }
            }

            DgrReplayFileLifecycle.PromoteTemporaryFile(
                temporaryFullPath,
                finalFullPath);
            return DgrReplayFileScanner.Scan(finalFullPath);
        }
    }
}
