using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Random;

namespace SharedMeta.Core
{
    /// <summary>
    /// Base context for executing shared meta logic.
    /// Implementations differ for Client (Replay) and Server (Real).
    /// </summary>
    public abstract class MetaContext
    {
        /// <summary>
        /// Gets the strongly-typed state of the current entity.
        /// </summary>
        public abstract object StateObject { get; }

        /// <summary>
        /// Read-only access to another entity's state.
        /// On Server: calls target entity grain ([AlwaysInterleave]), records bytes for replay.
        /// On Client (replay): reads pre-recorded bytes from replay payload.
        /// Returns null if entity doesn't exist or isn't activated.
        /// </summary>
        /// <typeparam name="TEntityState">The state type of the target entity.</typeparam>
        /// <param name="entityId">The target entity's ID.</param>
        public abstract Task<TEntityState?> GetState<TEntityState>(string entityId) where TEntityState : class, ISharedState;

        /// <summary>
        /// Subscribe to updates from a remote entity (Reactive Sync).
        /// </summary>
        /// <typeparam name="TInterface">The service interface.</typeparam>
        /// <param name="id">The entity ID.</param>
        /// <summary>
        /// Subscribe to updates from a remote entity (Reactive Sync).
        /// </summary>
        /// <typeparam name="TInterface">The service interface.</typeparam>
        /// <param name="id">The entity ID.</param>
        public abstract void Observe<TInterface>(string id) where TInterface : class, IMetaService;

        /// <summary>
        /// Access an external server-side service (e.g. Random, Time).
        /// On Server: Returns real service.
        /// On Client: Returns Replay Proxy.
        /// </summary>
        public abstract TInterface GetExternal<TInterface>() where TInterface : class;

        /// <summary>
        /// True if executing on the Server (authoritative), False if Client (Replay/Optimistic).
        /// </summary>
        public virtual bool IsServer => false;

        /// <summary>
        /// True if context is currently replaying from a payload (broadcast replay).
        /// Used by generated code to distinguish broadcast replay from CrossOptimistic local execution.
        /// Both have CrossEntityResolver set, but only broadcast has IsReplaying = true.
        /// </summary>
        public virtual bool IsReplaying => false;

        /// <summary>
        /// The ID of the caller making the current request.
        /// On Server: Set from transport layer (SignalR ConnectionId, Orleans caller grain, etc.)
        /// On Client: Set to the local client ID.
        /// </summary>
        public string? CallerId { get; set; }

        /// <summary>
        /// App version of the originating client for the current call. Propagated from
        /// <see cref="SharedMeta.Core.Transport.RpcCall.CallerClientVersion"/> on the server, and
        /// across cross-entity boundaries so the target entity sees the same version as the
        /// session-level call. Used by <c>ComputeSchemaCapForClient</c> to avoid premature
        /// migration when a chain of cross-entity calls activates a fresh target entity.
        /// On Client: typically null (the client knows its own version directly).
        /// </summary>
        public string? CallerClientVersion { get; set; }

        /// <summary>
        /// The entity ID of the current entity.
        /// Used for cross-entity calls and self-identification.
        /// </summary>
        public string? EntityId { get; set; }

        /// <summary>
        /// Synchronized server time (UTC ticks) for deterministic time access.
        /// On Server: set from client's captured time (RpcCall.ServerTimeTicks).
        /// On Client: set before local execution (Optimistic) or replay (Server/Broadcast).
        /// Services access via MetaContextAccessor.Current.ServerTimeTicks.
        /// </summary>
        public long ServerTimeTicks { get; set; }

        /// <summary>
        /// 0.20.0 (server-side only): true when the active call originated from a direct
        /// client RPC (<c>EntityGrain.HandleCallAsync</c> / <c>HandleQueryAsync</c> /
        /// <c>HandleSignalAsync</c>); false when it came in via cross-entity
        /// (<c>HandleCallFromEntityAsync</c>). Generated dispatcher cases for
        /// <c>[MetaMethod(GenerateClientApi = false)]</c> methods inline-check this flag and
        /// reject the call when true. Sibling-bypass dispatches the in-process typed caller
        /// and never reaches a dispatcher, so this flag is irrelevant to that path. On the
        /// client this property is unused and stays at its default (true).
        /// </summary>
        public bool IsClientCall { get; set; } = true;

        /// <summary>
        /// Cross-entity resolver for CrossOptimistic mode.
        /// When set, generated GetI{Service}(entityId) returns LocalEntityCaller
        /// instead of EntityReplayer, enabling local cross-entity execution.
        /// </summary>
        public ICrossEntityResolver? CrossEntityResolver { get; set; }

