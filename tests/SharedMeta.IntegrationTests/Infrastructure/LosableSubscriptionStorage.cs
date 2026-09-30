using System.Collections.Concurrent;
using Orleans.Runtime;
using Orleans.Storage;

namespace SharedMeta.IntegrationTests.Infrastructure;

/// <summary>
/// Test stand-in for a subscriber store that can lose data (a flushed Redis). Delegates to the
/// silo's "Default" provider — the same resolution the production fallback alias uses — and, for
/// a grain marked with <see cref="Lose"/>, answers its next read as if the record never existed.
/// </summary>
public sealed class LosableSubscriptionStorage : IGrainStorage
{
    private static readonly ConcurrentDictionary<GrainId, bool> Lost = new();
    private readonly IGrainStorage _inner;

    public LosableSubscriptionStorage(IGrainStorage inner) => _inner = inner;

    /// <summary>The grain's next read of its subscriber record comes back empty.</summary>
    public static void Lose(GrainId grainId) => Lost[grainId] = true;

    public async Task ReadStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
    {
        if (!Lost.TryRemove(grainId, out _))
        {
            await _inner.ReadStateAsync(stateName, grainId, grainState);
            return;
        }

        // Drop the stored record for real, so the grain's later writes don't hit an ETag conflict
        // against data it was never shown.
        var existing = new GrainState<T>();
        await _inner.ReadStateAsync(stateName, grainId, existing);
        if (existing.RecordExists)
            await _inner.ClearStateAsync(stateName, grainId, existing);

        grainState.State = Activator.CreateInstance<T>();
        grainState.ETag = existing.ETag;
        grainState.RecordExists = false;
    }

    public Task WriteStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => _inner.WriteStateAsync(stateName, grainId, grainState);

    public Task ClearStateAsync<T>(string stateName, GrainId grainId, IGrainState<T> grainState)
        => _inner.ClearStateAsync(stateName, grainId, grainState);
}
