using System.Collections.Generic;
using Orleans;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// A broadcast to be sent to subscribers.
    /// Contains both the original arguments (Payload) and the server-side replay context (ReplayPayload).
    /// </summary>
    [GenerateSerializer]
    public class EntityBroadcast
    {
        [Id(0)] public string ServiceName { get; set; } = "";
        [Id(1)] public string MethodName { get; set; } = "";
        /// <summary>Original serialized arguments.</summary>
        [Id(2)] public byte[] Payload { get; set; } = Array.Empty<byte>();
        [Id(3)] public string? ExcludePlayerId { get; set; }
        /// <summary>Server-side replay context for deterministic client execution.</summary>
        [Id(4)] public byte[]? ReplayPayload { get; set; }

        /// <summary>
        /// Triggered operations executed after the main method.
        /// Nested inside the main broadcast to preserve atomicity with the parent operation.
        /// </summary>
        [Id(5)] public List<EntityBroadcast>? TriggerBroadcasts { get; set; }

        /// <summary>
        /// Server time (UTC ticks) from the original RpcCall for deterministic replay.
        /// </summary>
        [Id(6)] public long ServerTimeTicks { get; set; }

        /// <summary>
        /// Delta of optimistic random ScrollId during this call.
        /// Used by client for desync detection after broadcast replay.
        /// </summary>
        [Id(7)] public long RandomScrollDelta { get; set; }

        /// <summary>
        /// Serialized state diff patch for ServerPatch mode.
        /// When present, subscribers apply this instead of replaying.
        /// </summary>
        [Id(8)] public byte[]? PatchBytes { get; set; }

        /// <summary>
        /// Full serialized state for ServerReplace mode.
        /// When present, subscribers replace their entire state with this.
        /// </summary>
        [Id(9)] public byte[]? StateBytes { get; set; }

        /// <summary>
        /// Per-index scroll deltas for named randoms declared via [NamedRandom] on the state.
        /// Positional: index corresponds to attribute declaration order. Null when no named
        /// randoms advanced. Subscribers use these for desync detection and Skip-catchup.
        /// </summary>
        [Id(10)] public long[]? NamedRandomScrollDeltas { get; set; }

        /// <summary>
        /// The <see cref="MetaConfigVersion"/> the server actually executed under (added in 0.21.0).
        /// Subscribers use this to pin <c>Context.Config</c> during broadcast replay, so a
        /// cross-version observer (e.g. patch rolled out mid-session, or <c>[EntityScope(Global)]</c>
        /// entity where the server normalizes to <c>CurrentClientVersion</c>) replays under the
        /// same config the server used. Default <c>default(MetaConfigVersion)</c> means "no config
        /// system" — observers fall back to their session-resolved version.
        /// </summary>
        [Id(11)] public MetaConfigVersion ExecutedConfigVersion { get; set; }
    }
}
