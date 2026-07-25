using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace DingoGameObjectsCMS.RuntimeObjects.Replay
{
    class DgrReplayPendingChunk
    {
        public readonly ulong Sequence;
        public readonly int TrackIndex;
        public readonly long StartTick;
        public readonly long EndTick;
        public readonly ulong Cursor;
        public readonly byte[] Payload;

        public DgrReplayPendingChunk(
            ulong sequence,
            int trackIndex,
            long startTick,
            long endTick,
            ulong cursor,
            byte[] payload)
        {
            Sequence = sequence;
            TrackIndex = trackIndex;
            StartTick = startTick;
            EndTick = endTick;
            Cursor = cursor;
            Payload = payload;
        }
    }

    public static class DgrReplayFileLifecycle
    {
        public static string GetTemporaryPath(string finalPath)
        {
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                throw new ArgumentException(
                    "Final replay path is required.",
                    nameof(finalPath));
            }
            return Path.GetFullPath(finalPath) + ".tmp";
        }

        public static void PromoteTemporaryFile(
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
                Path.GetFullPath(temporaryPath);
            var finalFullPath = Path.GetFullPath(finalPath);
            if (string.Equals(
                    temporaryFullPath,
                    finalFullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Temporary and final replay paths must differ.");
            }
            if (!string.Equals(
                    Path.GetPathRoot(temporaryFullPath),
                    Path.GetPathRoot(finalFullPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Temporary and final replay files must be on the same volume.");
            }
            if (!File.Exists(temporaryFullPath))
            {
                throw new FileNotFoundException(
                    "Temporary replay file does not exist.",
                    temporaryFullPath);
            }

            if (File.Exists(finalFullPath))
            {
                File.Replace(
                    temporaryFullPath,
                    finalFullPath,
                    null);
            }
            else
            {
                File.Move(temporaryFullPath, finalFullPath);
            }
        }
    }

    public class DgrReplayFileWriter : IDisposable
    {
        public const int MAX_QUEUED_BYTES = 16 * 1024 * 1024;
        public const int MAX_QUEUED_CHUNKS = 4096;

        private readonly object _gate = new();
        private readonly Queue<DgrReplayPendingChunk> _queue = new();
        private readonly List<DgrReplayChunkIndexEntry> _index = new();
        private readonly Dictionary<string, int> _trackIndices =
            new(StringComparer.Ordinal);
        private readonly FileStream _stream;
        private readonly Thread _worker;

        private int _queuedBytes;
        private int _queuedChunks;
        private ulong _nextSequence;
        private bool _finishing;
        private bool _disposed;
        private bool _completed;
        private Exception _workerFailure;
        private long _dataLength;

        public readonly string FinalPath;
        public readonly string TemporaryPath;
        public readonly DgrReplayFileHeader Header;

        public bool IsCompleted => _completed;

        public int QueuedBytes
        {
            get
            {
                lock (_gate)
                {
                    return _queuedBytes;
                }
            }
        }

        public DgrReplayFileWriter(
            string finalPath,
            DgrReplayFileHeader header)
        {
            if (string.IsNullOrWhiteSpace(finalPath))
            {
                throw new ArgumentException(
                    "Final replay path is required.",
                    nameof(finalPath));
            }
            Header = header
                     ?? throw new ArgumentNullException(nameof(header));

            FinalPath = Path.GetFullPath(finalPath);
            TemporaryPath =
                DgrReplayFileLifecycle.GetTemporaryPath(FinalPath);
            var directory = Path.GetDirectoryName(FinalPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException(
                    "Final replay path has no parent directory.");
            }
            Directory.CreateDirectory(directory);

            for (var i = 0; i < Header.Tracks.Count; i++)
            {
                _trackIndices.Add(
                    Header.Tracks[i].TrackId,
                    i);
            }

            _stream = new FileStream(
                TemporaryPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            try
            {
                _dataLength =
                    DgrReplayFormat.WriteHeader(_stream, Header);
                _stream.Flush(true);
                _worker = new Thread(WorkerLoop)
                {
                    IsBackground = true,
                    Name = "DGR Deflate Writer",
                };
                _worker.Start();
            }
            catch
            {
                _stream.Dispose();
                throw;
            }
        }

        public ulong EnqueueChunk(
            string trackId,
            long completedTick,
            ulong cursor,
            byte[] payload)
        {
            return EnqueueChunk(
                trackId,
                completedTick,
                completedTick,
                cursor,
                payload);
        }

        public ulong EnqueueChunk(
            string trackId,
            long startTick,
            long endTick,
            ulong cursor,
            byte[] payload)
        {
            RuntimeReplayId.Validate(trackId, nameof(trackId));
            if (!_trackIndices.TryGetValue(
                    trackId,
                    out var trackIndex))
            {
                throw new InvalidOperationException(
                    $"Replay track '{trackId}' is not declared in the DGR header.");
            }
            return EnqueueChunk(
                trackIndex,
                startTick,
                endTick,
                cursor,
                payload);
        }

        public ulong EnqueueChunk(
            int trackIndex,
            long completedTick,
            ulong cursor,
            byte[] payload)
        {
            return EnqueueChunk(
                trackIndex,
                completedTick,
                completedTick,
                cursor,
                payload);
        }

        public ulong EnqueueChunk(
            int trackIndex,
            long startTick,
            long endTick,
            ulong cursor,
            byte[] payload)
        {
            if (trackIndex < 0
                || trackIndex >= Header.Tracks.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(trackIndex));
            }
            if (startTick < -1 || endTick < -1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(startTick));
            }
            if ((startTick == -1) != (endTick == -1)
                || startTick > endTick)
            {
                throw new ArgumentException(
                    $"Replay tick range {startTick}..{endTick} is invalid.");
            }
            if (payload == null)
            {
                throw new ArgumentNullException(nameof(payload));
            }
            if (payload.Length > MAX_QUEUED_BYTES)
            {
                throw new DgrReplayQueueOverflowException(
                    $"A DGR chunk cannot exceed the {MAX_QUEUED_BYTES}-byte queue budget.");
            }

            lock (_gate)
            {
                ThrowIfUnavailable();
                if (_queuedBytes + payload.Length
                    > MAX_QUEUED_BYTES
                    || _queuedChunks >= MAX_QUEUED_CHUNKS)
                {
                    throw new DgrReplayQueueOverflowException(
                        $"DGR background queue is full "
                        + $"({_queuedBytes}/{MAX_QUEUED_BYTES} bytes, "
                        + $"{_queuedChunks}/{MAX_QUEUED_CHUNKS} chunks).");
                }
                if (_nextSequence
                    >= (ulong)DgrReplayFormat.MAX_CHUNKS)
                {
                    throw new InvalidOperationException(
                        $"A DGR file cannot contain more than {DgrReplayFormat.MAX_CHUNKS} chunks.");
                }

                var sequence = _nextSequence;
                _nextSequence++;
                var ownedPayload = (byte[])payload.Clone();
                _queue.Enqueue(
                    new DgrReplayPendingChunk(
                        sequence,
                        trackIndex,
                        startTick,
                        endTick,
                        cursor,
                        ownedPayload));
                _queuedBytes += ownedPayload.Length;
                _queuedChunks++;
                Monitor.PulseAll(_gate);
                return sequence;
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                ThrowIfUnavailable();
                _finishing = true;
                Monitor.PulseAll(_gate);
            }

            _worker.Join();
            try
            {
                ThrowWorkerFailure();
                _stream.Position = _dataLength;
                DgrReplayFormat.WriteFooter(
                    _stream,
                    _index,
                    _dataLength);
                _stream.Flush(true);
                _stream.Dispose();
                DgrReplayFileLifecycle.PromoteTemporaryFile(
                    TemporaryPath,
                    FinalPath);
                _completed = true;
                _disposed = true;
            }
            catch
            {
                _stream.Dispose();
                _disposed = true;
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                _finishing = true;
                Monitor.PulseAll(_gate);
            }
            _worker.Join();
            try
            {
                _stream.Flush(true);
            }
            finally
            {
                _stream.Dispose();
                _disposed = true;
            }
        }

        private void WorkerLoop()
        {
            while (true)
            {
                DgrReplayPendingChunk pending;
                lock (_gate)
                {
                    while (_queue.Count == 0
                           && !_finishing)
                    {
                        Monitor.Wait(_gate);
                    }
                    if (_queue.Count == 0)
                    {
                        return;
                    }
                    pending = _queue.Dequeue();
                }

                try
                {
                    var compressed =
                        DgrReplayFormat.EncodeDeflate(
                            pending.Payload);
                    var payloadHash =
                        RuntimeReplayHash.CalculateSha256(
                            pending.Payload);
                    var entry = DgrReplayFormat.WriteChunk(
                        _stream,
                        pending.Sequence,
                        pending.TrackIndex,
                        pending.StartTick,
                        pending.EndTick,
                        pending.Cursor,
                        DgrReplayCompression.Deflate,
                        pending.Payload.Length,
                        compressed,
                        payloadHash);
                    _index.Add(entry);
                    _dataLength = _stream.Position;
                }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        _workerFailure = exception;
                        _finishing = true;
                        _queue.Clear();
                        _queuedBytes = 0;
                        _queuedChunks = 0;
                        Monitor.PulseAll(_gate);
                    }
                    return;
                }

                lock (_gate)
                {
                    _queuedBytes -= pending.Payload.Length;
                    _queuedChunks--;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        private void ThrowIfUnavailable()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(
                    nameof(DgrReplayFileWriter));
            }
            ThrowWorkerFailure();
            if (_finishing)
            {
                throw new InvalidOperationException(
                    "DGR writer is already completing.");
            }
        }

        private void ThrowWorkerFailure()
        {
            if (_workerFailure != null)
            {
                throw new InvalidOperationException(
                    "The background DGR writer failed.",
                    _workerFailure);
            }
        }
    }
}
