using System;
using System.Threading.Tasks;

namespace SharedMeta.Core.Diagnostics
{
    /// <summary>
    /// Diagnostics interface for detecting and handling desyncs.
    /// </summary>
    public interface IDesyncDiagnostics
    {
        /// <summary>
        /// Called when server result doesn't match local result.
        /// </summary>
        void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult);

        /// <summary>
        /// Called with cross-entity call result from server (for logging/comparison).
        /// 0.24.0+ identifies the method by <c>ushort methodId</c>; consumers that want a
        /// friendly name resolve it via their own <see cref="SharedMeta.Core.Transport.MetaClientSignature"/>.
        /// </summary>
        void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes);

        /// <summary>
        /// Called when the optimistic random scroll delta doesn't match between server and client.
        /// </summary>
        void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta);

        /// <summary>
        /// Called when deep desync detection finds a patch CRC mismatch.
        /// State mutations differ between server and client, even if return values match.
        /// </summary>
        void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc) { }

        /// <summary>
        /// Called when a generated sync method is invoked but the effective execution mode
        /// (resolved via IExecutionModeProvider) is not Optimistic/Local — i.e. a server round-trip
        /// would be required but sync execution was requested anyway. Fires only when
        /// <see cref="SharedMeta.Core.SyncPolicy.Warn"/> or <see cref="SharedMeta.Core.SyncPolicy.Silent"/>
        /// is configured (Throw raises an exception instead of invoking this callback).
        /// </summary>
        void OnSyncPolicyViolation(string serviceName, string methodName, SharedMeta.Core.ExecutionMode effectiveMode) { }

        /// <summary>
        /// 0.26.6+ Called when a method annotated with
        /// <c>[MetaMethod(DeepStateCheck = SnapshotTiming.X)]</c> detected a CRC mismatch
        /// between client- and server-side serialized state. The state type is a generic
        /// parameter — callers know the type at compile-time (the generator emits an
        /// explicit type argument), so user diagnostic code can deserialize without a
        /// runtime <c>switch (stateType)</c>:
        /// <code>
        /// public void OnDeepStateDesync&lt;TState&gt;(string entityId, byte[] clientBytes,
        ///     byte[] serverBytes, SnapshotTiming timing, long ticks) where TState : class
        /// {
        ///     var clientState = MemoryPackSerializer.Deserialize&lt;TState&gt;(clientBytes);
        ///     var serverState = MemoryPackSerializer.Deserialize&lt;TState&gt;(serverBytes);
        ///     // diff clientState vs serverState — TState is typed
        /// }
        /// </code>
        /// <para>
        /// <paramref name="timing"/> indicates which snapshot moment failed when
        /// <see cref="SharedMeta.Core.SnapshotTiming.Both"/> was requested:
        /// <see cref="SharedMeta.Core.SnapshotTiming.Before"/> = mismatch existed before the
        /// method ran (drift accumulated from prior calls);
        /// <see cref="SharedMeta.Core.SnapshotTiming.After"/> = the method's own body produced
        /// divergence. Never <see cref="SharedMeta.Core.SnapshotTiming.None"/> or
        /// <see cref="SharedMeta.Core.SnapshotTiming.Both"/> at the callback boundary —
        /// the framework checks Before first and stops at the first mismatch.
        /// </para>
        /// </summary>
        /// <typeparam name="TState">Compile-time state type the method operated on. Bound by
        /// the generator from <c>[MetaService(StateType = typeof(TState))]</c>.</typeparam>
        /// <param name="entityId">Entity whose state diverged.</param>
        /// <param name="clientStateBytes">Serialized client-side state at the failing snapshot.</param>
        /// <param name="serverStateBytes">Serialized server-side state at the failing snapshot.</param>
        /// <param name="timing">Which snapshot timing failed (Before or After).</param>
        /// <param name="timestampTicks">UTC ticks captured at the server moment the mismatch
        /// was detected. Useful for correlating with other telemetry.</param>
        void OnDeepStateDesync<TState>(
            string entityId,
            byte[] clientStateBytes,
            byte[] serverStateBytes,
            SharedMeta.Core.SnapshotTiming timing,
            long timestampTicks)
            where TState : class { }

        /// <summary>
        /// Request full state comparison with server.
        /// </summary>
        Task<StateComparisonResult> CompareFullStateAsync(string entityId);
    }

    /// <summary>
    /// Result of comparing local state with server state.
    /// </summary>
    public class StateComparisonResult
    {
        /// <summary>
        /// Whether states match.
        /// </summary>
        public bool IsMatch { get; set; }

        /// <summary>
        /// Human-readable diff if states don't match.
        /// </summary>
        public string? Diff { get; set; }

        /// <summary>
        /// Server state hash for debugging.
        /// </summary>
        public string? ServerStateHash { get; set; }

        /// <summary>
        /// Local state hash for debugging.
        /// </summary>
        public string? LocalStateHash { get; set; }
    }

    /// <summary>
    /// Exception thrown when a desync is detected.
    /// </summary>
    public class DesyncException : Exception
    {
        public string ServiceName { get; }
        public string MethodName { get; }
        public object? ServerResult { get; }
        public object? LocalResult { get; }

        public DesyncException(string message) : base(message)
        {
            ServiceName = "";
            MethodName = "";
        }

        public DesyncException(
            string serviceName,
            string methodName,
            object? serverResult,
            object? localResult)
            : base($"Desync in {serviceName}.{methodName}: server={serverResult}, local={localResult}")
        {
            ServiceName = serviceName;
            MethodName = methodName;
            ServerResult = serverResult;
            LocalResult = localResult;
        }
    }
}
