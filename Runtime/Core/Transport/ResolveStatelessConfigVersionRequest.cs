using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Request to resolve the <see cref="MetaConfigVersion"/> a <c>[StatelessMetaService]</c>'s
    /// linked config should use for this client — the non-entity counterpart of
    /// <see cref="ConfigDownloadUrlRequest"/>. No entity/state is involved; resolution is keyed
    /// purely by the config type name and the caller's app version.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ResolveStatelessConfigVersionRequest
    {
        [Id(0), Key(0)] public string ConfigTypeName { get; set; } = "";
        [Id(1), Key(1)] public string ClientAppVersion { get; set; } = "";
    }
}
