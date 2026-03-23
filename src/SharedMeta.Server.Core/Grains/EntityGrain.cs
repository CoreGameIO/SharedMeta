using System.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Concurrency;
using Orleans.Runtime;
using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Transport;
using SharedMeta.Server;
using SharedMeta.Server.Core.Session;

namespace SharedMeta.Server.Core.Grains
{
    /// <summary>
    /// Context implementation for IMetaProvider.
    /// </summary>
    internal class MetaProviderContext : IMetaProviderContext
    {
        public string EntityId { get; }
        public IMetaSerializer Serializer { get; }
        public IGrainFactory GrainFactory { get; }
        public ILogger? Logger { get; }

        public MetaProviderContext(string entityId, IMetaSerializer serializer, IGrainFactory grainFactory, ILogger? logger = null)
        {
            EntityId = entityId;
            Serializer = serializer;
            GrainFactory = grainFactory;
            Logger = logger;
        }
    }

    /// <summary>
    /// Thin entity grain wrapper with persistent state.
    /// Delegates all business logic to IMetaProvider.
    /// State, subscribers, and sequence numbers survive deactivation/reactivation.
    /// </summary>
    /// <typeparam name="TState">The state type.</typeparam>
    public class EntityGrain<TState> : Grain, IEntityGrain<TState>
        where TState : class, ISharedState, new()
    {
        private readonly IPersistentState<EntityGrainState<TState>> _persistentState;
        private readonly IMetaProviderFactory<TState> _providerFactory;
        private readonly IMetaSerializer _serializer;
        private readonly ILogger _logger;
        private readonly EntityGrainOptions _options;
        private readonly IEntityGrainResolver _entityGrainResolver;
        private readonly IExecutionModeProvider? _executionModeProvider;
        private readonly IConfigVersionResolver? _configVersionResolver;

        private IMetaProvider<TState>? _provider;

        // Persistence policy tracking
        private int _requestsSinceLastSave;
        private DateTime _lastSaveTime = DateTime.UtcNow;
        private bool _isDirty;

        /// <summary>
        /// Runtime grain references for subscribers.
        /// Reconstructed from persisted PlayerId on activation.
        /// </summary>
        private readonly Dictionary<string, ISessionManagerReference> _subscriberRefs = new();

        public EntityGrain(
            [PersistentState("entity", "Default")] IPersistentState<EntityGrainState<TState>> persistentState,
            IMetaProviderFactory<TState> providerFactory,
            IMetaSerializer serializer,
            ILogger<EntityGrain<TState>> logger,
            IOptions<EntityGrainOptions> options,
            IEntityGrainResolver entityGrainResolver,
            IExecutionModeProvider? executionModeProvider = null,
            IConfigVersionResolver? configVersionResolver = null)
        {
            _persistentState = persistentState ?? throw new ArgumentNullException(nameof(persistentState));
            _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _executionModeProvider = executionModeProvider;
            _configVersionResolver = configVersionResolver;
        }

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            var entityId = this.GetPrimaryKeyString();
            var state = _persistentState.State;

            _logger.EntityGrainActivated(typeof(TState).Name, entityId);
            _logger.EntityStateLoaded(typeof(TState).Name, entityId, state.Subscribers.Count, state.EntitySequenceNumber);

            // Prune expired subscribers
            var cutoff = DateTime.UtcNow - _options.SubscriberTtl;
            var expired = state.Subscribers.Where(kv => kv.Value.LastActiveUtc < cutoff).Select(kv => kv.Key).ToList();
            if (expired.Count > 0)
            {
                foreach (var playerId in expired)
                    state.Subscribers.Remove(playerId);

                _logger.ExpiredSubscribersPruned(entityId, expired.Count);
                await _persistentState.WriteStateAsync();
                ResetPersistenceTracking();
            }

            // Reconstruct grain references for surviving subscribers
            foreach (var (playerId, sub) in state.Subscribers)
            {
                _subscriberRefs[playerId] = GrainFactory.GetGrain<ISessionManagerReference>(playerId);
                _logger.SubscriberRestored(entityId, playerId);
            }

            // Create and initialize provider with persisted state
            _provider = _providerFactory.Create();

            // Wire execution mode provider and cross-entity call handler
            if (_provider is MetaProviderBase<TState> providerBase)
            {
                providerBase.ExecutionModeProvider = _executionModeProvider;
                providerBase.EntityCallHandler = async (targetEntityId, serviceName, methodName, argsBytes, serverTimeTicks) =>
                {
                    var targetGrain = _entityGrainResolver.GetEntityGrainByService(
                        GrainFactory, serviceName, targetEntityId);
                    if (targetGrain == null)
                        throw new InvalidOperationException(
                            $"Cannot resolve entity grain for service {serviceName}, entity {targetEntityId}");

                    var result = await targetGrain.HandleCallFromEntityAsync(new RpcCall
                    {
                        ServiceName = serviceName,
                        MethodName = methodName,
                        Payload = argsBytes,
                        ServerTimeTicks = serverTimeTicks
                    });

                    if (result.HasError)
                        throw new InvalidOperationException(
                            $"Cross-entity call failed: {result.ErrorMessage}");

                    return new CrossEntityCallInfo
                    {
                        EntityId = targetEntityId,
                        EntitySequenceNumber = result.EntitySequenceNumber,
                        ResultBytes = result.MainOperation?.Response?.ResultBytes,
                        ServiceName = serviceName,
                        MethodName = methodName
                    };
                };

                providerBase.EntityStateHandler = async (targetEntityId, stateTypeName) =>
                {
                    var targetGrain = _entityGrainResolver.GetEntityGrain(
                        GrainFactory, stateTypeName, targetEntityId);
                    if (targetGrain == null)
                        return null;

                    return await targetGrain.GetEntityStateAsync();
                };
            }

            var context = new MetaProviderContext(entityId, _serializer, GrainFactory, _logger);
            _provider.Initialize(context, state.UserState, state.ServerRandomBytes, state.OptimisticRandomBytes);

            // Resolve config version: use persisted version, or resolve for new entities
            _provider.InitializeConfig(state.ConfigVersion);

            // Persist resolved config version if it changed (first activation or upgrade)
            if (_provider.ConfigVersion != state.ConfigVersion)
            {
                state.ConfigVersion = _provider.ConfigVersion;
            }

            // Run state initialization/migration if provider has [MetaInit] methods
            var newVersion = await _provider.InitializeStateAsync(state.Version);
            if (newVersion != state.Version)
            {
                state.Version = newVersion;
                _logger.EntityStateInitialized(typeof(TState).Name, entityId, newVersion);
            }

            ResetPersistenceTracking();

            await base.OnActivateAsync(cancellationToken);
        }

