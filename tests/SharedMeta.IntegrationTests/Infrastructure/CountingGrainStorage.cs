using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Storage;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Pass-through over the silo's memory store that counts writes per grain, so a test can tell
/// whether a call wrote the entity's state.
/// </summary>
public sealed class CountingGrainStorage : IGrainStorage
{
    private static readonly ConcurrentDictionary<(GrainId, string), int> Writes = new();
    private readonly IGrainStorage _inner;

    public CountingGrainStorage(IGrainStorage inner) => _inner = inner;

    public static int WriteCount(GrainId grainId, string stateName)
        => Writes.TryGetValue((grainId, stateName), out var count) ? count : 0;

    public Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => _inner.ReadStateAsync(stateName, grainId, grainState);

    public async Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        await _inner.WriteStateAsync(stateName, grainId, grainState);
        Writes.AddOrUpdate((grainId, stateName), 1, (_, count) => count + 1);
    }

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => _inner.ClearStateAsync(stateName, grainId, grainState);
}
