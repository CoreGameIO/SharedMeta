using Orleans;
using SharedMeta.Core.Memory;
using SharedMeta.Core.Packets;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// A broadcast to be sent to a specific subscriber session. EntityGrain selects the
    /// appropriate per-subscriber variant (replay or patch) BEFORE wrapping into
    /// <see cref="EntityBroadcast"/> — SessionManagerGrain is per-player and only orders &amp;
    /// forwards, so the bytes carried here are already the final shape for THIS subscriber.
    /// </summary>
    [GenerateSerializer]
    public class EntityBroadcast
    {
        /// <summary>Player id to skip when fanning this broadcast out (typically the original
        /// caller — they already received the result via the RPC response).</summary>
        [Id(0)] public string? ExcludePlayerId { get; set; }

        /// <summary>Pre-serialized MetaOperation for this subscriber (already tailored to
        /// either replay or patch variant) as a pool-rented buffer. The producing EntityGrain
        /// fans the same payload out to N subscribers with <c>IncrementRef(N-1)</c> so the
        /// effective ref-count equals N; each receiving SessionManagerGrain releases its share
        /// when the broadcast is delivered / evicted, returning the buffer to the pool exactly
        /// once across all subscribers.</summary>
        [Id(1)] public PooledPayload OpBytes { get; set; }

        /// <summary>Executed method identifier</summary>
        [Id(2)] public ushort MethodId { get; set; }
    }
}