        public override async Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
        {
            var entityId = this.GetPrimaryKeyString();
            _logger.EntityGrainDeactivating(typeof(TState).Name, entityId);

            _provider?.OnDeactivating();

            // Only persist if there were actual player interactions
            if (_isDirty)
            {
                await _persistentState.WriteStateAsync();
                _logger.EntityStatePersisted(entityId);
            }

            await base.OnDeactivateAsync(reason, cancellationToken);
        }

        public async Task<EntitySnapshot> SubscribeAsync(string playerId, ISessionManagerReference sessionManager)
        {
            // Access policy check
            if (_provider != null)
            {
                var policy = _provider.AccessPolicy;
                if (policy != EntityAccessPolicy.Open)
                {
                    bool allowed;
                    if (policy is EntityAccessPolicy.OwnerOnly or EntityAccessPolicy.UserOwned)
                        allowed = this.GetPrimaryKeyString() == playerId;
                    else // Authorized
                        allowed = await _provider.CheckAccessAsync(playerId);

                    if (!allowed)
                    {
                        _logger.EntityAccessDenied(this.GetPrimaryKeyString(), playerId, policy.ToString());
                        throw new EntityAccessDeniedException(
                            $"Player '{playerId}' is not authorized to access entity '{this.GetPrimaryKeyString()}'");
                    }
                }
            }

            var state = _persistentState.State;

            state.Subscribers[playerId] = new PersistedSubscriberInfo
            {
                PlayerId = playerId,
                LastActiveUtc = DateTime.UtcNow
            };
            _subscriberRefs[playerId] = sessionManager;

            _logger.PlayerSubscribed(this.GetPrimaryKeyString(), playerId);

            await _persistentState.WriteStateAsync();
            ResetPersistenceTracking();

            var stateBytes = _provider?.GetStateBytes() ?? _serializer.Pack(state.UserState);

            return new EntitySnapshot
            {
                StateBytes = stateBytes,
                CurrentSequenceNumber = state.EntitySequenceNumber,
                OptimisticRandomBytes = _provider?.GetOptimisticRandomBytes(),
                ConfigVersion = _provider?.ConfigVersion ?? default
            };
        }

        public async Task UnsubscribeAsync(string playerId)
        {
            _persistentState.State.Subscribers.Remove(playerId);
            _subscriberRefs.Remove(playerId);
            _logger.PlayerUnsubscribed(this.GetPrimaryKeyString(), playerId);

            await _persistentState.WriteStateAsync();
            ResetPersistenceTracking();
        }

