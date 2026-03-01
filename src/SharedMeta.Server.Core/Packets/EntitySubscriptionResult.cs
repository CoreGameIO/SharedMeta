using Orleans;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// Result of subscribing to an entity.
    /// </summary>
    [GenerateSerializer]
    public class EntitySubscriptionResult
    {
        [Id(0)] public bool Success { get; set; }
        [Id(1)] public string? Error { get; set; }
        [Id(2)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();
        [Id(3)] public long EntitySequenceNumber { get; set; }
        [Id(4)] public byte[]? OptimisticRandomBytes { get; set; }
    }
}
