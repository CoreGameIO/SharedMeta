using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response containing the config download URL for an entity.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ConfigDownloadUrlResponse
    {
        [Id(0), Key(0)] public bool Success { get; set; }
        [Id(1), Key(1)] public string? Error { get; set; }
        [Id(2), Key(2)] public string? DownloadUrl { get; set; }
    }
}