        /// <summary>
        /// Optimistic deterministic random. Runs identically on client and server.
        /// Access via Context.Random.Next(100) in service implementations.
        /// </summary>
        public IMetaRandom? Random { get; set; }

        /// <summary>
        /// Server-only random. On server: generates real values (recorded to replay payload).
        /// On client: reads pre-recorded values from replay payload.
        /// Access via Context.ServerRandom.Next(100) in service implementations.
        /// </summary>
        public IMetaRandom? ServerRandom { get; set; }

        /// <summary>
        /// Named deterministic randoms declared via <see cref="NamedRandomAttribute"/> on the entity state.
        /// Positional: index corresponds to attribute declaration order on the state class.
        /// Generated context properties access this list by literal index (e.g. Context.NamedRandoms[0]).
        /// Semantics mirror <see cref="Random"/> (same algorithm and seed on server and client).
        /// Null or empty when the state has no [NamedRandom] attributes.
        /// </summary>
        public IReadOnlyList<IMetaRandom>? NamedRandoms { get; set; }

        /// <summary>
        /// Logger for meta methods. On server: bridges to ILogger. On client: uses MetaLog.
        /// </summary>
        public IMetaLogger Log { get; set; } = NullMetaLogger.Instance;

        /// <summary>
        /// Static game configuration for this entity.
        /// Provided by IMetaConfigProvider on server, sent to client on subscribe.
        /// Access the typed config via generated Context.Config property in service implementations.
        /// </summary>
        public object? Config { get; set; }

        /// <summary>
        /// State schema version of the current entity, as recorded by <c>[MetaInit]</c>.
        /// Set by the framework before each call/init dispatch. <c>0</c> until the entity has
        /// completed its first init. During a migration step, reflects the version <em>before</em>
        /// the step runs (i.e. the source version for the <c>[MetaInit]</c> being dispatched).
        /// </summary>
        public int Version { get; set; }

        /// <summary>
        /// Config version currently pinned on this context. Mirrors the resolved
        /// <see cref="MetaConfigVersion"/> of <see cref="Config"/>:
        /// <list type="bullet">
        ///   <item>During a regular call, this is the per-caller resolved version (per <c>[MetaConfigVersion]</c> rules).</item>
        ///   <item>During a <c>[MetaInit]</c> migration step, this is the transition version pinned by the framework.</item>
        ///   <item>During a <c>[NoMigrate]</c> call, this is the floor version matching the entity's persisted state schema.</item>
        /// </list>
        /// </summary>
        public MetaConfigVersion ConfigVersion { get; set; }

        /// <summary>
        /// Force-persist the current entity state mid-method.
        /// On server: persists state to storage immediately.
        /// On client: no-op (returns completed task).
        /// Use for pseudo-transactional patterns where state must be saved before continuing.
        /// </summary>
        public virtual Task SaveStateAsync() => Task.CompletedTask;

        /// <summary>
        /// Patch wrapper for ServerPatch mode. Non-null when patch tracking is active.
        /// The actual type is generated (e.g., GameStatePatchWrapper).
        /// </summary>
        public object? PatchWrapper { get; set; }

        /// <summary>
        /// True when patch tracking is active (ServerPatch mode).
        /// </summary>
        public bool IsPatchTracking => PatchWrapper != null;

        /// <summary>
        /// Serializer for PatchState wrappers and general use.
        /// </summary>
        public abstract IMetaSerializer MetaSerializer { get; }

        // ============================================
        // Logging Convenience Methods
        // ============================================

        public void LogDebug(string template, params object?[] args)
        {
            if (!Log.IsEnabled(MetaLogLevel.Debug)) return;
            Log.Log(MetaLogLevel.Debug, FormatTemplate(template, args));
        }

        public void LogInfo(string template, params object?[] args)
        {
            if (!Log.IsEnabled(MetaLogLevel.Info)) return;
            Log.Log(MetaLogLevel.Info, FormatTemplate(template, args));
        }

        public void LogWarning(string template, params object?[] args)
        {
            if (!Log.IsEnabled(MetaLogLevel.Warning)) return;
            Log.Log(MetaLogLevel.Warning, FormatTemplate(template, args));
        }

        public void LogError(string template, params object?[] args)
        {
            if (!Log.IsEnabled(MetaLogLevel.Error)) return;
            Log.Log(MetaLogLevel.Error, FormatTemplate(template, args));
        }

