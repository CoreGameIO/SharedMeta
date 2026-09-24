using System;
using System.Collections.Generic;

namespace SharedMeta.Core
{
    /// <summary>
    /// Result of dispatching a service method call.
    /// Contains the method result and list of triggers to execute.
    /// </summary>
    public struct DispatchResult
    {
        /// <summary>How many cached <see cref="Int"/> values the cache pre-populates.
        /// Covers the common "small return" range — slot/index/counter values up to 31.</summary>
        public const int MaxCached = 32;

        /// <summary>Void / no-result. <see cref="ResultBytes"/> stays empty.</summary>
        public static DispatchResult Void = new DispatchResult();

        /// <summary>Cached "true" return — populated by <see cref="InitializeCache"/>.</summary>
        public static DispatchResult True;

        /// <summary>Cached "false" return — populated by <see cref="InitializeCache"/>.</summary>
        public static DispatchResult False;

        /// <summary>Cached int returns for values in <c>[0, MaxCached)</c>. Out-of-range
        /// values fall back to <c>serializer.Pack(i)</c> in generated dispatchers.</summary>
        public static DispatchResult[] Int = new DispatchResult[MaxCached];

        /// <summary>
        /// Populate the cached primitive-return tables (<see cref="True"/> / <see cref="False"/> /
        /// <see cref="Int"/>) using the supplied codec. Host calls this ONCE at server bootstrap
        /// (after picking <see cref="IMetaSerializer"/>) — generated dispatchers then return
        /// the cached <c>DispatchResult</c> instances directly for primitive method returns,
        /// skipping the per-call <c>Pack(value)</c> + scratch write.
        /// <para>
        /// Idempotent: subsequent calls with the same codec are no-ops; calling with a
        /// different codec rebuilds the cache (host shouldn't switch codecs at runtime, but
        /// the rebuild is safe — cached <see cref="ReadOnlyMemory{T}"/> targets owned byte[]s).
        /// </para>
        /// </summary>
        public static void InitializeCache(IMetaSerializer codec)
        {
            if (codec == null) throw new ArgumentNullException(nameof(codec));
            True  = new DispatchResult { ResultBytes = codec.Pack(true).ToArray() };
            False = new DispatchResult { ResultBytes = codec.Pack(false).ToArray() };
            for (int i = 0; i < Int.Length; i++)
                Int[i] = new DispatchResult { ResultBytes = codec.Pack(i).ToArray() };
        }


        /// <summary>
        /// Serialized result bytes from the method call (default for void methods — check
        /// <see cref="ReadOnlyMemory{T}.IsEmpty"/>). On the server path the dispatcher writes
        /// these via <c>Context.Serializer.Pack&lt;T&gt;(T)</c> — i.e. <c>GrainScopedSerializer</c> —
        /// so the ROM is a slice over the per-grain scratch buffer and is valid only until the
        /// next Handle*Async entry resets the pool.
        /// </summary>
        public ReadOnlyMemory<byte> ResultBytes { get; set; }

        /// <summary>
        /// 0.24.0+ Client-local method ids (from <c>GameMethodIds</c>) of triggers to execute
        /// after this method. The generated dispatcher emits the id constant directly per
        /// trigger so the provider's trigger loop dispatches via the same <c>switch (methodId)</c>
        /// jump table as the main call — no name-to-id resolution at runtime.
        /// Conditions have already been evaluated; only methods that should fire are included.
        /// </summary>
        public List<ushort>? TriggersToExecute { get; set; }

        /// <summary>
        /// If true, EntityGrain must persist state immediately after this call,
        /// regardless of the configured PersistencePolicy.
        /// Set by the generated dispatcher when [MetaMethod(ForcePersist = true)].
        /// </summary>
        public bool ForcePersist { get; set; }

        /// <summary>
        /// 0.26.6+ Optional <see cref="PayloadDebug"/> populated by the generated dispatcher
        /// when a method annotated <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c>
        /// detected a CRC mismatch against the client's <see cref="PayloadDebug.PreStateCrc"/> /
        /// <see cref="PayloadDebug.PostStateCrc"/>. Carries the server's serialized state at
        /// the failing timing — <see cref="MetaProviderBase"/> / LocalBackend copy it onto
        /// <c>MetaOperation.Debug</c> for delivery to the client's
        /// <c>IDesyncDiagnostics.OnDeepStateDesync</c>. Null on the common (matching) path.
        /// </summary>
        public PayloadDebug? Debug { get; set; }
    }
}
