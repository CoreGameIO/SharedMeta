using System;

namespace SharedMeta.Core
{
    /// <summary>
    /// Access policy for entity subscription.
    /// Defines who can subscribe to entities of this type.
    /// </summary>
    public enum EntityAccessPolicy
    {
        /// <summary>Any player can subscribe. Default behavior.</summary>
        Open = 0,
        /// <summary>
        /// Access checked via IsAuthorized(string playerId) on service implementation.
        /// The service impl must define a public bool IsAuthorized(string playerId) method.
        /// </summary>
        Authorized = 1,
        /// <summary>Only the entity owner (entityId == playerId) can subscribe.</summary>
        OwnerOnly = 2,
        /// <summary>
        /// Entity owned by the player (entityId == playerId).
        /// Same server-side behavior as OwnerOnly.
        /// Generates convenience methods on MetaClient (no entityId needed).
        /// </summary>
        UserOwned = 3
    }

    /// <summary>
    /// Attribute to mark a class as a shared state entity.
    /// The generator will produce a partial class with serialization support.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class SharedStateAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a private backing field for push-based change tracking.
    /// The source generator produces a public property with a tracking setter that
    /// records changes into ChangeTracker during method execution (client-only, zero server overhead).
    /// Field must be private, named with underscore prefix (e.g. _health),
    /// and have a serialization attribute ([Key(n)] or [MemoryPackOrder(n)]).
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public class TrackedAttribute : Attribute
    {
    }

    /// <summary>
    /// Marks a class as static game configuration data.
    /// Config is provided by IMetaConfigProvider on the server and sent to clients on subscribe.
    /// Access via Context.Config in service methods and [MetaInit].
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class MetaConfigAttribute : Attribute
    {
        /// <summary>
        /// If true, this config type is the default config.
        /// Services with DefaultConfig = true on [MetaService] will use this config type
        /// without needing an explicit ConfigType reference.
        /// Only one config class should be marked as default per assembly.
        /// </summary>
        public bool Default { get; set; }
    }

    /// <summary>
    /// Attribute to mark an interface as a shared meta service.
    /// The generator will produce Client Proxies and Server Dispatchers.
    /// </summary>
    [AttributeUsage(AttributeTargets.Interface)]
    public class MetaServiceAttribute : Attribute
    {
        /// <summary>
        /// The state type this service operates on.
        /// Required for DI registration code generation.
        /// </summary>
        public Type? StateType { get; set; }

        /// <summary>
        /// Explicit config type for this service.
        /// The config is provided by IMetaConfigProvider and available via Context.Config.
        /// Takes priority over DefaultConfig.
        /// </summary>
        public Type? ConfigType { get; set; }

        /// <summary>
        /// If true, use the config class marked with [MetaConfig(Default = true)].
        /// No explicit ConfigType reference needed.
        /// </summary>
        public bool DefaultConfig { get; set; }

        /// <summary>
        /// Framework subscriber interfaces implemented by this service.
        /// Used to generate subscriber method invokers for client-side replay.
        /// Example: SubscriberInterfaces = new[] { typeof(ILobbySubscriber) }
        /// </summary>
        public Type[]? SubscriberInterfaces { get; set; }

        /// <summary>
        /// Access policy for entities of this service's state type.
        /// Controls who can subscribe to the entity.
        /// If multiple services share the same state type, the most restrictive policy wins.
        /// </summary>
        public EntityAccessPolicy AccessPolicy { get; set; } = EntityAccessPolicy.Open;
    }


    public enum ExecutionMode
    {
        /// <summary>Execute only on client, no server call.</summary>
        Local,
        /// <summary>Execute on client first (optimistic), then sync with server. Default mode.</summary>
        Optimistic,
        /// <summary>Execute on server first, then replay on client.</summary>
        Server,
        /// <summary>
        /// Execute locally including cross-entity calls on local state,
        /// then confirm on server. Cross-entity broadcasts are suppressed
        /// for the caller. Server-only services will throw at runtime.
        /// </summary>
        CrossOptimistic,
        /// <summary>
        /// Execute only on server, generate state diff patch.
        /// Client applies patch directly without executing the method.
        /// Used for hotfixing server-side bugs when clients can't be updated.
        /// </summary>
        ServerPatch,
        /// <summary>
        /// Execute only on server, send full serialized state.
        /// Client replaces its entire state without executing the method or applying patches.
        /// Efficient when state is fully regenerated (e.g., generating a game map).
        /// </summary>
        ServerReplace
    }

