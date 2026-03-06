using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to get the download URL for an entity's config.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ConfigDownloadUrlRequest
    {
        [Id(0), Key(0)] public string StateTypeName { get; set; } = "";
        [Id(1), Key(1)] public int ConfigMajorVersion { get; set; }
        [Id(2), Key(2)] public int ConfigMinorVersion { get; set; }
    }
}
