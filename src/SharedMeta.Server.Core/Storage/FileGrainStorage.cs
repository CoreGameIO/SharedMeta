using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Storage;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Storage
{
    /// <summary>
    /// Simple file-based grain storage provider.
    /// Uses IMetaSerializer (MessagePack/MemoryPack) for serialization.
    ///
    /// File layout:
    ///   {RootDirectory}/{stateName}/{sanitizedGrainId}.bin
    /// </summary>
    public class FileGrainStorage : IGrainStorage
    {
        private readonly FileGrainStorageOptions _options;
        private readonly IMetaSerializer _serializer;
        private readonly ILogger<FileGrainStorage> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        private static readonly char[] InvalidChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        public FileGrainStorage(FileGrainStorageOptions options, IMetaSerializer serializer, ILogger<FileGrainStorage> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var filePath = GetFilePath(stateName, grainId);
            var semaphore = GetLock(filePath);
            await semaphore.WaitAsync();
            try
            {
                if (!File.Exists(filePath))
                {
                    grainState.RecordExists = false;
                    grainState.ETag = null;
                    return;
                }

                var bytes = await File.ReadAllBytesAsync(filePath);
                grainState.State = _serializer.Unpack<T>(bytes);
                grainState.RecordExists = true;
                grainState.ETag = File.GetLastWriteTimeUtc(filePath).Ticks.ToString();

                _logger.LogDebug("Read grain state {StateName}/{GrainId} from {Path}",
                    stateName, grainId, filePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read grain state {StateName}/{GrainId} from {Path}",
                    stateName, grainId, filePath);
                throw;
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var filePath = GetFilePath(stateName, grainId);
            var semaphore = GetLock(filePath);
            await semaphore.WaitAsync();
            try
            {
                // ETag concurrency check
                if (grainState.ETag != null && File.Exists(filePath))
                {
                    var currentETag = File.GetLastWriteTimeUtc(filePath).Ticks.ToString();
                    if (currentETag != grainState.ETag)
                    {
                        throw new InconsistentStateException(
                            $"ETag mismatch for {stateName}/{grainId}: stored={currentETag}, expected={grainState.ETag}",
                            grainState.ETag,
                            currentETag);
                    }
                }

                var dir = Path.GetDirectoryName(filePath)!;
                Directory.CreateDirectory(dir);

                // Write to temp file, then atomic move
                var tempPath = filePath + ".tmp";
                var bytes = _serializer.Pack(grainState.State);
                await File.WriteAllBytesAsync(tempPath, bytes);
                File.Move(tempPath, filePath, overwrite: true);

                grainState.RecordExists = true;
                grainState.ETag = File.GetLastWriteTimeUtc(filePath).Ticks.ToString();

                _logger.LogDebug("Wrote grain state {StateName}/{GrainId} to {Path}",
                    stateName, grainId, filePath);
            }
            catch (InconsistentStateException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to write grain state {StateName}/{GrainId} to {Path}",
                    stateName, grainId, filePath);
                throw;
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        {
            var filePath = GetFilePath(stateName, grainId);
            var semaphore = GetLock(filePath);
            await semaphore.WaitAsync();
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.LogDebug("Cleared grain state {StateName}/{GrainId} at {Path}",
                        stateName, grainId, filePath);
                }

                grainState.RecordExists = false;
                grainState.ETag = null;
            }
            finally
            {
                semaphore.Release();
            }
        }

        private string GetFilePath(string stateName, GrainId grainId)
        {
            var sanitizedId = SanitizeForPath(grainId.ToString());
            return Path.Combine(_options.RootDirectory, stateName, sanitizedId + ".bin");
        }

        private static string SanitizeForPath(string input)
        {
            var result = input;
            foreach (var c in InvalidChars)
            {
                result = result.Replace(c, '+');
            }
            return result;
        }

        private SemaphoreSlim GetLock(string filePath)
        {
            return _locks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
        }
    }
}
