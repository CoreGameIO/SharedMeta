using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Session;

namespace SharedMeta.Server.Core.Transport
{
    /// <summary>
    /// Handler for meta operations on a single connection.
    /// Knows about Orleans grains and ISessionManager.
    /// Transport-agnostic - receives IBroadcastSender for sending broadcasts.
    /// </summary>
    public class MetaConnectionHandler : IMetaConnectionHandler, ISessionObserver
    {
        private readonly string _connectionId;
        private readonly IGrainFactory _grainFactory;
        private readonly IEntityGrainResolver _entityGrainResolver;
        private readonly IBroadcastSender _broadcastSender;
        private readonly SignatureValidator? _signatureValidator;
        private readonly ILogger _logger;
        private ISessionObserver? _observerRef;
        private Timer? _observerRenewalTimer;
        private static readonly TimeSpan ObserverRenewalInterval = TimeSpan.FromSeconds(60);

        public string PlayerId { get; private set; } = string.Empty;
        public Guid SessionId { get; private set; }
        public bool IsSessionConnected => PlayerId.Length > 0;

        public MetaConnectionHandler(
            string connectionId,
            IGrainFactory grainFactory,
            IEntityGrainResolver entityGrainResolver,
            IBroadcastSender broadcastSender,
            ILogger logger,
            SignatureValidator? signatureValidator = null)
        {
            _connectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _broadcastSender = broadcastSender ?? throw new ArgumentNullException(nameof(broadcastSender));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _signatureValidator = signatureValidator;
        }

        #region IMetaConnectionHandler

