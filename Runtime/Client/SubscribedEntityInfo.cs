using System;
using System.Collections.Generic;
using SharedMeta.Core;

namespace SharedMeta.Client
{
    /// <summary>
    /// Read-only snapshot of one subscribed entity on a <see cref="MetaServiceResolver"/>.
    /// Returned by <see cref="MetaServiceResolver.GetSubscribedEntities"/> and
    /// <see cref="MetaClient.GetSubscribedEntities"/> for debug inspection, status panels,
    /// and troubleshooting (which entities is this client tracking, which config branches
    /// got pinned, which services are wired locally). 0.20.3.
    ///
    /// <para><b>Debug-only intent.</b> This snapshot is taken at call time and is not kept
    /// live — mutations to the entity after this call do not propagate to the record.
    /// Branching production logic on these fields is fragile; the snapshot exists so you
    /// can <c>Debug.Log</c> / inspect the state, not to drive runtime decisions. Use
    /// <see cref="MetaClient.GetState{TState}"/> + <c>OnStateMutated</c> events for live
    /// state. The <see cref="State"/> reference IS the live container's object, so reading
    /// it on the same thread is fine; just don't cache the snapshot itself.</para>
    /// </summary>
    public sealed record SubscribedEntityInfo
    {
        /// <summary>Entity id passed to <c>GetServiceAsync</c> / <c>SubscribeAsync</c>.</summary>
        public string EntityId { get; init; } = "";

        /// <summary>Type of the entity's <see cref="ISharedState"/>.</summary>
        public Type StateType { get; init; } = null!;

        /// <summary>
        /// <c>[MetaConfig]</c> class type pinned for this entity, or null if the service is
        /// declared without a config (no <c>ConfigType</c> on <c>[MetaService]</c> and no
        /// <c>DefaultConfig = true</c>).
        /// </summary>
        public Type? ConfigType { get; init; }

        /// <summary>
        /// Config version branch that was resolved for this client at subscribe time —
        /// driven by <c>[MetaConfigVersion]</c> rules on the config class and the client's
        /// app version. <see cref="MetaConfigVersion.Major"/> 0 / <see cref="MetaConfigVersion.Minor"/>
        /// 0 means "no config or unversioned config".
        /// </summary>
        public MetaConfigVersion ConfigVersion { get; init; }

        /// <summary>
        /// Service names (the <c>[MetaService]</c> interface name without leading "I" hasn't
        /// been stripped — values like <c>"IProfileService"</c>) for which a typed
        /// <c>*ApiClient</c> has been registered locally on this entity. Broadcasts for
        /// services in this list short-circuit to the typed ApiClient's own dispatch path;
        /// services NOT in this list (hosted on the same entity but not subscribed locally)
        /// still receive state updates through <c>EntityReplayDispatcher</c>.
        /// </summary>
        public IReadOnlyList<string> ServiceNames { get; init; } = System.Array.Empty<string>();

        /// <summary>
        /// Live reference to the entity's state — same object that <c>MetaClient.GetState&lt;T&gt;</c>
        /// returns. Safe to inspect on the dispatcher thread. Cast to <see cref="StateType"/>.
        /// </summary>
        public object State { get; init; } = null!;

        /// <summary>
        /// Resolved config object pinned for this entity, or null when no config applies.
        /// Same instance that's exposed via <c>Context.Config</c> for this entity's calls.
        /// </summary>
        public object? Config { get; init; }
    }
}
