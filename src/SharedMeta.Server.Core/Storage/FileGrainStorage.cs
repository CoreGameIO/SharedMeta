using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Runtime;
using Orleans.Serialization;
using Orleans.Storage;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Storage
{
    /// <summary>
    /// Simple file-based grain storage provider.
    ///
    /// Serialization is selected by <see cref="FileGrainStorageOptions.UseOrleansSerializer"/>:
    /// when true (default), Orleans <see cref="Serializer"/> is used so any grain state type
    /// with <c>[GenerateSerializer]</c> works — same shape as Azure Tables / Redis / ADO.NET
    /// providers. When false, the framework <see cref="IMetaSerializer"/> (MemoryPack /
    /// MessagePack) is used, which requires the corresponding transport attributes on the
    /// grain state type.
    ///
    /// File layout:
    ///   {RootDirectory}/{stateName}/{sanitizedGrainId}.bin
    /// </summary>
    public class FileGrainStorage : IGrainStorage
    {
        private readonly FileGrainStorageOptions _options;
        private readonly IMetaSerializer? _metaSerializer;
        private readonly Serializer? _orleansSerializer;
        private readonly ILogger<FileGrainStorage> _logger;
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        private static readonly char[] InvalidChars = { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };

        public FileGrainStorage(
            FileGrainStorageOptions options,
            Serializer? orleansSerializer,
            IMetaSerializer? metaSerializer,
            ILogger<FileGrainStorage> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (_options.UseOrleansSerializer)
            {
                _orleansSerializer = orleansSerializer
                    ?? throw new InvalidOperationException(
                        "FileGrainStorage is configured with UseOrleansSerializer=true but no Orleans Serializer is registered.");
            }
            else
            {
                _metaSerializer = metaSerializer
                    ?? throw new InvalidOperationException(
                        "FileGrainStorage is configured with UseOrleansSerializer=false but no IMetaSerializer is registered.");
            }
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
                grainState.State = Deserialize<T>(bytes);
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

                var tempPath = filePath + ".tmp";
                var bytes = Serialize(grainState.State);
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

        private byte[] Serialize<T>(T value)
        {
            return _orleansSerializer is not null
                ? _orleansSerializer.SerializeToArray(value)
                : _metaSerializer!.Pack(value).ToArray();
        }

        private T Deserialize<T>(byte[] bytes)
        {
            return _orleansSerializer is not null
                ? _orleansSerializer.Deserialize<T>(bytes)
                : _metaSerializer!.Unpack<T>(bytes);
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