        public async Task<SessionConnectResponse> SessionConnectAsync(SessionConnectRequest request)
        {
            try
            {
                if (string.IsNullOrEmpty(request.PlayerId))
                {
                    return new SessionConnectResponse { Success = false, Error = "PlayerId is required" };
                }

                PlayerId = request.PlayerId;

                var grain = _grainFactory.GetGrain<ISessionManager>(request.PlayerId);
                var result = await grain.ConnectAsync(request.SessionId ?? Guid.Empty, request.LastAcknowledgedSequence);

                if (result.Success)
                {
                    SessionId = result.SessionId;

                    // Clear queued data from previous session
                    _broadcastSender.Reset();

                    // Register this handler as observer for async broadcasts
                    _observerRef = _grainFactory.CreateObjectReference<ISessionObserver>(this);
                    await grain.SetObserverAsync(_observerRef);

                    // Start periodic observer renewal to keep subscription alive
                    _observerRenewalTimer?.Dispose();
                    _observerRenewalTimer = new Timer(
                        _ => _ = RenewObserverAsync(),
                        null,
                        ObserverRenewalInterval,
                        ObserverRenewalInterval);
                }
                else
                {
                    PlayerId = string.Empty; // Clear on failure
                }

                _logger.HandlerSessionConnect(request.PlayerId, result.Success, result.IsNewSession);

                // Validate method signatures if provided
                List<string>? signatureMismatches = null;
                if (request.MethodSignatures != null && _signatureValidator != null)
                {
                    signatureMismatches = _signatureValidator(request.MethodSignatures);
                    if (signatureMismatches != null)
                    {
                        _logger.SignatureMismatches(request.PlayerId, string.Join("; ", signatureMismatches));
                    }
                }

                return new SessionConnectResponse
                {
                    Success = result.Success,
                    Error = result.Error,
                    SessionId = result.SessionId,
                    IsNewSession = result.IsNewSession,
                    MissedPackets = result.MissedPackets,
                    SignatureMismatches = signatureMismatches,
                    ServerTimeTicks = result.ServerTimeTicks,
                    ResubscribedEntities = result.ResubscribedEntities?.Select(e => new ResubscribedEntityInfo
                    {
                        EntityId = e.EntityId,
                        StateBytes = e.StateBytes,
                        EntitySequenceNumber = e.EntitySequenceNumber,
                        OptimisticRandomBytes = e.OptimisticRandomBytes,
                        ConfigMajorVersion = e.ConfigVersion.Major,
                        ConfigMinorVersion = e.ConfigVersion.Minor
                    }).ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.HandlerSessionConnectError(ex);
                return new SessionConnectResponse { Success = false, Error = ex.Message };
            }
        }

        public async Task<SubscribeResponse> SubscribeAsync(SubscribeRequest request)
        {
            try
            {
                EnsureSessionConnected();

                if (string.IsNullOrEmpty(request.EntityId))
                {
                    return new SubscribeResponse { Success = false, Error = "EntityId is required" };
                }

                if (string.IsNullOrEmpty(request.StateTypeName))
                {
                    return new SubscribeResponse { Success = false, Error = "StateTypeName is required" };
                }

                var grain = _grainFactory.GetGrain<ISessionManager>(PlayerId);
                var result = await grain.SubscribeToEntityAsync(request.EntityId, request.StateTypeName);

                _logger.HandlerSubscribe(PlayerId!, request.EntityId, result.Success);

                return new SubscribeResponse
                {
                    Success = result.Success,
                    Error = result.Error,
                    StateBytes = result.StateBytes ?? Array.Empty<byte>(),
                    EntitySequenceNumber = result.EntitySequenceNumber,
                    OptimisticRandomBytes = result.OptimisticRandomBytes,
                    ConfigMajorVersion = result.ConfigVersion.Major,
                    ConfigMinorVersion = result.ConfigVersion.Minor
                };
            }
            catch (Exception ex)
            {
                _logger.HandlerSubscribeError(ex);
                return new SubscribeResponse { Success = false, Error = ex.Message };
            }
        }

        public async Task<UnsubscribeResponse> UnsubscribeAsync(UnsubscribeRequest request)
        {
            try
            {
                EnsureSessionConnected();

                if (!string.IsNullOrEmpty(request.EntityId))
                {
                    var grain = _grainFactory.GetGrain<ISessionManager>(PlayerId);
                    await grain.UnsubscribeFromEntityAsync(request.EntityId);
                }

                _logger.HandlerUnsubscribe(PlayerId!, request.EntityId);

                return new UnsubscribeResponse { Success = true };
            }
            catch (Exception ex)
            {
                _logger.HandlerUnsubscribeError(ex);
                return new UnsubscribeResponse { Success = false, Error = ex.Message };
            }
        }

        public async Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
        {
            try
            {
                EnsureSessionConnected();

                if (string.IsNullOrEmpty(request.EntityId))
                {
                    return SessionResponse.ForError("EntityId is required");
                }

                var call = new RpcCall
                {
                    ServiceName = request.ServiceName,
                    MethodName = request.MethodName,
                    CallerId = PlayerId,
                    Payload = request.Payload,
                    IsCrossOptimistic = request.IsCrossOptimistic,
                    ServerTimeTicks = request.ServerTimeTicks
                };

                var grain = _grainFactory.GetGrain<ISessionManager>(PlayerId);

                return await grain.SendToEntityAsync(
                    request.EntityId,
                    request.RequestId,
                    call,
                    request.LastAcknowledgedSequence,
                    SessionId);
            }
            catch (Exception ex)
            {
                _logger.HandlerRpcCallError(ex);
                return SessionResponse.ForError(ex.Message);
            }
        }

        public async Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request)
        {
            try
            {
                EnsureSessionConnected();

                if (string.IsNullOrEmpty(request.EntityId))
                    return new QueryCallResponse { Error = "EntityId is required" };

                if (string.IsNullOrEmpty(request.ServiceName))
                    return new QueryCallResponse { Error = "ServiceName is required" };

                var call = new RpcCall
                {
                    ServiceName = request.ServiceName,
                    MethodName = request.MethodName,
                    CallerId = PlayerId,
                    Payload = request.Payload,
                    ServerTimeTicks = DateTime.UtcNow.Ticks
                };

                var grain = _grainFactory.GetGrain<ISessionManager>(PlayerId);
                return await grain.QueryEntityAsync(request.EntityId, request.ServiceName, call);
            }
            catch (Exception ex)
            {
                _logger.HandlerRpcCallError(ex);
                return new QueryCallResponse { Error = ex.Message };
            }
        }

