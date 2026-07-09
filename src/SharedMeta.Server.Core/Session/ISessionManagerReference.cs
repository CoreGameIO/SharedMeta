using Orleans.Concurrency;
using SharedMeta.Core;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core.Session;

/// <summary>
/// Reference to SessionManager for callbacks.
/// Used by EntityGrain to send broadcasts.
/// </summary>
public interface ISessionManagerReference : IGrainWithStringKey
{
    /// <summary>
    /// [OneWay, AlwaysInterleave] Receive a broadcast from an entity.
    /// AlwaysInterleave allows this to execute during an active RPC await,
    /// so broadcasts can be queued while SendToEntityAsync is in progress.
    /// </summary>
    [OneWay]
    [AlwaysInterleave]
    Task ReceiveBroadcastAsync(string entityId, string stateTypeName, EntityBroadcast broadcast, long entitySequenceNumber);

    /// <summary>
    /// [OneWay] Notify that an entity is deactivating.
    /// </summary>
    [OneWay]
    Task NotifyEntityDeactivatingAsync(string entityId, string stateTypeName);
}