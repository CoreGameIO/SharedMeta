using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SharedMeta.Core;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Network;
using SharedMeta.Core.Patch;
using SharedMeta.Core.Random;
using SharedMeta.Core.Transport;
using SharedMeta.Server;
using SharedMeta.Server.Core.Grains;

namespace SharedMeta.Server.Core;

/// <summary>
/// Base class for generated MetaProvider implementations.
/// Provides common functionality for handling RPC calls and external events.
/// </summary>
public abstract class MetaProviderBase<TState> : IMetaProvider<TState> where TState : class, ISharedState, new()
{
    protected IMetaProviderContext Context { get; private set; } = null!;
    protected TState State { get; private set; } = null!;
    protected ServerMetaContext<TState>? MetaContext { get; private set; }
    protected ILogger Logger { get; private set; } = NullLogger.Instance;

    private MetaRandom _serverRandom = null!;
    private MetaRandom _optimisticRandom = null!;

    /// <summary>
    /// Service resolver for dependency injection (e.g., IRandomService).
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<Type, object>? ServiceResolver { get; set; }

    /// <summary>
    /// Handler for cross-entity calls (when a service calls another entity).
    /// Returns CrossEntityCallInfo with EntitySequenceNumber and ResultBytes.
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<string, string, string, byte[], long, Task<CrossEntityCallInfo>>? EntityCallHandler { get; set; }

    /// <summary>
    /// Handler for read-only cross-entity state access.
    /// Returns serialized state bytes of the target entity, or null if not found.
    /// Set via constructor or before Initialize is called.
    /// </summary>
    public Func<string, string, Task<byte[]?>>? EntityStateHandler { get; set; }

    /// <summary>
    /// Server-side execution mode provider. Determines per-method execution mode.
    /// Set via DI to enable runtime mode switching (e.g., switching to ServerPatch).
    /// </summary>
    public IExecutionModeProvider? ExecutionModeProvider { get; set; }

    /// <summary>
    /// When true, computes FNV-1a hash of serialized state after each method execution.
    /// Client compares its local hash with the server's to detect state-level desyncs.
    /// </summary>
    public bool DeepDesyncEnabled { get; set; }

    public virtual void Initialize(IMetaProviderContext context, TState state,
        byte[]? serverRandomBytes = null, byte[]? optimisticRandomBytes = null)
    {
        Context = context;
        State = state;
        Logger = context.Logger ?? NullLogger.Instance;
        MetaContext = new ServerMetaContext<TState>(state, context.Serializer);
        MetaContext.EntityId = context.EntityId;
        MetaContext.Log = context.Logger != null
            ? new MetaLoggerAdapter(context.Logger, context.EntityId)
            : NullMetaLogger.Instance;
        MetaContext.ServiceResolver = ServiceResolver;
        MetaContext.EntityCallHandler = EntityCallHandler;
        MetaContext.EntityStateHandler = EntityStateHandler;

        // Initialize deterministic randoms
        _serverRandom = DeserializeOrCreateRandom(context.Serializer, serverRandomBytes, context.EntityId + ":server");
        _optimisticRandom = DeserializeOrCreateRandom(context.Serializer, optimisticRandomBytes, context.EntityId + ":optimistic");

        // Hook for subclass initialization
        OnInitialize();
    }

    private static MetaRandom DeserializeOrCreateRandom(IMetaSerializer serializer, byte[]? bytes, string seed)
    {
        if (bytes != null && bytes.Length > 0)
            return serializer.Unpack<MetaRandom>(bytes);
        return MetaRandom.FromString(seed);
    }

    /// <summary>
    /// Override to perform additional initialization after context and state are set.
    /// </summary>
    protected virtual void OnInitialize() { }

    /// <summary>
    /// Dispatch a service method call. Implemented by generated code.
    /// </summary>
    protected abstract Task<DispatchResult> DispatchCall(string serviceName, string methodName, byte[] payload);

    /// <summary>
    /// Dispatch an external event. Override in derived class if needed.
    /// </summary>
    protected virtual Task<DispatchResult> DispatchEvent(string subscriberInterface, string methodName, byte[] eventData)
    {
        return Task.FromResult(new DispatchResult { ResultBytes = null, TriggersToExecute = null });
    }