        public void LogError(Exception exception, string template, params object?[] args)
        {
            if (!Log.IsEnabled(MetaLogLevel.Error)) return;
            Log.Log(MetaLogLevel.Error, FormatTemplate(template, args), exception);
        }

        private static string FormatTemplate(string template, object?[] args)
        {
            if (args.Length == 0) return template;
            var sb = new System.Text.StringBuilder(template.Length);
            int argIndex = 0;
            for (int i = 0; i < template.Length; i++)
            {
                if (template[i] == '{')
                {
                    var end = template.IndexOf('}', i + 1);
                    if (end > i && argIndex < args.Length)
                    {
                        sb.Append(args[argIndex++]?.ToString() ?? "null");
                        i = end;
                        continue;
                    }
                }
                sb.Append(template[i]);
            }
            return sb.ToString();
        }

        // ============================================
        // Service Caching Helpers (used by generated code)
        // ============================================
        
        /// <summary>
        /// Try to get a cached service wrapper.
        /// </summary>
        public abstract bool TryGetCached(Type key, out object? value);
        
        /// <summary>
        /// Cache a service wrapper.
        /// </summary>
        public abstract void CacheService(Type key, object wrapper);
        
        /// <summary>
        /// Resolve a service implementation (server only).
        /// </summary>
        public abstract TService ResolveService<TService>() where TService : class;

        /// <summary>
        /// 0.20.0: Resolve a sibling-service impl instance hosted on the same entity (same
        /// <c>TState</c>, same grain). Wired by <c>EntityGrain.OnActivateAsync</c> to point
        /// at the provider's lazy <c>Get{Name}()</c> getters. Used by generated cross-entity
        /// accessors when the target entity id matches this entity — the call is dispatched
        /// directly on the cached sibling instance with typed args, skipping the cross-entity
        /// serialization+grain-RPC path entirely.
        /// </summary>
        public Func<Type, object?>? SiblingServiceResolver { get; set; }

        // ============================================
        // Transformer Support
        // ============================================

        private readonly Dictionary<Type, object> _transformerCache = new();

        /// <summary>
        /// Registry for auto-discovery of transformers.
        /// Set this to enable automatic transformation of registered types.
        /// </summary>
        public TransformerRegistry? TransformerRegistry { get; set; }

        /// <summary>
        /// Get or create a transformer instance.
        /// Uses [Transformer(UseResolver)] to determine instantiation method.
        /// </summary>
        /// <typeparam name="TTransformer">The transformer type.</typeparam>
        /// <returns>The transformer instance.</returns>
        public TTransformer GetTransformer<TTransformer>() where TTransformer : class
        {
            var type = typeof(TTransformer);

            if (_transformerCache.TryGetValue(type, out var cached))
                return (TTransformer)cached;

            // Check for [Transformer(UseResolver)] attribute
            var attr = type.GetCustomAttribute<TransformerAttribute>();
            var useResolver = attr?.UseResolver ?? false;

            var transformer = useResolver
                ? ResolveService<TTransformer>()
                : (TTransformer)Activator.CreateInstance(type)!;

            _transformerCache[type] = transformer;
            return transformer;
        }

        /// <summary>
        /// Get or create a transformer instance by type.
        /// </summary>
        /// <param name="transformerType">The transformer type.</param>
        /// <returns>The transformer instance.</returns>
        public object GetTransformer(Type transformerType)
        {
            if (_transformerCache.TryGetValue(transformerType, out var cached))
                return cached;

            // Check for [Transformer(UseResolver)] attribute
            var attr = transformerType.GetCustomAttribute<TransformerAttribute>();
            var useResolver = attr?.UseResolver ?? false;

            object transformer;
            if (useResolver)
            {
                // Use reflection to call ResolveService<T>
                var method = typeof(MetaContext).GetMethod(nameof(ResolveService))!.MakeGenericMethod(transformerType);
                transformer = method.Invoke(this, null)!;
            }
            else
            {
                transformer = Activator.CreateInstance(transformerType)!;
            }

            _transformerCache[transformerType] = transformer;
            return transformer;
        }

        /// <summary>
        /// Box a complex value using a transformer.
        /// </summary>
        /// <param name="transformerType">The transformer type.</param>
        /// <param name="complexValue">The complex value to box.</param>
        /// <returns>The boxed (simple) value.</returns>
        public object BoxValue(Type transformerType, object complexValue)
        {
            var transformer = GetTransformer(transformerType);
            var interfaces = transformerType.GetInterfaces();

            // Try IStateArgumentTransformer first (3 generic args)
            var stateInterface = interfaces.FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IStateArgumentTransformer<,,>));

