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
        /// </summary>
        void OnCrossEntityResult(string entityId, string serviceName, string methodName, byte[]? resultBytes);

        /// <summary>
        /// Called when the optimistic random scroll delta doesn't match between server and client.
        /// </summary>
        void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta);

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