    [AttributeUsage(AttributeTargets.Method)]
    public class MetaMethodAttribute : Attribute
    {
        /// <summary>RPC method alias (defaults to method name).</summary>
        public string Alias { get; set; } = "";

        /// <summary>Method version for compatibility.</summary>
        public int Version { get; set; }

        public bool GenerateClientApi { get; set; } = true;

        /// <summary>Execution mode. Defaults to Optimistic (client-first).</summary>
        public ExecutionMode Mode { get; set; } = ExecutionMode.Optimistic;

        /// <summary>
        /// If true: when local execution returns false/default, skip server call.
        /// Use for validation methods that can fail locally.
        /// </summary>
        public bool SkipServerOnFalse { get; set; }

        /// <summary>
        /// If true, EntityGrain always persists state after this method executes,
        /// regardless of the configured PersistencePolicy.
        /// Use for critical operations: purchases, currency changes, etc.
        /// </summary>
        public bool ForcePersist { get; set; }

        /// <summary>
        /// If true, this method can be called without subscribing to the entity.
        /// Query methods are lightweight read-only request/response with no state sync,
        /// broadcasts, replay, or persistence. Must return a value (void not allowed).
        /// </summary>
        public bool Query { get; set; }

        /// <summary>
        /// If true AND Query is true, skip EntityAccessPolicy checks for this query method.
        /// Allows reading public info from entities the caller cannot subscribe to.
        /// Ignored if Query is false.
        /// </summary>
        public bool OpenAccess { get; set; }
    }