    /// <summary>
    /// Create a patch wrapper for the current state. Override in generated code.
    /// </summary>
    protected virtual object? CreatePatchWrapper(PatchNode root) => null;

    public async Task<HandleCallResult> HandleCallAsync(RpcCall call)
    {
        if (MetaContext == null || Context == null)
        {
            return new HandleCallResult { Response = new RpcResponse { Error = "Provider not initialized" } };
        }

        try
        {
            MetaContext.CallerId = call.CallerId;
            MetaContext.ServerTimeTicks = call.ServerTimeTicks;
            MetaContext.Random = _optimisticRandom;
            MetaContext.ServerRandom = new MetaRandomRecorder(_serverRandom, MetaContext);
            MetaContextAccessor.Current = MetaContext;

            // Capture optimistic random scroll position before dispatch
            var scrollIdBefore = _optimisticRandom.ScrollId;

            // Determine server-side execution mode
            var executionMode = ExecutionModeProvider?.GetMode(
                call.ServiceName, call.MethodName, ExecutionMode.Optimistic) ?? ExecutionMode.Optimistic;

            // Set up patch tracking for ServerPatch mode or deep desync detection
            PatchNode? patchRoot = null;
            bool isServerReplace = executionMode == ExecutionMode.ServerReplace;
            bool deepDesyncActive = DeepDesyncEnabled || call.DeepDesyncRequested;
            if (executionMode == ExecutionMode.ServerPatch || deepDesyncActive)
            {
                patchRoot = new PatchNode(-1);
                MetaContext.PatchWrapper = CreatePatchWrapper(patchRoot);
            }

            // Begin recording for replay
            MetaContext.BeginOperation();

            // Dispatch the call
            var result = await DispatchCall(call.ServiceName, call.MethodName, call.Payload);

            // End recording and get replay payload
            var replayPayload = MetaContext.EndOperation();

            // Collect patch bytes if ServerPatch mode was active
            byte[]? patchBytes = null;
            if (patchRoot != null)
            {
                patchRoot.Prune();
                if (patchRoot.HasChanges)
                    patchBytes = Context.Serializer.Pack(patchRoot);
                MetaContext.PatchWrapper = null;
            }

            // Compute optimistic random scroll delta for desync detection
            var randomScrollDelta = _optimisticRandom.ScrollId - scrollIdBefore;

            // Deep desync: compute CRC from patch (field-level mutation tracking)
            uint? deepDesyncCrc = null;
            if (deepDesyncActive && patchRoot != null)
            {
                patchRoot.Prune();
                if (patchRoot.HasChanges)
                {
                    var deepDesyncPatchBytes = Context.Serializer.Pack(patchRoot);
                    deepDesyncCrc = SharedMeta.Core.Patch.PatchCrc.Compute(deepDesyncPatchBytes);
                }
                else
                {
                    deepDesyncCrc = 0; // no changes
                }
            }

            // Capture cross-entity calls made during this operation
            var crossEntityCalls = MetaContext.CrossEntityCalls;

            // Build response
            var response = new HandleCallResult
            {
                Response = new RpcResponse
                {
                    ResultBytes = result.ResultBytes,
                    ReplayPayload = replayPayload,
                    Error = null,
                    RandomScrollDelta = randomScrollDelta,
                    PatchBytes = patchBytes,
                    DeepDesyncCrc = deepDesyncCrc
                },
                Broadcasts = new List<EntityBroadcast>(),
                CrossEntityCalls = crossEntityCalls,
                ForcePersist = result.ForcePersist
            };

            // Create broadcast for this call (to other subscribers)
            var mainBroadcast = new EntityBroadcast
            {
                ServiceName = call.ServiceName,
                MethodName = call.MethodName,
                Payload = call.Payload,
                ReplayPayload = replayPayload,
                ExcludePlayerId = call.CallerId, // Don't broadcast back to caller
                ServerTimeTicks = call.ServerTimeTicks,
                RandomScrollDelta = randomScrollDelta,
                PatchBytes = patchBytes
            };

            // Handle triggers if any — nest inside main broadcast
            if (result.TriggersToExecute is { Count: > 0 } triggers)
            {
                mainBroadcast.TriggerBroadcasts = new List<EntityBroadcast>();
                foreach (var triggerMethod in triggers)
                {
                    var triggerScrollBefore = _optimisticRandom.ScrollId;

                    // Set up patch tracking for trigger (if ServerPatch mode)
                    PatchNode? triggerPatchRoot = null;
                    if (executionMode == ExecutionMode.ServerPatch)
                    {
                        triggerPatchRoot = new PatchNode(-1);
                        MetaContext.PatchWrapper = CreatePatchWrapper(triggerPatchRoot);
                    }

                    MetaContext.BeginOperation();
                    var triggerResult = await DispatchCall(call.ServiceName, triggerMethod, []);
                    var triggerReplay = MetaContext.EndOperation();

                    // Collect trigger patch bytes
                    byte[]? triggerPatchBytes = null;
                    if (triggerPatchRoot != null)
                    {
                        triggerPatchRoot.Prune();
                        if (triggerPatchRoot.HasChanges)
                            triggerPatchBytes = Context.Serializer.Pack(triggerPatchRoot);
                        MetaContext.PatchWrapper = null;
                    }

                    var triggerScrollDelta = _optimisticRandom.ScrollId - triggerScrollBefore;

                    mainBroadcast.TriggerBroadcasts.Add(new EntityBroadcast
                    {
                        ServiceName = call.ServiceName,
                        MethodName = triggerMethod,
                        Payload = [], // Triggers have no arguments
                        ReplayPayload = triggerReplay,
                        ExcludePlayerId = null, // Broadcast triggers to everyone
                        ServerTimeTicks = call.ServerTimeTicks,
                        RandomScrollDelta = triggerScrollDelta,
                        PatchBytes = triggerPatchBytes
                    });
                }
            }

            // ServerReplace: serialize full state AFTER all triggers, capturing final state
            byte[]? stateBytes = null;
            if (isServerReplace)
            {
                stateBytes = GetStateBytes();
                response.Response.StateBytes = stateBytes;
                response.Response.ReplayPayload = null; // no replay needed
                mainBroadcast.StateBytes = stateBytes;
                mainBroadcast.ReplayPayload = null;
            }

            response.Broadcasts.Add(mainBroadcast);

            return response;
        }
        catch (Exception ex)
        {
            Logger.ProviderCallError(ex, call.ServiceName, call.MethodName);
            return new HandleCallResult { Response = new RpcResponse { Error = ex.Message } };
        }
        finally
        {
            MetaContextAccessor.Current = null;
        }
    }

