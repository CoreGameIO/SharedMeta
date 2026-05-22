namespace SharedMeta.Server.Core.Memory
{
    /// <summary>
    /// DI-configurable toggles for <see cref="PooledPayloadRegistry"/>. Hosts register via
    /// <c>services.Configure&lt;PooledPayloadOptions&gt;(o =&gt; { ... })</c>; the registry
    /// reads them on construction.
    /// <para>
    /// Defaults are intentionally conservative (both off) so a host that registers a
    /// registry without configuring options stays on the byte[]+GC path with no diagnostic
    /// overhead — pool and history are opt-in features.
    /// </para>
    /// </summary>
    public sealed class PooledPayloadOptions
    {
        /// <summary>
        /// When <c>true</c>, <c>MetaProviderBase.PackBroadcastVariant</c> routes outgoing
        /// wire payloads through this registry (slot rent + ref-counted fan-out). When
        /// <c>false</c>, falls back to a fresh <c>byte[]</c> per call (<see cref="PooledPayload.Ref"/>=0,
        /// GC-managed) — useful to A/B measure the pool's contribution to allocation
        /// pressure under a real workload, or as a temporary kill-switch.
        /// </summary>
        public bool UsePoolPath { get; set; } = false;

        /// <summary>
        /// Diagnostic toggle: when <c>true</c>, the registry records a per-slot lifecycle
        /// trail (Acquire / IncrementRef / Release call stacks + timestamps + thread ids).
        /// Dumped to the throw message on double-release / use-after-free, and surfaced via
        /// <c>PooledPayloadRegistry.DumpAllocatedSlots(stacks: true)</c> for leak hunts.
        /// Adds significant per-op overhead — opt-in for debugging, not for production.
        /// </summary>
        public bool EnableHistory { get; set; } = false;
    }
}