        public async Task<AcknowledgeResponse> AcknowledgeSequenceAsync(AcknowledgeRequest request)
        {
            try
            {
                EnsureSessionConnected();

                var grain = _grainFactory.GetGrain<ISessionManager>(PlayerId);
                await grain.AcknowledgeSequenceAsync(request.SequenceNumber);

                return new AcknowledgeResponse { Success = true };
            }
            catch (Exception ex)
            {
                _logger.HandlerAcknowledgeError(ex);
                return new AcknowledgeResponse { Success = false, Error = ex.Message };
            }
        }

        public async Task GracefulDisconnectAsync()
        {
            _observerRenewalTimer?.Dispose();
            _observerRenewalTimer = null;

            if (PlayerId != null)
            {
                _logger.HandlerGracefulDisconnect(_connectionId, PlayerId);

                try
                {
                    var grain = _grainFactory.GetGrain<ISessionManager>(PlayerId);
                    await grain.GracefulDisconnectAsync();
                }
                catch (Exception ex)
                {
                    _logger.HandlerGracefulDisconnectError(ex);
                }
            }
        }

        public async Task OnDisconnectedAsync()
        {
            _observerRenewalTimer?.Dispose();
            _observerRenewalTimer = null;

            if (PlayerId != null)
            {
                _logger.HandlerDisconnected(_connectionId, PlayerId);

                try
                {
                    var grain = _grainFactory.GetGrain<ISessionManager>(PlayerId);
                    await grain.OnTransportDisconnectedAsync();
                }
                catch (Exception ex)
                {
                    _logger.ErrorClearingObserver(ex);
                }
            }
        }

        private async Task RenewObserverAsync()
        {
            if (PlayerId == null || _observerRef == null) return;
            try
            {
                var grain = _grainFactory.GetGrain<ISessionManager>(PlayerId);
                await grain.SetObserverAsync(_observerRef);
            }
            catch (Exception ex)
            {
                _logger.ObserverRenewalFailed(ex);
            }
        }

        #endregion

        #region ISessionObserver (called by grain, [OneWay] fire-and-forget)

        public Task OnBatch(SessionResponse response)
        {
            _logger.ObserverOnBatch(PlayerId, response.SequenceNumber, response.Operations.Count);

            _broadcastSender.SendBroadcast(response);
            return Task.CompletedTask;
        }

        public Task OnEntityDeactivating(string entityId)
        {
            _broadcastSender.SendEntityDeactivating(entityId);
            return Task.CompletedTask;
        }

        public Task OnSessionTerminated(string reason)
        {
            _broadcastSender.SendSessionTerminated(reason);
            return Task.CompletedTask;
        }

        #endregion

        private void EnsureSessionConnected()
        {
            if (!IsSessionConnected)
            {
                throw new InvalidOperationException("Session not connected. Call SessionConnect first.");
            }
        }

        public void Dispose()
        {
            _observerRenewalTimer?.Dispose();
            _observerRenewalTimer = null;
            // Remaining cleanup is handled by OnDisconnectedAsync which is called from MetaHub.OnDisconnectedAsync
        }
    }

    /// <summary>
    /// Factory for creating MetaConnectionHandler instances.
    /// </summary>
    public class MetaConnectionHandlerFactory : IMetaConnectionHandlerFactory
    {
        private readonly IGrainFactory _grainFactory;
        private readonly IEntityGrainResolver _entityGrainResolver;
        private readonly ILoggerFactory _loggerFactory;
        private readonly SignatureValidator? _signatureValidator;

        public MetaConnectionHandlerFactory(
            IGrainFactory grainFactory,
            IEntityGrainResolver entityGrainResolver,
            ILoggerFactory loggerFactory,
            SignatureValidator? signatureValidator = null)
        {
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _signatureValidator = signatureValidator;
        }

        public IMetaConnectionHandler Create(string connectionId, IBroadcastSender broadcastSender)
        {
            var logger = _loggerFactory.CreateLogger<MetaConnectionHandler>();
            return new MetaConnectionHandler(connectionId, _grainFactory, _entityGrainResolver, broadcastSender, logger, _signatureValidator);
        }
    }
}