    public async Task<HandleEventResult> HandleExternalEventAsync(
        string subscriberInterface,
        string methodName,
        byte[] eventData,
        string? callerId = null)
    {
        if (MetaContext == null || Context == null)
        {
            return new HandleEventResult();
        }

        try
        {
            MetaContext.CallerId = callerId;
            MetaContext.ServerTimeTicks = DateTime.UtcNow.Ticks;
            MetaContextAccessor.Current = MetaContext;

            MetaContext.BeginOperation();
            var result = await DispatchEvent(subscriberInterface, methodName, eventData);
            var replayPayload = MetaContext.EndOperation();

            var broadcasts = new List<EntityBroadcast>();

            // Create broadcast for this event
            broadcasts.Add(new EntityBroadcast
            {
                ServiceName = subscriberInterface,
                MethodName = methodName,
                Payload = eventData,
                ReplayPayload = replayPayload,
                ExcludePlayerId = null, // Broadcast to everyone
                ServerTimeTicks = MetaContext.ServerTimeTicks
            });

            return new HandleEventResult { Broadcasts = broadcasts };
        }
        catch (Exception ex)
        {
            Logger.ProviderEventError(ex, subscriberInterface, methodName);
            return new HandleEventResult();
        }
        finally
        {
            MetaContextAccessor.Current = null;
        }
    }

