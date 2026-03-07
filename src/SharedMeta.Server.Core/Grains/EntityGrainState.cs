using MemoryPack;
using MessagePack;
using Orleans;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Grains;

/// <summary>
/// Wrapper state for EntityGrain persistence.
/// Holds user state, entity sequence number, and subscriber information.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
public partial class EntityGrainState<TState> where TState : class, ISharedState, new()
{
    [Id(0), Key(0), MemoryPackOrder(0)] public TState UserState { get; set; } = new();
    [Id(1), Key(1), MemoryPackOrder(1)] public long EntitySequenceNumber { get; set; }
    [Id(2), Key(2), MemoryPackOrder(2)] public Dictionary<string, PersistedSubscriberInfo> Subscribers { get; set; } = new();
    [Id(3), Key(3), MemoryPackOrder(3)] public byte[]? ServerRandomBytes { get; set; }
    [Id(4), Key(4), MemoryPackOrder(4)] public byte[]? OptimisticRandomBytes { get; set; }
    [Id(5), Key(5), MemoryPackOrder(5)] public int Version { get; set; }

    /// <summary>
    /// Persisted config version for this entity. Default (0,0) = not yet resolved.
    /// Once set, the entity uses this version until explicitly upgraded.
    /// </summary>
    [Id(6), Key(6), MemoryPackOrder(6)] public MetaConfigVersion ConfigVersion { get; set; }
}

/// <summary>
/// Persisted subscriber information.
/// Grain references are reconstructed from PlayerId on activation.
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
public partial class PersistedSubscriberInfo
{
    [Id(0), Key(0), MemoryPackOrder(0)] public string PlayerId { get; set; } = "";
    [Id(1), Key(1), MemoryPackOrder(1)] public DateTime LastActiveUtc { get; set; }
}
