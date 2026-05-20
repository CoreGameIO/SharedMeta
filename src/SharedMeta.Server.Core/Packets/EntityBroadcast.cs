using System.Collections.Immutable;
using Orleans;
using SharedMeta.Core.Packets;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// A broadcast to be sent to a specific subscriber session. EntityGrain selects the
    /// appropriate per-subscriber variant (replay or patch) BEFORE wrapping into
    /// <see cref="EntityBroadcast"/> — SessionManagerGrain is per-player and only orders &amp;
    /// forwards, so the bytes carried here are already the final shape for THIS subscriber.
    /// </summary>
    [GenerateSerializer, Immutable]
    public class EntityBroadcast
    {
        /// <summary>Player id to skip when fanning this broadcast out (typically the original
        /// caller — they already received the result via the RPC response).</summary>
        [Id(0)] public string? ExcludePlayerId { get; set; }

        /// <summary>Pre-serialized MetaOperation for this subscriber (already tailored to
        /// either replay or patch variant based on the caller's force-patch capabilities).</summary>
        [Id(1)] public ImmutableArray<byte> OpBytes { get; set; }

        /// <summary>Executed method identifier</summary>
        [Id(2)] public ushort MethodId { get; set; }
    }
}
