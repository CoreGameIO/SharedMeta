using System;
using Orleans;

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
        /// either replay or patch variant). Backed by a GC byte[] — the class-level
        /// <c>[Immutable]</c> marker tells Orleans to share by reference on in-silo hops,
        /// so the same byte[] is fanned out to N subscribers without copying.</summary>
        [Id(1)] public ReadOnlyMemory<byte> OpBytes { get; set; }

        /// <summary>Executed method identifier</summary>
        [Id(2)] public ushort MethodId { get; set; }
    }
}
