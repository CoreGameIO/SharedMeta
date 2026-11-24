namespace SharedMeta.Inspector;

/// <summary>
/// Events of the whole cluster, as published by every silo's <see cref="IMetaEventSink"/>. Once a
/// source is attached, the store is fed from it alone — a silo's own events come back through it
/// too — so every inspector sees the same timeline whichever silo ran the call.
/// </summary>
public interface IMetaEventSource
{
    /// <summary>Delivers batches until cancelled. Order within one entity is the publish order.</summary>
    Task RunAsync(Func<IReadOnlyList<MetaInspectorEvent>, ValueTask> deliver, CancellationToken cancellationToken);
}

/// <summary>
/// Durable per-entity history kept by the broker beyond the in-memory window (and across restarts).
/// </summary>
public interface IMetaEventHistory
{
    /// <summary>
    /// Events of one entity in publish order, at most <paramref name="maxEvents"/> (the oldest are
    /// dropped first). <c>Truncated</c> tells that older events exist beyond the cap.
    /// </summary>
    Task<(IReadOnlyList<MetaInspectorEvent> Events, bool Truncated)> ReadEntityAsync(
        string stateType, string entityId, int maxEvents, CancellationToken cancellationToken);
}

/// <summary>
/// Capture settings shared by every silo and inspector of a cluster: a change made through any of
/// them applies everywhere.
/// </summary>
public interface IInspectorControlChannel
{
    /// <summary>Applies <paramref name="change"/> to the current shared settings atomically; returns the result.</summary>
    Task<InspectorCaptureSettings> UpdateAsync(Action<InspectorCaptureSettings> change, CancellationToken cancellationToken);

    /// <summary>Calls <paramref name="apply"/> with the current settings, then with every change, until cancelled.</summary>
    Task WatchAsync(Action<InspectorCaptureSettings> apply, CancellationToken cancellationToken);
}