    /// <summary>
    /// Marks a method on a [MetaServiceImpl] class as a state initializer.
    /// Called during EntityGrain activation with the current state version.
    /// Must have signature: Task&lt;int&gt; MethodName(int version)
    /// Returns the new version number to persist.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class MetaInitAttribute : Attribute
    {
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class MetaServiceImplAttribute : Attribute
    {
        public Type ServiceInterface { get; }
        public Type StateType { get; }
        public Type[] Dependencies { get; }

        public MetaServiceImplAttribute(Type serviceInterface, Type stateType, params Type[] dependencies)
        {
            ServiceInterface = serviceInterface;
            StateType = stateType;
            Dependencies = dependencies;
        }
    }

    [AttributeUsage(AttributeTargets.Interface)]
    public class ServerMetaServiceAttribute : Attribute
    {
        // Marker for services that run on Server and Replay on Client
    }

    /// <summary>
    /// Defines a trigger that executes a method when a condition is met after another method completes.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class TriggerAttribute : Attribute
    {
        /// <summary>
        /// The method name that triggers this check.
        /// </summary>
        public string On { get; set; } = "";
        
        /// <summary>
        /// Boolean method name to check. If true, the marked method is executed.
        /// </summary>
        public string Condition { get; set; } = "";
    }

    /// <summary>
    /// Subscribe to receive callbacks before or after a method executes.
    /// Used for UI integration.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public class SubscribeAttribute : Attribute
    {
        /// <summary>
        /// If true, callback is invoked before method execution.
        /// </summary>
        public bool Before { get; set; }
        
        /// <summary>
        /// If true, callback is invoked after method execution.
        /// </summary>
        public bool After { get; set; }
        
        /// <summary>
        /// Method name to subscribe to.
        /// </summary>
        public string Method { get; set; } = "";
    }

    /// <summary>
    /// Marks a transformer class and specifies resolution mode.
    /// By default, transformers are auto-registered in generated code.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class TransformerAttribute : Attribute
    {
        /// <summary>
        /// If true, resolve transformer via DI. If false, create via Activator.
        /// </summary>
        public bool UseResolver { get; set; } = false;

        /// <summary>
        /// If true, skip auto-registration in generated TransformerRegistry.
        /// Use this when you want to register the transformer manually.
        /// </summary>
        public bool NoAutoRegister { get; set; } = false;
    }

    /// <summary>
    /// Explicitly specify a transformer for a method parameter.
    /// Overrides auto-discovery from TransformerRegistry.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class TransformAttribute : Attribute
    {
        public Type TransformerType { get; }

        /// <summary>Use specific transformer type.</summary>
        public TransformAttribute(Type transformerType) => TransformerType = transformerType;
    }

    /// <summary>
    /// Explicitly skip transformation for a method parameter.
    /// Use this to disable auto-discovery for a specific parameter.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public class SkipTransformAttribute : Attribute
    {
    }

    /// <summary>
    /// Defines a trigger that fires when a framework service event is received.
    /// Use this to trigger MetaMethods in response to service events (e.g., Lobby.OnMatchFound).
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class ServiceTriggerAttribute : Attribute
    {
        /// <summary>
        /// The subscriber interface type (e.g., typeof(ILobbySubscriber)).
        /// </summary>
        public Type Service { get; set; } = null!;

        /// <summary>
        /// The method name on the subscriber interface that triggers this (e.g., "OnMatchFound").
        /// </summary>
        public string Method { get; set; } = "";

        /// <summary>
        /// Optional boolean method name to check before executing.
        /// </summary>
        public string Condition { get; set; } = "";
    }

    /// <summary>
    /// Marker interface for framework service subscriber interfaces.
    /// Implement this on interfaces like ILobbySubscriber.
    /// </summary>
    public interface IFrameworkServiceSubscriber
    {
    }

    /// <summary>
    /// Ordering mode for client-side operation execution.
    /// Controls how operations are applied relative to broadcasts from other clients.
    /// </summary>
    public enum OrderingMode
    {
        /// <summary>
        /// Operations are queued and applied in entity sequence order.
        /// Ensures all clients see the same order of operations.
        /// Use for state-mutating operations where order matters.
        /// </summary>
        Ordered,

        /// <summary>
        /// Operations are applied immediately without waiting for sequence ordering.
        /// Use for read-only operations or when ordering doesn't matter.
        /// </summary>
        Immediate
    }

    /// <summary>
    /// Specifies the ordering mode for a service or method.
    /// When applied to an interface, sets the default for all methods.
    /// When applied to a method, overrides the service-level default.
    /// Default behavior (no attribute): Ordered.
    /// </summary>
    /// <example>
    /// // Service-level: all methods are immediate by default
    /// [OrderedExecution(OrderingMode.Immediate)]
    /// public interface IReadOnlyService { ... }
    ///
    /// // Method-level: override service default
    /// [MetaMethod]
    /// [OrderedExecution(OrderingMode.Immediate)]
    /// void GetStatus();
    /// </example>
    [AttributeUsage(AttributeTargets.Interface | AttributeTargets.Method)]
    public class OrderedExecutionAttribute : Attribute
    {
        /// <summary>
        /// The ordering mode for this service or method.
        /// </summary>
        public OrderingMode Mode { get; }

        public OrderedExecutionAttribute(OrderingMode mode)
        {
            Mode = mode;
        }
    }

    /// <summary>
    /// Serializer type for code generation.
    /// Controls how the generator produces serialization code.
    /// </summary>
    public enum SerializerType
    {
        /// <summary>
        /// Auto-detect serializer from referenced assemblies.
        /// Checks for MemoryPack first, falls back to generic IPayloadWriter.
        /// </summary>
        Auto = 0,

        /// <summary>
        /// Generate MemoryPack-specific code using ref struct writers.
        /// Requires MemoryPack package reference.
        /// </summary>
        MemoryPack = 1,

        /// <summary>
        /// Generate generic code using IPayloadWriter interface.
        /// Works with any serializer implementing IMetaSerializer.
        /// </summary>
        Generic = 2
    }

    /// <summary>
    /// Specifies the serializer type for code generation.
    /// Apply to the assembly to control generated serialization code.
    /// If not specified, auto-detection is used (MemoryPack if available, otherwise Generic).
    /// </summary>
    /// <example>
    /// [assembly: SharedMeta.Core.MetaSerializer(SharedMeta.Core.SerializerType.MemoryPack)]
    /// </example>
    [AttributeUsage(AttributeTargets.Assembly)]
    public class MetaSerializerAttribute : Attribute
    {
        /// <summary>
        /// The serializer type to use for code generation.
        /// </summary>
        public SerializerType Type { get; }

        public MetaSerializerAttribute(SerializerType type)
        {
            Type = type;
        }
    }
}