            if (stateInterface != null)
            {
                var boxMethod = transformerType.GetMethod("Box")!;
                return boxMethod.Invoke(transformer, new[] { complexValue, StateObject })!;
            }

            // Fall back to IArgumentTransformer (2 generic args)
            var simpleInterface = interfaces.FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IArgumentTransformer<,>));

            if (simpleInterface != null)
            {
                var boxMethod = transformerType.GetMethod("Box")!;
                return boxMethod.Invoke(transformer, new[] { complexValue })!;
            }

            throw new InvalidOperationException($"Type {transformerType} does not implement IArgumentTransformer or IStateArgumentTransformer");
        }

        /// <summary>
        /// Unbox a simple value using a transformer.
        /// </summary>
        /// <param name="transformerType">The transformer type.</param>
        /// <param name="simpleValue">The boxed (simple) value.</param>
        /// <returns>The unboxed (complex) value.</returns>
        public object UnboxValue(Type transformerType, object simpleValue)
        {
            var transformer = GetTransformer(transformerType);
            var interfaces = transformerType.GetInterfaces();

            // Try IStateArgumentTransformer first (3 generic args)
            var stateInterface = interfaces.FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IStateArgumentTransformer<,,>));

            if (stateInterface != null)
            {
                var unboxMethod = transformerType.GetMethod("Unbox")!;
                return unboxMethod.Invoke(transformer, new[] { simpleValue, StateObject })!;
            }

            // Fall back to IArgumentTransformer (2 generic args)
            var simpleInterface = interfaces.FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IArgumentTransformer<,>));

            if (simpleInterface != null)
            {
                var unboxMethod = transformerType.GetMethod("Unbox")!;
                return unboxMethod.Invoke(transformer, new[] { simpleValue })!;
            }

            throw new InvalidOperationException($"Type {transformerType} does not implement IArgumentTransformer or IStateArgumentTransformer");
        }

        /// <summary>
        /// Try to box a value using auto-discovery from TransformerRegistry.
        /// Uses typed invokers when available (no reflection).
        /// </summary>
        /// <param name="complexType">The complex type to transform.</param>
        /// <param name="complexValue">The complex value to box.</param>
        /// <param name="boxedValue">The boxed (simple) value if successful.</param>
        /// <param name="simpleType">The simple type if successful.</param>
        /// <returns>True if a transformer was found and applied.</returns>
        public bool TryAutoBox(Type complexType, object complexValue, out object? boxedValue, out Type? simpleType)
        {
            boxedValue = null;
            simpleType = null;

            var invoker = TransformerRegistry?.GetInvoker(complexType);
            if (invoker == null)
                return false;

            boxedValue = invoker.Box(complexValue, StateObject);
            simpleType = invoker.SimpleType;
            return true;
        }

        /// <summary>
        /// Try to unbox a value using auto-discovery from TransformerRegistry.
        /// Uses typed invokers when available (no reflection).
        /// </summary>
        /// <param name="complexType">The expected complex type.</param>
        /// <param name="simpleValue">The boxed (simple) value.</param>
        /// <param name="complexValue">The unboxed (complex) value if successful.</param>
        /// <returns>True if a transformer was found and applied.</returns>
        public bool TryAutoUnbox(Type complexType, object simpleValue, out object? complexValue)
        {
            complexValue = null;

            var invoker = TransformerRegistry?.GetInvoker(complexType);
            if (invoker == null)
                return false;

            complexValue = invoker.Unbox(simpleValue, StateObject);
            return true;
        }

        /// <summary>
        /// Get the simple type for a complex type using auto-discovery.
        /// Uses typed invokers when available (no reflection).
        /// </summary>
        /// <param name="complexType">The complex type.</param>
        /// <returns>The simple type, or null if no transformer registered.</returns>
        public Type? GetAutoSimpleType(Type complexType)
        {
            return TransformerRegistry?.GetSimpleType(complexType);
        }

        /// <summary>
        /// Get the simple type that a transformer boxes to.
        /// </summary>
        /// <param name="transformerType">The transformer type.</param>
        /// <returns>The simple (boxed) type.</returns>
        public static Type GetSimpleType(Type transformerType)
        {
            var interfaces = transformerType.GetInterfaces();

            // Try IStateArgumentTransformer first
            var stateInterface = interfaces.FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IStateArgumentTransformer<,,>));

            if (stateInterface != null)
            {
                return stateInterface.GetGenericArguments()[1]; // TSimple is second arg
            }

            // Fall back to IArgumentTransformer
            var simpleInterface = interfaces.FirstOrDefault(i =>
                i.IsGenericType &&
                i.GetGenericTypeDefinition() == typeof(IArgumentTransformer<,>));

            if (simpleInterface != null)
            {
                return simpleInterface.GetGenericArguments()[1]; // TSimple is second arg
            }

            throw new InvalidOperationException($"Type {transformerType} does not implement IArgumentTransformer or IStateArgumentTransformer");
        }

        // ============================================
        // Compact Helper Methods for Generated Code
        // ============================================

        /// <summary>
        /// Read a value from payload with auto-discovery transformation.
        /// Used by generated code for compact unboxing.
        /// </summary>
        public T ReadWithAutoUnbox<T>(IPayloadReader reader, IMetaSerializer serializer)
        {
            var simpleType = GetAutoSimpleType(typeof(T));
            if (simpleType != null)
            {
                var boxedBytes = reader.Read<byte[]>();
                var boxed = serializer.Unpack(simpleType, boxedBytes);
                TryAutoUnbox(typeof(T), boxed!, out var unboxed);
                return (T)unboxed!;
            }
            return reader.Read<T>();
        }

        /// <summary>
        /// Write a value to payload with auto-discovery transformation.
        /// Used by generated code for compact boxing.
        /// </summary>
        public void WriteWithAutoBox<T>(IPayloadWriter writer, T value, IMetaSerializer serializer)
        {
            if (TryAutoBox(typeof(T), value!, out var boxed, out var simpleType))
            {
                var boxedBytes = serializer.Pack(simpleType!, boxed!);
                writer.Write(boxedBytes);
            }
            else
            {
                writer.Write(value);
            }
        }
    }

    /// <summary>
    /// Typed wrapper for convenience.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    public abstract class MetaContext<TState> : MetaContext
    {
        public TState State => (TState)StateObject;
    }

    /// <summary>
    /// Interface for server-side recording context.
    /// Used by generated Recorder wrappers.
    /// </summary>
    public interface IServerRecordContext
    {
        IMetaSerializer Serializer { get; }

        /// <summary>
        /// Active payload writer for current operation.
        /// </summary>
        IPayloadWriter Writer { get; }

        /// <summary>
        /// Whether debug info should be recorded.
        /// </summary>
        bool DebugEnabled { get; }

        /// <summary>
        /// Add debug info for current write operation.
        /// </summary>
        void WriteDebugInfo(string info);

        /// <summary>
        /// Start recording a new operation.
        /// </summary>
        void BeginOperation();

        /// <summary>
        /// Complete recording and return the payload bytes.
        /// </summary>
        byte[] EndOperation();

        /// <summary>
        /// Get current debug info and reset.
        /// </summary>
        PayloadDebug? GetAndClearDebug();

        /// <summary>
        /// The entity ID of the current entity.
        /// </summary>
        string? EntityId { get; }

        /// <summary>
        /// Call a method on another entity asynchronously.
        /// Used by generated EntityCaller interfaces for cross-entity communication.
        /// </summary>
        /// <param name="targetEntityId">The ID of the target entity.</param>
        /// <param name="serviceName">The service interface name (e.g., "IProfileService").</param>
        /// <param name="methodName">The method alias/name.</param>
        /// <param name="argsBytes">Serialized method arguments.</param>
        /// <returns>Serialized result bytes (empty for void methods).</returns>
        Task<byte[]> CallEntityAsync(string targetEntityId, string serviceName, string methodName, byte[] argsBytes);

        /// <summary>
        /// 0.22.0+: Fire-and-forget cross-entity call. Used by generated EntityCaller wrappers
        /// for methods marked <c>[MetaMethod(OneWay = true)]</c>. The source grain returns
        /// immediately; the target grain processes asynchronously via an Orleans <c>[OneWay]</c>
        /// invocation. No result is recorded into the replay payload — the client-side replayer
        /// for the same call site consumes nothing.
        /// <para>
        /// Default implementation throws — implementations that don't support OneWay (e.g.
        /// <see cref="NullServerRecordContext"/>) can remain on the default.
        /// </para>
        /// </summary>
        void CallEntityOneWay(string targetEntityId, string serviceName, string methodName, byte[] argsBytes)
            => throw new NotSupportedException(
                "This IServerRecordContext does not support OneWay cross-entity calls.");
    }

    /// <summary>
    /// Interface for client-side replay context.
    /// Used by generated Replayer wrappers.
    /// </summary>
    public interface IClientReplayContext
    {
        IMetaSerializer Serializer { get; }
        
        /// <summary>
        /// Active payload reader for current replay.
        /// </summary>
        IPayloadReader Reader { get; }
    }
}
