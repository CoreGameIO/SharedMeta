using Orleans;
using SharedMeta.Core.Packets;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// A broadcast to be sent to subscribers.
    /// Carries one canonical <see cref="MetaOperation"/> payload (what was called + what came
    /// out + nested triggers) plus the only routing field that has to live on the outer wrapper:
    /// <see cref="ExcludePlayerId"/>.
    /// <para>
    /// Pre-0.24 this type duplicated <see cref="MetaOperation"/>'s flat fields (ServiceName,
    /// MethodName, ReplayPayload, PatchBytes, ...) and used a parallel
    /// <c>TriggerBroadcasts: List&lt;EntityBroadcast&gt;</c> for nested triggers. The unification
    /// folds both into <see cref="Op"/> with <see cref="MetaOperation.Triggers"/>, so a single
    /// payload object travels through every server-side hop.
    /// </para>
    /// </summary>
    [GenerateSerializer, Immutable]
    public class EntityBroadcast
    {
        /// <summary>Player id to skip when fanning this broadcast out (typically the original
        /// caller — they already received the result via the RPC response).</summary>
        [Id(0)] public string? ExcludePlayerId { get; set; }

        /// <summary>
        /// The canonical operation payload. Carries ServiceName / MethodName / MethodVersion /
        /// Payload (the original arguments), ReplayPayload, PatchBytes, StateBytes,
        /// RandomScrollDelta, NamedRandomScrollDeltas, ServerTimeTicks, ExecutedConfigVersion,
        /// and — for trigger fan-out — nested <see cref="MetaOperation.Triggers"/>.
        /// </summary>
        [Id(1)] public MetaOperation Op { get; set; } = new();
    }
}
