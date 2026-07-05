using Orleans;
using MemoryPack;
using MessagePack;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// Response to <see cref="ResolveStatelessConfigVersionRequest"/>.
    /// </summary>
    [MemoryPackable, MessagePackObject, GenerateSerializer]
    public partial class ResolveStatelessConfigVersionResponse
    {
        [Id(0), Key(0)] public bool Success { get; set; }
        [Id(1), Key(1)] public string? Error { get; set; }

        /// <summary>
        /// True when a <c>[StatelessMetaService]</c> registered for the requested config type
        /// name was found server-side. False means no such service is declared in the server's
        /// assembly — distinct from a transport/DI failure (<see cref="Success"/> = false).
        /// </summary>
        [Id(2), Key(2)] public bool Found { get; set; }

        [Id(3), Key(3)] public int Major { get; set; }
        [Id(4), Key(4)] public int Minor { get; set; }
        [Id(5), Key(5)] public int Patch { get; set; }
    }
}
