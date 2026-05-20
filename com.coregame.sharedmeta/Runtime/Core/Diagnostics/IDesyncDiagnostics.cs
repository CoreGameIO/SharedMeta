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
