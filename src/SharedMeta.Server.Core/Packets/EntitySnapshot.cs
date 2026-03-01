using Orleans;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Snapshot of entity state.
    /// </summary>
    [GenerateSerializer]
    public class EntitySnapshot
    {
        [Id(0)] public byte[] StateBytes { get; set; } = Array.Empty<byte>();
        [Id(1)] public long CurrentSequenceNumber { get; set; }
        [Id(2)] public byte[]? OptimisticRandomBytes { get; set; }
    }
}
