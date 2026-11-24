using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Optional companion to <see cref="IMetaServerObserver"/> for observers that only sometimes
    /// want per-call notifications. Without it, any registered observer makes the transport
    /// build a <see cref="MetaCallInfo"/> for every client call; with it, that work is skipped
    /// while <see cref="ObservesCalls"/> is false. Session notifications are unaffected.
    /// </summary>
    public interface IMetaCallObservationGate
    {
        /// <summary>Read on every client call — keep it a field read.</summary>
        bool ObservesCalls { get; }
    }
}
