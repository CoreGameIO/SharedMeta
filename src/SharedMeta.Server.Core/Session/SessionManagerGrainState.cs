using System.Collections.Generic;
using MemoryPack;
using MessagePack;
using Orleans;
using SharedMeta.Core.Transport;

namespace SharedMeta.Server.Core.Session
{
    /// <summary>
    /// 0.24.0+ Persistent state for <see cref="SessionManagerGrain"/>.
    /// <para>
    /// Written on <c>OnDeactivateAsync</c> + <c>GracefulDisconnectAsync</c>, restored on
    /// <c>OnActivateAsync</c>. Hot path doesn't write — losses on crash mid-session are
    /// acceptable per design (client recovery flow handles them via <c>SessionUnknown</c>).
    /// </para>
    /// <para>
    /// Persisted only what's needed to honour a Resume from the same client:
    /// <see cref="CurrentSessionId"/>, <see cref="SequenceNumber"/>,
    /// <see cref="SubscribedEntities"/> map (entity id → state type) for transparent
    /// rehydration, and <see cref="PendingPackets"/> so the client's <c>LastAcknowledgedSequence</c>
    /// can still pull missed broadcasts after grain reactivation. <c>_observerManager</c>
    /// is NOT persisted (live refs can't survive); the first request from
    /// <c>MetaConnectionHandler</c> after reactivation re-establishes the observer via
    /// SessionConnect.
    /// </para>
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class SessionManagerGrainState
    {
        /// <summary>True when this state has been populated at least once. Distinguishes
        /// "freshly-activated, never registered" from "registered with empty session".</summary>
        [Id(0), Key(0), MemoryPackOrder(0)] public bool Populated { get; set; }

        /// <summary>The session id that the next Resume must match. <see cref="System.Guid.Empty"/>
        /// when no session is currently bound (after graceful disconnect / first activation).</summary>
        [Id(1), Key(1), MemoryPackOrder(1)] public System.Guid CurrentSessionId { get; set; }

        /// <summary>Highest sequence number issued for this session — restored so Resume's
        /// missed-packet replay starts after the right boundary.</summary>
        [Id(2), Key(2), MemoryPackOrder(2)] public long SequenceNumber { get; set; }

        /// <summary>Entity subscriptions the session held at deactivation time. After
        /// reactivation these are staged in the grain's saved-subscriptions structure so
        /// the first Resume re-attaches the player to every entity without the client
        /// having to walk its in-memory list.</summary>
        [Id(3), Key(3), MemoryPackOrder(3)] public Dictionary<string, PersistedSubscriptionInfo> SubscribedEntities { get; set; } = new();

        /// <summary>Outgoing broadcast/RPC-response buffer that the client may still
        /// need to catch up on (<c>SessionResponse</c> messages with sequence numbers
        /// past the client's <c>LastAcknowledgedSequence</c>). Bounded by the same
        /// retention rule that prunes on ack — pruning happens before deactivation.</summary>
        [Id(4), Key(4), MemoryPackOrder(4)] public List<SharedMeta.Core.Transport.SessionResponse>? PendingPackets { get; set; }

        /// <summary>
        /// 0.24.0+ Highest RPC <c>RequestId</c> the grain had successfully dispatched at
        /// deactivation time. Used by <c>RpcOrderingBuffer.AdvanceLastDispatched</c> on
        /// reactivation so the buffer doesn't reset to zero — without this, the next
        /// client-sent RequestId looks like a gap (NextExpected=1) and gets stashed
        /// instead of processed.
        /// </summary>
        [Id(5), Key(5), MemoryPackOrder(5)] public long LastDispatchedRequestId { get; set; }
    }

    /// <summary>
    /// 0.24.0+ One subscribed entity persisted on the SessionManagerGrain. The grain
    /// uses these on Resume to re-issue Subscribe calls into each <c>EntityGrain</c>
    /// transparently, avoiding the client-driven re-subscribe round-trips that the
    /// Reconnect recovery path runs.
    /// </summary>
    [MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, GenerateSerializer]
    public partial class PersistedSubscriptionInfo
    {
        [Id(0), Key(0), MemoryPackOrder(0)] public string EntityId { get; set; } = "";
        [Id(1), Key(1), MemoryPackOrder(1)] public string StateTypeName { get; set; } = "";
        [Id(2), Key(2), MemoryPackOrder(2)] public string? ClientVersion { get; set; }
        [Id(3), Key(3), MemoryPackOrder(3)] public ulong ClientSignatureHash { get; set; }
    }
}