    public async Task<QueryCallResponse> HandleQueryAsync(RpcCall call)
    {
        if (MetaContext == null || Context == null)
            return new QueryCallResponse { Error = "Provider not initialized" };

        try
        {
            // Set up minimal context for the query (read-only)
            MetaContext.CallerId = call.CallerId;
            MetaContext.ServerTimeTicks = DateTime.UtcNow.Ticks;
            MetaContextAccessor.Current = MetaContext;

            // Dispatch the call — same dispatcher, but no replay/random/broadcast machinery
            var result = await DispatchCall(call.ServiceName, call.MethodName, call.Payload);

            return new QueryCallResponse
            {
                Success = true,
                ResultBytes = result.ResultBytes
            };
        }
        catch (Exception ex)
        {
            Logger.ProviderCallError(ex, call.ServiceName, call.MethodName);
            return new QueryCallResponse { Error = ex.Message };
        }
        finally
        {
            MetaContextAccessor.Current = null;
        }
    }

    /// <summary>
    /// Check if a method is a query method. Generated code overrides.
    /// </summary>
    public virtual bool IsQueryMethod(string serviceName, string methodName) => false;

    /// <summary>
    /// Check if a query method has OpenAccess. Generated code overrides.
    /// </summary>
    public virtual bool IsOpenAccessQuery(string serviceName, string methodName) => false;

    public byte[] GetStateBytes()
    {
        if (Context == null || State == null) return [];
        return Context.Serializer.Pack(State);
    }

    public byte[] GetServerRandomBytes()
    {
        if (Context == null) return [];
        return Context.Serializer.Pack(_serverRandom);
    }

    public byte[] GetOptimisticRandomBytes()
    {
        if (Context == null) return [];
        return Context.Serializer.Pack(_optimisticRandom);
    }

    public async Task<int> InitializeStateAsync(int currentVersion)
    {
        if (MetaContext == null) return currentVersion;

        // Set up ServerRandom so [MetaInit] methods can use it
        MetaContext.ServerRandom = new MetaRandomRecorder(_serverRandom, MetaContext);
        MetaContext.Random = _optimisticRandom;
        MetaContextAccessor.Current = MetaContext;
        MetaContext.BeginOperation();

        try
        {
            var newVersion = await RunInitAsync(currentVersion);
            return newVersion;
        }
        finally
        {
            MetaContext.EndOperation(); // discard replay payload (init is server-only)
            MetaContextAccessor.Current = null;
            MetaContext.ServerRandom = null;
        }
    }

    /// <summary>
    /// Override in generated code to call [MetaInit] methods.
    /// ServerRandom and Config are available during this call.
    /// </summary>
    protected virtual Task<int> RunInitAsync(int currentVersion)
    {
        return Task.FromResult(currentVersion);
    }

    public virtual void OnDeactivating() { }

    /// <summary>
    /// Access policy for this entity type. Default: Open.
    /// Generated providers override for OwnerOnly or Authorized.
    /// </summary>
    public virtual EntityAccessPolicy AccessPolicy => EntityAccessPolicy.Open;

    /// <summary>
    /// Config version for this entity. Set during activation via InitializeConfig.
    /// </summary>
    public MetaConfigVersion ConfigVersion { get; private set; }

    /// <summary>
    /// Initialize config for this entity with the given version.
    /// Generated code overrides to resolve config from IMetaConfigProvider.
    /// </summary>
    public virtual void InitializeConfig(MetaConfigVersion version)
    {
        ConfigVersion = version;
    }

    /// <summary>
    /// Check if a player is authorized to subscribe to this entity.
    /// Default: Open → true, OwnerOnly → entityId == playerId, Authorized → false (must be overridden).
    /// Generated providers override for Authorized to call service.IsAuthorized(playerId).
    /// </summary>
    public virtual Task<bool> CheckAccessAsync(string playerId)
    {
        return AccessPolicy switch
        {
            EntityAccessPolicy.Open => Task.FromResult(true),
            EntityAccessPolicy.OwnerOnly or EntityAccessPolicy.UserOwned
                => Task.FromResult(Context.EntityId == playerId),
            _ => Task.FromResult(false) // Authorized without override → deny by default
        };
    }
}
