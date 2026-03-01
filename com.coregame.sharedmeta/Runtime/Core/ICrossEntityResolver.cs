using System.Collections.Generic;
using System.Threading.Tasks;

namespace SharedMeta.Core
{
    /// <summary>
    /// Recorded result from a cross-entity call during CrossOptimistic local execution.
    /// Used for desync detection by comparing with server results.
    /// </summary>
    public class CrossEntityLocalResult
    {
        public string EntityId { get; set; } = "";
        public string ServiceName { get; set; } = "";
        public string MethodName { get; set; } = "";
        public object? Result { get; set; }
    }

    /// <summary>
    /// Provides lazy entity subscription and local state access
    /// for CrossOptimistic execution on the client.
    /// Also records cross-entity call results for desync detection.
    /// </summary>
    public interface ICrossEntityResolver
    {
        /// <summary>
        /// Check if entity is already subscribed (synchronous, no network).
        /// Called by generated LocalEntityCaller before accessing state.
        /// </summary>
        bool IsSubscribed(string entityId);

        /// <summary>
        /// Ensure entity is subscribed. No-op if already subscribed.
        /// Called by generated LocalEntityCaller before accessing state.
        /// </summary>
        Task EnsureSubscribedAsync(string entityId, string stateTypeName);

        /// <summary>
        /// Get the local state object for a subscribed entity.
        /// Throws if not subscribed.
        /// </summary>
        object GetEntityState(string entityId);

        /// <summary>
        /// Serializer for creating client MetaContext.
        /// </summary>
        IMetaSerializer Serializer { get; }

        /// <summary>
        /// Record a cross-entity call result for desync comparison.
        /// Called by generated LocalEntityCaller after executing a method.
        /// </summary>
        void RecordCrossEntityResult(string entityId, string serviceName, string methodName, object? result);

        /// <summary>
        /// Take all recorded results (clears the internal list).
        /// Called by generated CrossOptimistic method after local execution.
        /// </summary>
        List<CrossEntityLocalResult> TakeRecordedResults();
    }
}
