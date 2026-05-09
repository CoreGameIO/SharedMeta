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
        /// Overwrite the cached state reference for an entity. Called by generated
        /// ApiClient code in ServerReplace mode after <c>_state = Unpack(stateBytes)</c>,
        /// so that <see cref="GetEntityState"/> and <c>Client.GetState&lt;T&gt;()</c>
        /// observe the same fresh instance the ApiClient now holds. No-op if the entity
        /// is not subscribed.
        /// </summary>
        void UpdateCachedState(string entityId, object newState);

        /// <summary>
        /// Get the config object for a subscribed entity. Returns null if no config.
        /// Used by generated LocalEntityCaller to propagate Config to cross-entity context.
        /// </summary>
        object? GetEntityConfig(string entityId);

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

        /// <summary>
        /// 0.20.0 client-side sibling resolution. Asks the registered <see cref="MetaServiceConfig.ClientSiblingFactory"/>
        /// for <paramref name="interfaceType"/> to produce a transient impl bound to
        /// <paramref name="metaContext"/>. Used during client-side replay/optimistic flows when
        /// user code calls <c>Get{Iface}SiblingAsync()</c> or <c>GetI{Iface}(self).Method(...)</c>
        /// — generated code wires <c>ctx.SiblingServiceResolver</c> to delegate here. Returns
        /// null if no service is registered for the interface (or the registered config predates
        /// 0.20.0 and didn't emit a sibling factory).
        /// </summary>
        object? ResolveSibling(System.Type interfaceType, object metaContext);
    }
}