        public async Task<EntityCallResult> HandleCallAsync(RpcCall call)
        {
            if (_provider == null)
            {
                return new EntityCallResult { Error = "Provider not initialized" };
            }

            var state = _persistentState.State;
            var operationSequence = ++state.EntitySequenceNumber;
            var forcePersist = false;

            // Update caller's last active time
            if (call.CallerId != null && state.Subscribers.TryGetValue(call.CallerId, out var callerSub))
                callerSub.LastActiveUtc = DateTime.UtcNow;

            try
            {
                var providerResult = await _provider.HandleCallAsync(call);
                forcePersist = providerResult.ForcePersist;

                PersistRandomBytes();

                // Distribute broadcasts to ALL EXCEPT caller
                await DistributeBroadcasts(providerResult.Broadcasts, operationSequence, excludePlayerId: call.CallerId);

                // Build trigger OperationResults from nested broadcasts
                var mainBroadcast = providerResult.Broadcasts.FirstOrDefault();
                var triggerOps = mainBroadcast?.TriggerBroadcasts?.Select(t => new OperationResult
                {
                    Call = new RpcCall { ServiceName = t.ServiceName, MethodName = t.MethodName, Payload = t.Payload },
                    Response = new RpcResponse { ReplayPayload = t.ReplayPayload, RandomScrollDelta = t.RandomScrollDelta, PatchBytes = t.PatchBytes }
                }).ToList();

                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    MainOperation = new OperationResult
                    {
                        Call = call,
                        Response = providerResult.Response
                    },
                    TriggerOperations = triggerOps,
                    CrossEntityCalls = providerResult.CrossEntityCalls,
                    Error = providerResult.Response.Error
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorHandlingCall(ex);
                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    Error = ex.Message
                };
            }
            finally
            {
                await PersistIfNeeded(forcePersist);
            }
        }

        public async Task<EntityCallResult> HandleCallFromEntityAsync(RpcCall call)
        {
            if (_provider == null)
            {
                return new EntityCallResult { Error = "Provider not initialized" };
            }

            var state = _persistentState.State;
            var operationSequence = ++state.EntitySequenceNumber;
            var forcePersist = false;

            try
            {
                var providerResult = await _provider.HandleCallAsync(call);
                forcePersist = providerResult.ForcePersist;

                PersistRandomBytes();

                // Distribute broadcasts to ALL subscribers (no exclusion for cross-entity calls)
                await DistributeBroadcasts(providerResult.Broadcasts, operationSequence, excludePlayerId: null);

                var mainBroadcast = providerResult.Broadcasts.FirstOrDefault();
                var triggerOps = mainBroadcast?.TriggerBroadcasts?.Select(t => new OperationResult
                {
                    Call = new RpcCall { ServiceName = t.ServiceName, MethodName = t.MethodName, Payload = t.Payload },
                    Response = new RpcResponse { ReplayPayload = t.ReplayPayload, RandomScrollDelta = t.RandomScrollDelta, PatchBytes = t.PatchBytes }
                }).ToList();

                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    MainOperation = new OperationResult
                    {
                        Call = call,
                        Response = providerResult.Response
                    },
                    TriggerOperations = triggerOps,
                    CrossEntityCalls = providerResult.CrossEntityCalls,
                    Error = providerResult.Response.Error
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorHandlingCrossEntityCall(ex);
                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    Error = ex.Message
                };
            }
            finally
            {
                await PersistIfNeeded(forcePersist);
            }
        }

        public async Task<QueryCallResponse> HandleQueryAsync(RpcCall call)
        {
            if (_provider == null)
                return new QueryCallResponse { Error = "Provider not initialized" };

            // Verify this is a registered query method
            if (!_provider.IsQueryMethod(call.ServiceName, call.MethodName))
                return new QueryCallResponse { Error = $"Method '{call.ServiceName}.{call.MethodName}' is not a query method" };

            // Access policy check (unless OpenAccess)
            if (!_provider.IsOpenAccessQuery(call.ServiceName, call.MethodName))
            {
                var policy = _provider.AccessPolicy;
                if (policy != EntityAccessPolicy.Open)
                {
                    bool allowed;
                    if (policy is EntityAccessPolicy.OwnerOnly or EntityAccessPolicy.UserOwned)
                        allowed = this.GetPrimaryKeyString() == call.CallerId;
                    else // Authorized
                        allowed = await _provider.CheckAccessAsync(call.CallerId ?? "");

                    if (!allowed)
                        return new QueryCallResponse { Error = $"Access denied for query on entity '{this.GetPrimaryKeyString()}'" };
                }
            }

            try
            {
                return await _provider.HandleQueryAsync(call);
            }
            catch (Exception ex)
            {
                _logger.ErrorHandlingCall(ex);
                return new QueryCallResponse { Error = ex.Message };
            }
        }

        public Task<byte[]?> GetEntityStateAsync()
        {
            var userState = _persistentState.State.UserState;
            return Task.FromResult<byte[]?>(_serializer.Pack(userState));
        }

        public async Task<EntityCallResult> HandleExternalEventAsync(
            string subscriberInterface,
            string methodName,
            byte[] eventData,
            string? callerId = null)
        {
            if (_provider == null)
            {
                return new EntityCallResult { Error = "Provider not initialized" };
            }

            var state = _persistentState.State;
            var operationSequence = ++state.EntitySequenceNumber;

            try
            {
                var providerResult = await _provider.HandleExternalEventAsync(subscriberInterface, methodName, eventData, callerId);

                // Distribute broadcasts to ALL subscribers (no exclusion for external events)
                await DistributeBroadcasts(providerResult.Broadcasts, operationSequence, excludePlayerId: null);

                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    MainOperation = new OperationResult
                    {
                        Call = new RpcCall
                        {
                            ServiceName = subscriberInterface,
                            MethodName = methodName,
                            Payload = eventData,
                            CallerId = callerId
                        },
                        Response = new RpcResponse()
                    }
                };
            }
            catch (Exception ex)
            {
                _logger.ErrorHandlingExternalEvent(ex);
                return new EntityCallResult
                {
                    EntitySequenceNumber = operationSequence,
                    Error = ex.Message
                };
            }
            finally
            {
                await PersistIfNeeded(forcePersist: false);
            }
        }

        /// <summary>
        /// Determines whether state should be persisted based on the configured policy.
        /// </summary>
        private bool ShouldPersist(bool forcePersist)
        {
            if (forcePersist) return true;
            if (!_isDirty) return false;

            var policy = _options.PersistencePolicy;
            return policy.Mode switch
            {
                PersistenceMode.EveryCall => true,
                PersistenceMode.EveryNRequests => _requestsSinceLastSave >= policy.RequestInterval,
                PersistenceMode.EveryNMinutes =>
                    (DateTime.UtcNow - _lastSaveTime).TotalMinutes >= policy.TimeIntervalMinutes,
                PersistenceMode.RequestsOrTime =>
                    _requestsSinceLastSave >= policy.RequestInterval
                    || (DateTime.UtcNow - _lastSaveTime).TotalMinutes >= policy.TimeIntervalMinutes,
                PersistenceMode.OnDeactivationOnly => false,
                _ => true // Unknown mode: safe default
            };
        }

        /// <summary>
        /// Persist state if the policy allows it. Always updates tracking counters.
        /// </summary>
        private async Task PersistIfNeeded(bool forcePersist)
        {
            _requestsSinceLastSave++;
            _isDirty = true;

            if (ShouldPersist(forcePersist))
            {
                await _persistentState.WriteStateAsync();
                var entityId = this.GetPrimaryKeyString();
                if (forcePersist || _options.PersistencePolicy.Mode != PersistenceMode.EveryCall)
                    _logger.PersistenceForced(entityId, _requestsSinceLastSave);
                ResetPersistenceTracking();
            }
            else if (_options.PersistencePolicy.Mode != PersistenceMode.EveryCall)
            {
                _logger.PersistenceDeferred(this.GetPrimaryKeyString(),
                    _requestsSinceLastSave, _options.PersistencePolicy.Mode.ToString());
            }
        }

        private void ResetPersistenceTracking()
        {
            _requestsSinceLastSave = 0;
            _lastSaveTime = DateTime.UtcNow;
            _isDirty = false;
        }

        private void PersistRandomBytes()
        {
            if (_provider == null) return;
            var state = _persistentState.State;
            state.ServerRandomBytes = _provider.GetServerRandomBytes();
            state.OptimisticRandomBytes = _provider.GetOptimisticRandomBytes();
        }

        private async Task DistributeBroadcasts(List<EntityBroadcast> broadcasts, long operationSequence, string? excludePlayerId)
        {
            var entityId = this.GetPrimaryKeyString();
            var subscriberCount = _subscriberRefs.Count;

            _logger.DistributingBroadcasts(entityId, operationSequence, broadcasts.Count, subscriberCount, excludePlayerId ?? "none");

            foreach (var broadcast in broadcasts)
            {
                var sentCount = 0;
                foreach (var (playerId, sessionManager) in _subscriberRefs)
                {
                    if (excludePlayerId != null && playerId == excludePlayerId)
                    {
                        _logger.SkippingExcludedSubscriber(playerId);
                        continue;
                    }

                    try
                    {
                        _logger.SendingBroadcast(playerId, entityId, operationSequence, broadcast.ServiceName, broadcast.MethodName);
                        await sessionManager.ReceiveBroadcastAsync(entityId, broadcast, operationSequence);
                        sentCount++;
                    }
                    catch (Exception ex)
                    {
                        _logger.ErrorBroadcasting(ex, playerId);
                    }
                }

                _logger.BroadcastSent(sentCount, subscriberCount, broadcast.ServiceName, broadcast.MethodName);
            }
        }
    }
}
