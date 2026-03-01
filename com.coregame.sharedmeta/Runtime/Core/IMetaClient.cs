using System;
using System.Threading.Tasks;

namespace SharedMeta.Core
{
    /// <summary>
    /// Delegate for invoking a method locally during replay (void methods).
    /// </summary>
    public delegate void LocalMethodInvoker(string serviceName, string methodName, byte[] argsBytes, IMetaSerializer serializer);

    /// <summary>
    /// Delegate for invoking a method locally with result (for optimistic execution).
    /// Returns the method result, or null for void methods.
    /// </summary>
    public delegate object? LocalMethodInvokerWithResult(string serviceName, string methodName, byte[] argsBytes, IMetaSerializer serializer);

    /// <summary>
    /// Direct local invoker that executes service method without serialization overhead.
    /// Used for optimistic execution where we already have typed arguments.
    /// </summary>
    public delegate object? DirectLocalInvoker();

    /// <summary>
    /// Lazy serialization delegate that produces serialized arguments.
    /// Used for deferred serialization when SkipServerOnFalse is true.
    /// </summary>
    public delegate byte[] LazyArgsSerializer();

    /// <summary>
    /// Client instance for communicating with SharedMeta services.
    /// Each client represents one player/connection.
    /// </summary>
    public interface IMetaClient : IDisposable
    {
        /// <summary>
        /// Unique identifier for this client connection.
        /// </summary>
        string ClientId { get; }
        
        /// <summary>
        /// The entity ID this client is connected to.
        /// </summary>
        string EntityId { get; }
        
        /// <summary>
        /// Connect to an entity and receive initial state.
        /// </summary>
        Task ConnectAsync(string entityId);

        /// <summary>
        /// Disconnect from the current entity.
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Get the current state for this entity.
        /// </summary>
        TState GetState<TState>() where TState : class, ISharedState;
        
        /// <summary>
        /// Call a service method asynchronously.
        /// Handles pack/send/unpack/replay internally.
        /// </summary>
        Task<TResult> CallAsync<TArgs, TResult>(string serviceName, string methodName, TArgs args);
        
        /// <summary>
        /// Call a service method with pre-serialized args.
        /// </summary>
        Task<TResult> CallAsync<TResult>(string serviceName, string methodName, byte[] argsBytes);
        
        /// <summary>
        /// Call a void service method asynchronously.
        /// </summary>
        Task CallVoidAsync<TArgs>(string serviceName, string methodName, TArgs args);
        
        /// <summary>
        /// Call a void service method with pre-serialized args.
        /// </summary>
        Task CallVoidAsync(string serviceName, string methodName, byte[] argsBytes);

        /// <summary>
        /// Optimistic execution: call local first, then server.
        /// </summary>
        /// <param name="serviceName">Service name for RPC</param>
        /// <param name="methodName">Method alias for RPC</param>
        /// <param name="argsBytes">Pre-serialized arguments</param>
        /// <param name="localInvoke">Direct local method invocation (no serialization overhead)</param>
        /// <param name="skipServerOnFalse">If true, skip server call when local returns false/default</param>
        /// <returns>Result from local execution (server result is ignored in optimistic mode)</returns>
        Task<TResult> CallOptimisticAsync<TResult>(
            string serviceName,
            string methodName,
            byte[] argsBytes,
            DirectLocalInvoker localInvoke,
            bool skipServerOnFalse = false);

        /// <summary>
        /// Optimistic execution for void methods.
        /// </summary>
        Task CallOptimisticVoidAsync(
            string serviceName,
            string methodName,
            byte[] argsBytes,
            DirectLocalInvoker localInvoke,
            bool skipServerOnFalse = false);

        /// <summary>
        /// Optimistic execution with lazy serialization.
        /// Serialization is deferred until after local execution succeeds.
        /// Use this when SkipServerOnFalse=true to avoid wasted serialization.
        /// </summary>
        Task<TResult> CallOptimisticLazyAsync<TResult>(
            string serviceName,
            string methodName,
            DirectLocalInvoker localInvoke,
            LazyArgsSerializer serializeArgs,
            bool skipServerOnFalse = true);

        /// <summary>
        /// Optimistic void execution with lazy serialization.
        /// </summary>
        Task CallOptimisticLazyVoidAsync(
            string serviceName,
            string methodName,
            DirectLocalInvoker localInvoke,
            LazyArgsSerializer serializeArgs,
            bool skipServerOnFalse = true);

        /// <summary>
        /// Event raised when server pushes a method call for replay.
        /// </summary>
        event Action<RpcCall>? OnIncomingCall;
        
        /// <summary>
        /// Subscribe to a method execution callback (legacy API).
        /// </summary>
        void Subscribe<TArgs>(string methodName, Action<TArgs> beforeCallback, Action<TArgs>? afterCallback = null);

        /// <summary>
        /// Subscribe to a method being replayed from server broadcast.
        /// This fires when another client's action is replayed locally.
        /// Returns a subscription handle that can be disposed to unsubscribe.
        /// </summary>
        /// <param name="serviceName">Service interface name (e.g., "ICardGameService", "ILobbySubscriber")</param>
        /// <param name="methodName">Method name (e.g., "OnMatchFound")</param>
        /// <param name="handler">Handler to invoke when the method is replayed</param>
        IMethodSubscription OnMethodReplayed(string serviceName, string methodName, Action<MethodReplayedContext> handler);

        /// <summary>
        /// Subscribe to a method being replayed with strongly-typed arguments.
        /// </summary>
        IMethodSubscription OnMethodReplayed<TArgs>(string serviceName, string methodName, Action<TArgs> handler);
    }

    /// <summary>
    /// Handle for a method subscription. Dispose to unsubscribe.
    /// </summary>
    public interface IMethodSubscription : IDisposable { }

    /// <summary>
    /// Context provided to method replay handlers.
    /// </summary>
    public class MethodReplayedContext
    {
        public string ServiceName { get; init; } = "";
        public string MethodName { get; init; } = "";
        public string? CallerId { get; init; }
        public byte[] ArgsBytes { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Deserialize arguments to the specified type.
        /// </summary>
        public TArgs GetArgs<TArgs>(IMetaSerializer serializer)
        {
            return serializer.Unpack<TArgs>(ArgsBytes)!;
        }
    }
}
