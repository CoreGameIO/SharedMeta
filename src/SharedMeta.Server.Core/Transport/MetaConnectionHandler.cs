using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Orleans;
using SharedMeta.Core;
using SharedMeta.Core.Packets;
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
        private readonly ILogger _logger;
        private readonly MetaTransportOptions? _transportOptions;
        private readonly ClientVersionPolicy? _versionPolicy;
        private readonly IMetaSerializer? _serializer;
        private readonly SharedMeta.Core.Patch.IPatchSchemaRegistry? _schemaRegistry;
        /// <summary>
        /// Silo-local cache fronting the cluster's client-signature directory. Null when
        /// the host hasn't called <c>AddSharedMetaClientSignatureRegistry()</c> — in that
        /// mode every connection is treated as "negotiation disabled."
        /// </summary>
        private readonly Session.IClientSignatureRegistry? _signatureRegistry;
        private ISessionObserver? _observerRef;
        private Timer? _observerRenewalTimer;
        private static readonly TimeSpan ObserverRenewalInterval = TimeSpan.FromSeconds(60);

        // Cached session-manager grain reference. PlayerId is fixed for the connection
        // lifetime, so resolving once at SessionConnect avoids the per-RPC
        // GrainFactory.GetGrain<T>(playerId) path — RuntimeTypeNameParser + TypeRewriter
        // run on every call, allocating ~6 MB/s of
        // String + Char[] under 25K RPS in profiling).
        private ISessionManager? _sessionManagerGrain;
        private ISessionManager SessionManagerGrainOrThrow =>
            _sessionManagerGrain ?? throw new InvalidOperationException(
                "Session manager grain not resolved. SessionConnect must succeed first.");

        // Per-connection cache of recent server patches keyed by (entityId, service, method).
        // Stores the most recent patch per (entity,service,method) — used for desync follow-up.
        private readonly LinkedList<CachedServerPatch> _patchCache = new();
        private readonly object _patchCacheLock = new();

        private class CachedServerPatch
        {
            public string EntityId { get; set; } = "";
            public string ServiceName { get; set; } = "";
            public string MethodName { get; set; } = "";
            public ReadOnlyMemory<byte> PatchBytes { get; set; }
        }

        public string PlayerId { get; private set; } = string.Empty;
        public Guid SessionId { get; private set; }
        public bool IsSessionConnected => PlayerId.Length > 0;
        public bool DeepDesyncRequested { get; private set; }
        /// <summary>Client app version from SessionConnect. Passed to entity grain during subscribe
        /// so it can resolve the correct [MetaConfigVersion] branch for this client.</summary>
        private string? _clientVersion;

        /// <summary>
        /// 0.24.0+ Session-scoped annotation. Populated from
        /// <see cref="Session.IClientSignatureRegistry"/> at SessionConnect (phase-1) or
        /// RegisterClientSignature (phase-2). Consulted on every RpcCall as a back-stop:
        /// even forged clients that bypassed their local <c>CapabilitiesGate</c> get rejected
        /// here via O(1) <c>Statuses[methodId]</c> lookup.
        /// </summary>
        private SharedMeta.Core.Transport.ClientSignatureAnnotated? _clientAnnotated;

        /// <summary>
        /// Negotiated signature hash for this connection. Captured from
        /// <c>SessionConnectRequest.ClientSignatureHash</c> on phase-1 (or zero if absent),
        /// and overwritten by <c>RegisterClientSignatureRequest.Signature.Hash</c> after
        /// phase-2 registration. Forwarded into every <c>SubscribeToEntityAsync</c> call so
        /// the target <c>EntityGrain</c> can resolve the caller's compatibility capabilities
        /// via <c>IClientSignatureRegistry</c> locally — replaces the 0.22.x push-the-list
        /// flow into <c>SessionManagerGrain._clientCapabilities</c>.
        /// </summary>
        private ulong _clientSignatureHash;

        // 0.24.0+ Reverse lookup MethodId → ServerMethodEntry for resolving (service, method,
        // version) from the wire's ushort identifier. Required since RpcCallRequest /
        // SignalCallRequest / QueryCallRequest no longer carry the strings. Null only when
        // host hasn't wired the signature in DI (legacy / pure-test setup).
        private readonly SharedMeta.Core.Transport.MetaServerSignature? _serverSignature;

        public MetaConnectionHandler(
            string connectionId,
            IGrainFactory grainFactory,
            IEntityGrainResolver entityGrainResolver,
            IBroadcastSender broadcastSender,
            ILogger logger,
            MetaTransportOptions? transportOptions = null,
            IMetaSerializer? serializer = null,
            SharedMeta.Core.Patch.IPatchSchemaRegistry? schemaRegistry = null,
            ClientVersionPolicy? versionPolicy = null,
            Session.IClientSignatureRegistry? signatureRegistry = null,
            SharedMeta.Core.Transport.MetaServerSignature? serverSignature = null)
        {
            _connectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _broadcastSender = broadcastSender ?? throw new ArgumentNullException(nameof(broadcastSender));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _transportOptions = transportOptions;
            _serializer = serializer;
            _schemaRegistry = schemaRegistry;
            _versionPolicy = versionPolicy;
            _signatureRegistry = signatureRegistry;
            _serverSignature = serverSignature;
        }

        /// <summary>
        /// Resolve a server-side <see cref="ushort"/> MethodId to its <c>(ServiceName, Alias, Version)</c>
        /// triple via the registered <see cref="MetaServerSignature"/>. Returns <c>(null, null, 0)</c>
        /// when no signature is wired or the id is unknown — callers fall back to a methodId-only
        /// diagnostic string.
        /// </summary>
        private (string? Service, string? Method, int Version) ResolveServerMethodEntry(ushort serverMethodId)
        {
            var entry = _serverSignature?.GetMethodEntry(serverMethodId);
            if (entry == null) return (null, null, 0);
            return (entry.ServiceName, entry.Alias, entry.Version);
        }

        #region IMetaConnectionHandler

        public async Task<SessionConnectResponse> SessionConnectAsync(SessionConnectRequest request)
        {
            // Bracket the whole handshake (version-gate, sig lookup, session create/resume).
            // The measurement struct records duration + result tag + active-session gauge bump
            // on Dispose; result defaults to "rejected" so an early-return path is tagged
            // correctly without an explicit MarkResult call.
            using var __sc = SharedMeta.Server.Core.Telemetry.SessionConnectMeasurement.Start(request.PlayerId ?? "<null>");
            SessionConnectResponse? __scResponse = null;
            try
            {
                if (string.IsNullOrEmpty(request.PlayerId))
                {
                    return new SessionConnectResponse { Success = false, Error = "PlayerId is required" };
                }

                // 0.24.0+ Optionally reject Hash=0 (legacy / opt-out clients). Hosts that have
                // moved every supported client to a generated ClientSignature flip this on so
                // the wire packets carry MethodId without needing string-fallback resolution
                // on every cross-grain hop.
                if (_transportOptions?.RequireClientSignature == true && request.ClientSignatureHash == 0)
                {
                    return new SessionConnectResponse
                    {
                        Success = false,
                        Error = "ClientSignature is required (server rejects Hash=0). Wire the generated " +
                                "MetaClientSignature on MetaClientOptions.ClientSignature."
                    };
                }

                // Version gate — policy encapsulates TTL caching, grain refresh, and parsing.
                if (_versionPolicy != null)
                {
                    var versionResult = await _versionPolicy.ValidateAsync(request.ClientVersion);
                    _logger.LogTrace(
                        "[Handler] SessionConnect version check player={PlayerId} client={ClientVersion} server={ServerVersion} min={MinClientVersion} allowed={Allowed} error={Error}",
                        request.PlayerId,
                        request.ClientVersion ?? "<null>",
                        versionResult.ServerVersion ?? "<null>",
                        versionResult.MinClientVersion ?? "<null>",
                        versionResult.IsAllowed,
                        versionResult.Error ?? "<none>");
                    if (!versionResult.IsAllowed)
                    {
                        _logger.LogWarning("[Handler] Version rejected for {PlayerId}: {Error}",
                            request.PlayerId, versionResult.Error);
                        return new SessionConnectResponse
                        {
                            Success = false,
                            Error = versionResult.Error,
                            ServerVersion = versionResult.ServerVersion,
                            MinClientVersion = versionResult.MinClientVersion,
                            MaxClientVersion = versionResult.MaxClientVersion
                        };
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "[Handler] SessionConnect: ClientVersionPolicy is null — version gate is OFF. " +
                        "Check that ConfigureMeta() / AddMetaServices() was called so the policy gets registered.");
                }

                // Per-player version gate: reject downgrade by Major or Minor.
                // Patch-only changes are allowed (patch = content update, no schema impact).
                if (!string.IsNullOrEmpty(request.ClientVersion) && !string.IsNullOrEmpty(request.PlayerId))
                {
                    var versionGrain = _grainFactory.GetGrain<IPlayerVersionGrain>(request.PlayerId);
                    var versionCheck = await versionGrain.RecordClientVersionAsync(request.ClientVersion);
                    if (!versionCheck.Accepted)
                    {
                        _logger.LogWarning(
                            "[Handler] Player version downgrade rejected: PlayerId={PlayerId} client={ClientVersion} max={MaxVersion}",
                            request.PlayerId, request.ClientVersion, versionCheck.MaxVersion);
                        return new SessionConnectResponse
                        {
                            Success = false,
                            Error = $"Your profile was last used on a newer client version " +
                                    $"({versionCheck.MaxVersion}). Please upgrade your app to continue.",
                            MinClientVersion = versionCheck.MaxVersion
                        };
                    }
                }

                PlayerId = request.PlayerId;
                _clientVersion = request.ClientVersion; // stored for per-client config resolution at subscribe time

                // Cache the session-manager grain reference for the lifetime of this connection.
                // Subsequent RPCs reuse it via SessionManagerGrainOrThrow without going through
                // GrainFactory.GetGrain (which reparses the grain type name on every call).
                var grain = _grainFactory.GetGrain<ISessionManager>(request.PlayerId);
                _sessionManagerGrain = grain;

                // 0.24.0+ Honor explicit SessionConnectMode. StartNew → allocate a fresh
                // SessionId (handler-side, grain doesn't synthesize ids). Resume → forward
                // the client-supplied SessionId for grain-side match; on mismatch the grain
                // returns SessionUnknown, which we propagate as wire FailureReason so the
                // client fires its IMetaSessionRecoveryHandler.
                Guid effectiveSessionId = request.Mode == SessionConnectMode.StartNew
                    ? Guid.NewGuid()
                    : request.SessionId ?? Guid.Empty;
                var result = await grain.ConnectAsync(
                    effectiveSessionId,
                    request.LastAcknowledgedSequence,
                    request.Mode,
                    request.LastCompletedRequestId,
                    request.ClaimedSubscriptions,
                    request.ClientVersion,
                    request.ClientSignatureHash);

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

                // Compatibility negotiation: non-zero ClientSignatureHash means the client
                // opted in. Registry lookup is silo-cache-first → cluster directory on miss.
                // Unknown signature ships NeedsSignatureRegistration so the client follows up
                // with phase-2 (RegisterClientSignature). Hash == 0 = legacy / opted-out =
                // no capabilities attached (server treats client as fully compatible).
                bool needsSignatureRegistration = false;
                ClientSignatureAnnotated? annotated = null;
                var serverSignatureHash = _signatureRegistry?.ServerSignatureHash ?? 0UL;
                if (request.ClientSignatureHash != 0 && _signatureRegistry != null && result.Success)
                {
                    annotated = await _signatureRegistry.TryGetAnnotatedAsync(request.ClientSignatureHash);
                    needsSignatureRegistration = annotated == null;
                    if (annotated != null)
                    {
                        var (rej, fp) = CountStatuses(annotated.Statuses);
                        _logger.HandshakeAnnotationFromCache(request.PlayerId, request.ClientSignatureHash, serverSignatureHash, annotated.Statuses.Length, rej, fp);
                    }
                    else
                    {
                        _logger.HandshakeNeedsRegistration(request.PlayerId, request.ClientSignatureHash, serverSignatureHash);
                    }
                }
                // Stash for the per-RPC back-stop. Null annotation (negotiation disabled, or
                // pending phase-2) means the back-stop runs in pass-through mode.
                _clientAnnotated = annotated;

                // Stash the hash for forwarding through SubscribeToEntityAsync. EntityGrain
                // resolves its own capabilities from IClientSignatureRegistry — no caps blob
                // pushed into SessionManagerGrain.
                _clientSignatureHash = request.ClientSignatureHash;

                __scResponse = new SessionConnectResponse
                {
                    Success = result.Success,
                    Error = result.Error,
                    SessionId = result.SessionId,
                    IsNewSession = result.IsNewSession,
                    MissedPackets = result.MissedPackets,
                    ServerTimeTicks = result.ServerTimeTicks,
                    ServerVersion = _versionPolicy?.ServerVersion ?? _transportOptions?.ServerVersion,
                    NeedsSignatureRegistration = needsSignatureRegistration,
                    ServerSignatureHash = serverSignatureHash,
                    Annotated = annotated,
                    FailureReason = result.FailureReason,
                    // 0.24.0+ Server-driven ResubscribedEntities replaced by client-driven
                    // Subscriptions[] — grain produces these directly from the per-claim
                    // ReclaimSubscriptionAsync verdicts; no further DTO mapping needed.
                    Subscriptions = result.Subscriptions,
                };
                return __scResponse;
            }
            catch (Exception ex)
            {
                _logger.HandlerSessionConnectError(ex);
                __scResponse = new SessionConnectResponse { Success = false, Error = ex.Message };
                return __scResponse;
            }
            finally
            {
                __sc.MarkResult(__scResponse?.Success == true ? "success"
                    : __scResponse?.NeedsSignatureRegistration == true ? "needs_signature_registration"
                    : "rejected");
            }
        }

        public async Task<RegisterClientSignatureResponse> RegisterClientSignatureAsync(RegisterClientSignatureRequest request)
        {
            // The session must be established and the SessionId must match — phase-2 belongs
            // to the same connection that received NeedsSignatureRegistration in phase-1.
            // We don't reject mismatches harshly (no enforced session state machine yet) but
            // log so a misrouted register call is at least observable.
            if (request.SessionId != SessionId)
            {
                _logger.LogWarning(
                    "[Handler] RegisterClientSignature with stale SessionId — request={RequestSessionId} handler={HandlerSessionId}",
                    request.SessionId, SessionId);
            }

            if (_signatureRegistry == null)
            {
                // Host hasn't wired the registry. Fall through to default no-op capabilities so
                // a client that opted in still gets a coherent reply (and won't retry forever
                // because of an exception bubble).
                return new RegisterClientSignatureResponse
                {
                    Success = true,
                    Annotated = null,
                };
            }

            try
            {
                var annotated = await _signatureRegistry.RegisterAsync(request.Signature);
                _clientAnnotated = annotated;
                // Update the cached signature hash so subsequent SubscribeToEntityAsync calls
                // forward the freshly-registered hash to EntityGrain.
                _clientSignatureHash = request.Signature.SignatureHash;
                var (rej, fp) = CountStatuses(annotated.Statuses);
                int serverOnly = CountServerOnly(annotated.ServerToClient);
                _logger.HandshakeSignatureRegistered(PlayerId ?? "<unknown>",
                    request.Signature.SignatureHash, request.Signature.KnownMethods.Count,
                    annotated.ServerSignatureHash, rej, fp, serverOnly);
                return new RegisterClientSignatureResponse
                {
                    Success = true,
                    Annotated = annotated,
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[Handler] RegisterClientSignature failed for hash {Hash}",
                    request.Signature.SignatureHash);
                return new RegisterClientSignatureResponse
                {
                    Success = false,
                    Error = ex.Message,
                    Annotated = null,
                };
            }
        }

        public async Task<SubscribeResponse> SubscribeAsync(SubscribeRequest request)
        {
            try
            {
                if (!TryEnsureSessionConnected("Subscribe"))
                    return new SubscribeResponse { Success = false, Error = "Session not connected — please re-handshake." };

                if (string.IsNullOrEmpty(request.EntityId))
                {
                    return new SubscribeResponse { Success = false, Error = "EntityId is required" };
                }

                if (string.IsNullOrEmpty(request.StateTypeName))
                {
                    return new SubscribeResponse { Success = false, Error = "StateTypeName is required" };
                }

                var grain = SessionManagerGrainOrThrow;
                var result = await grain.SubscribeToEntityAsync(request.EntityId, request.StateTypeName, _clientVersion, _clientSignatureHash);

                _logger.HandlerSubscribe(PlayerId!, request.EntityId, result.Success);

                return new SubscribeResponse
                {
                    Success = result.Success,
                    Error = result.Error,
                    // ROM → byte[] on the wire boundary (SubscribeResponse.StateBytes stays
                    // byte[] for client compatibility; flipping to ROM is a separate Unity-side
                    // change). The pool-rented buffer behind `result.StateBytes` is released
                    // by the producing EntityGrain at its next HandleCallAsync entry.
                    StateBytes = result.StateBytes.IsEmpty ? Array.Empty<byte>() : result.StateBytes.ToArray(),
                    EntitySequenceNumber = result.EntitySequenceNumber,
                    OptimisticRandomBytes = result.OptimisticRandomBytes,
                    NamedRandomsBytes = result.NamedRandomsBytes,
                    ConfigMajorVersion = result.ConfigVersion.Major,
                    ConfigMinorVersion = result.ConfigVersion.Minor,
                    ConfigPatchVersion = result.ConfigVersion.Patch,
                    FeatureRequirement = result.FeatureRequirement,
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
                if (!TryEnsureSessionConnected("Unsubscribe"))
                    return new UnsubscribeResponse { Success = false, Error = "Session not connected — please re-handshake." };

                if (!string.IsNullOrEmpty(request.EntityId))
                {
                    var grain = SessionManagerGrainOrThrow;
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
            // 0.24.0+ End-to-end server-side timing wrapped in a using-disposable that
            // records the histogram on scope exit. Tagged by ushort MethodId only — no
            // name resolution on the hot path. MarkRejected / MarkError set the result tag.
            // Diff against RpcDuration still surfaces queue / hop overhead.
            using var __m = SharedMeta.Server.Core.Telemetry.ServerRpcTotalMeasurement.Start(request.MethodId);
            try
            {
                if (!TryEnsureSessionConnected("RpcCall"))
                {
                    __m.MarkRejected();
                    return SessionResponse.ForError("Session not connected — please re-handshake.");
                }

                if (string.IsNullOrEmpty(request.EntityId))
                {
                    __m.MarkBadRequest();
                    return SessionResponse.ForError("EntityId is required");
                }

                // 0.24.0+ Translate client's MethodId → server's via per-signature mapping.
                // MethodId=0 is a real client id (alphabetical-first slot in the per-assembly
                // GameMethodIds table) so we must always run translation — no special-case
                // bypass for 0. Sentinel ushort.MaxValue in the map means the signature
                // flagged this method as not callable by this client (e.g. GenerateClientApi
                // = false, ArgHash mismatch, unknown to server). When the client never
                // negotiated a signature (_clientSignatureHash=0) calls are rejected — the
                // wire no longer carries enough info to dispatch without the per-signature map.
                ushort serverMethodId;
                if (_clientSignatureHash == 0 || _signatureRegistry == null)
                {
                    __m.MarkRejected();
                    return SessionResponse.ForError(
                        "Cannot dispatch RPC: client signature not negotiated. Call RegisterClientSignatureAsync first.");
                }
                {
                    var c2s = await _signatureRegistry.TryGetClientToServerMapAsync(_clientSignatureHash);
                    if (c2s == null || request.MethodId >= c2s.Length)
                    {
                        __m.MarkRejected();
                        return SessionResponse.ForError(
                            $"Method id {request.MethodId} is out of range for client signature.");
                    }
                    var mapped = c2s[request.MethodId];
                    if (mapped == ushort.MaxValue)
                    {
                        __m.MarkRejected();
                        return SessionResponse.ForError(
                            $"Method id {request.MethodId} is not callable by this client.");
                    }
                    serverMethodId = mapped;
                }

                // Resolve once for capabilities back-stop + patch cache key. Returns (null,
                // null, 0) when signature isn't wired. Not used as a telemetry tag anymore —
                // ServerRpcTotalMeasurement is methodId-only.
                var (resolvedService, resolvedMethod, resolvedVersion) = ResolveServerMethodEntry(serverMethodId);

                // Server-side back-stop: even if the client bypassed its local CapabilitiesGate,
                // we enforce rejection here from the cached annotation. Lookup is O(1) — the
                // wire's clientMethodId indexes directly into the Statuses array. Force-ServerPatch
                // is intentionally NOT enforced server-side — the server executes whatever the
                // client declared, since downgrading the mode mid-flight would change semantics
                // for a well-behaved client that already complied at the gate.
                if (SharedMeta.Core.Transport.CapabilitiesGate.IsRejected(_clientAnnotated, request.MethodId))
                {
                    __m.MarkRejected();
                    return SessionResponse.ForError(
                        $"Method '{resolvedService}.{resolvedMethod}' (v{resolvedVersion}) is rejected for this client signature.");
                }

                var call = new RpcCall
                {
                    MethodId = serverMethodId,
                    CallerId = PlayerId,
                    CallerClientVersion = _clientVersion,
                    Payload = request.Payload,
                    IsCrossOptimistic = request.IsCrossOptimistic,
                    ServerTimeTicks = request.ServerTimeTicks,
                    DeepDesyncRequested = DeepDesyncRequested,
                    Debug = request.Debug  // 0.26.6+ piggybacked PayloadDebug (deep-state CRCs)
                };

                var grain = SessionManagerGrainOrThrow;

                var response = await grain.SendToEntityAsync(
                    request.EntityId,
                    request.RequestId,
                    call,
                    request.LastAcknowledgedSequence,
                    SessionId).ConfigureAwait(false);

                // Cache server patch for desync reporting (if enabled). OpBytes is the
                // pre-serialized MetaOperation; we deserialize lazily only for this
                // diagnostic path — production has DesyncReporting off.
                if (_transportOptions?.DesyncReportingEnabled == true && response.Operations != null && _serializer != null
                    && resolvedService != null && resolvedMethod != null)
                {
                    foreach (var op in response.Operations)
                    {
                        if (op.RequestId == request.RequestId && op.OpBytes.Length > 0)
                        {
                            var meta = _serializer.Unpack<MetaOperation>(op.OpBytes);
                            if (meta != null && !meta.PatchBytes.IsEmpty)
                            {
                                CachePatch(request.EntityId, resolvedService, resolvedMethod, meta.PatchBytes);
                            }
                            break;
                        }
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                __m.MarkError();
                _logger.HandlerRpcCallError(ex);
                return SessionResponse.ForError(ex.Message);
            }
        }

        public async Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request)
        {
            try
            {
                if (!TryEnsureSessionConnected("QueryCall"))
                    return new QueryCallResponse { Error = "Session not connected — please re-handshake." };

                if (string.IsNullOrEmpty(request.EntityId))
                    return new QueryCallResponse { Error = "EntityId is required" };

                // 0.24.0+ Translate client's MethodId → server's via per-signature mapping.
                // MethodId=0 is a real id, not a sentinel — translate unconditionally.
                if (_clientSignatureHash == 0 || _signatureRegistry == null)
                    return new QueryCallResponse { Error = "Cannot dispatch query: client signature not negotiated." };
                ushort serverMethodId;
                {
                    var c2s = await _signatureRegistry.TryGetClientToServerMapAsync(_clientSignatureHash);
                    if (c2s == null || request.MethodId >= c2s.Length)
                        return new QueryCallResponse { Error = $"Query method id {request.MethodId} is out of range for client signature." };
                    var mapped = c2s[request.MethodId];
                    if (mapped == ushort.MaxValue)
                        return new QueryCallResponse { Error = $"Query method id {request.MethodId} is not callable by this client." };
                    serverMethodId = mapped;
                }

                // Resolve service name for grain routing (`QueryEntityAsync` keys by it).
                var (resolvedService, _, _) = ResolveServerMethodEntry(serverMethodId);
                if (resolvedService == null)
                    return new QueryCallResponse { Error = $"Query method id {request.MethodId} is unknown on this server." };

                var call = new RpcCall
                {
                    MethodId = serverMethodId,
                    CallerId = PlayerId,
                    CallerClientVersion = _clientVersion,
                    Payload = request.Payload,
                    ServerTimeTicks = DateTime.UtcNow.Ticks
                };

                var grain = SessionManagerGrainOrThrow;
                return await grain.QueryEntityAsync(request.EntityId, resolvedService, call);
            }
            catch (Exception ex)
            {
                _logger.HandlerRpcCallError(ex);
                return new QueryCallResponse { Error = ex.Message };
            }
        }

        public async Task SignalCallAsync(SignalCallRequest request)
        {
            // Signal is fire-and-forget: validation errors are logged, never surfaced to caller.
            try
            {
                if (!TryEnsureSessionConnected("SignalCall")) return;

                if (string.IsNullOrEmpty(request.EntityId))
                {
                    _logger.LogWarning("[Handler] Signal rejected — missing EntityId");
                    return;
                }

                // 0.24.0+ Translate client's MethodId → server's via per-signature mapping.
                // MethodId=0 is a real id, not a sentinel — translate unconditionally.
                if (_clientSignatureHash == 0 || _signatureRegistry == null)
                {
                    _logger.LogWarning("[Handler] Signal rejected — client signature not negotiated");
                    return;
                }
                ushort serverMethodId;
                {
                    var c2s = await _signatureRegistry.TryGetClientToServerMapAsync(_clientSignatureHash);
                    if (c2s == null || request.MethodId >= c2s.Length)
                    {
                        _logger.LogWarning("[Handler] Signal rejected — method id {MethodId} out of range for client signature", request.MethodId);
                        return;
                    }
                    var mapped = c2s[request.MethodId];
                    if (mapped == ushort.MaxValue)
                    {
                        _logger.LogWarning("[Handler] Signal rejected — method id {MethodId} not callable by this client", request.MethodId);
                        return;
                    }
                    serverMethodId = mapped;
                }

                // Resolve service name for grain routing (`SignalEntityAsync` keys by it).
                var (resolvedService, _, _) = ResolveServerMethodEntry(serverMethodId);
                if (resolvedService == null)
                {
                    _logger.LogWarning("[Handler] Signal rejected — method id {MethodId} unknown on this server", request.MethodId);
                    return;
                }

                var call = new RpcCall
                {
                    MethodId = serverMethodId,
                    CallerId = PlayerId,
                    CallerClientVersion = _clientVersion,
                    Payload = request.Payload,
                    ServerTimeTicks = DateTime.UtcNow.Ticks
                };

                var grain = SessionManagerGrainOrThrow;
                await grain.SignalEntityAsync(request.EntityId, resolvedService, call);
            }
            catch (Exception ex)
            {
                _logger.HandlerRpcCallError(ex);
                // No error propagation — fire-and-forget contract.
            }
        }

        public async Task<AcknowledgeResponse> AcknowledgeSequenceAsync(AcknowledgeRequest request)
        {
            try
            {
                if (!TryEnsureSessionConnected("AcknowledgeSequence"))
                    return new AcknowledgeResponse { Success = false, Error = "Session not connected — please re-handshake." };

                var grain = SessionManagerGrainOrThrow;
                await grain.AcknowledgeSequenceAsync(request.SequenceNumber);

                return new AcknowledgeResponse { Success = true };
            }
            catch (Exception ex)
            {
                _logger.HandlerAcknowledgeError(ex);
                return new AcknowledgeResponse { Success = false, Error = ex.Message };
            }
        }

        public Task<DebugOptionsResponse> SetDebugOptionsAsync(DebugOptionsRequest request)
        {
            DeepDesyncRequested = request.DeepDesyncEnabled;
            _logger.LogDebug("[Handler] Deep desync {Status} for {PlayerId}",
                request.DeepDesyncEnabled ? "enabled" : "disabled", PlayerId);
            return Task.FromResult(new DebugOptionsResponse { Success = true });
        }

        public async Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request)
        {
            _logger.LogDebug("[Handler] SendDesyncReport kind={Kind} entity={Entity} {Service}.{Method} from {Player}",
                (DesyncMismatchKind)request.MismatchKind, request.EntityId, request.ServiceName, request.MethodName, PlayerId);
            try
            {
                if (_transportOptions?.DesyncReportingEnabled != true)
                    return new DesyncReportResponse { Status = "disabled" };

                if (!TryEnsureSessionConnected("DesyncReport"))
                    return new DesyncReportResponse { Status = "rejected", Error = "Session not connected — please re-handshake." };

                var kind = (DesyncMismatchKind)request.MismatchKind;
                // Backwards compat: legacy clients that only set ClientPatchBytes had no MismatchKind.
                if (kind == DesyncMismatchKind.None && request.ClientPatchBytes is { Length: > 0 })
                    kind = DesyncMismatchKind.Patch;

                var report = new SharedMeta.Core.Diagnostics.DeepDesyncReport
                {
                    PlayerId = PlayerId,
                    EntityId = request.EntityId,
                    ServiceName = request.ServiceName,
                    MethodName = request.MethodName,
                    Timestamp = DateTime.UtcNow,
                    ArgsBytes = request.ArgsBytes,
                    MismatchKind = (int)kind
                };
                int diffCount = 0;
                var textParts = new List<string>();

                // ── Patch mismatch ────────────────────────────────────────
                if ((kind & DesyncMismatchKind.Patch) != 0)
                {
                    if (_serializer == null)
                        return new DesyncReportResponse { Status = "error", Error = "Serializer not configured" };

                    var cachedServerPatch = LookupPatch(request.EntityId, request.ServiceName, request.MethodName);

                    // Either side may legitimately be empty: if one party mutated state and
                    // the other didn't, that IS the divergence we want to report.
                    SharedMeta.Core.Patch.PatchNode? serverNode = null;
                    SharedMeta.Core.Patch.PatchNode? clientNode = null;
                    if (!cachedServerPatch.IsEmpty)
                        serverNode = _serializer.Unpack<SharedMeta.Core.Patch.PatchNode>(cachedServerPatch);
                    if (request.ClientPatchBytes is { Length: > 0 })
                        clientNode = _serializer.Unpack<SharedMeta.Core.Patch.PatchNode>(request.ClientPatchBytes);

                    if (serverNode == null && clientNode == null)
                    {
                        _logger.LogWarning("[Handler] Desync report: empty patches on both sides for {EntityId}.{Service}.{Method}",
                            request.EntityId, request.ServiceName, request.MethodName);
                        return new DesyncReportResponse { Status = "empty-patches" };
                    }

                    var diffEntries = serverNode != null && clientNode != null
                        ? SharedMeta.Core.Patch.PatchNodeDiffer.Compare(serverNode, clientNode)
                        : new List<SharedMeta.Core.Patch.PatchDiffEntry>();

                    // Cold path (desync report): copy to byte[] for PatchCrc.Compute which still
                    // takes byte[]?. Keeping the cache as ROM avoids the copy on the happy path.
                    report.ServerPatchCrc = cachedServerPatch.IsEmpty ? 0u : SharedMeta.Core.Patch.PatchCrc.Compute(cachedServerPatch.ToArray());
                    report.ClientPatchCrc = request.ClientPatchBytes is { Length: > 0 } ? SharedMeta.Core.Patch.PatchCrc.Compute(request.ClientPatchBytes) : 0u;
                    report.ServerPatchBytes = cachedServerPatch.IsEmpty ? null : cachedServerPatch.ToArray();
                    report.ClientPatchBytes = request.ClientPatchBytes;
                    report.Diff = diffEntries;
                    diffCount = diffEntries.Count;

                    // Prefer schema-based JSON rendering when a schema is registered for this service.
                    var schema = _schemaRegistry?.GetByServiceName(request.ServiceName);
                    if (schema != null)
                    {
                        var jsonDiff = SharedMeta.Core.Patch.PatchTextRenderer.DiffToJson(serverNode, clientNode, schema, _serializer);
                        textParts.Add("PATCH:\n" + jsonDiff);
                    }
                    else
                    {
                        textParts.Add($"PATCH: {BuildTextDiff(diffEntries)}");
                    }
                }

                // ── Result mismatch ───────────────────────────────────────
                if ((kind & DesyncMismatchKind.Result) != 0)
                {
                    report.ServerResultBytes = request.ServerResultBytes;
                    report.LocalResultBytes = request.LocalResultBytes;
                    textParts.Add($"RESULT: server={BitConverter.ToString(request.ServerResultBytes)} local={BitConverter.ToString(request.LocalResultBytes)}");
                }

                // ── Random mismatch ───────────────────────────────────────
                if ((kind & DesyncMismatchKind.Random) != 0)
                {
                    report.ServerRandomDelta = request.ServerRandomDelta;
                    report.LocalRandomDelta = request.LocalRandomDelta;
                    textParts.Add($"RANDOM: server={request.ServerRandomDelta} local={request.LocalRandomDelta}");
                }

                report.TextDiff = string.Join("\n", textParts);

                var grain = _grainFactory.GetGrain<IDesyncReportGrain>(PlayerId);
                await grain.StoreReportAsync(report);

                LogDesync(report, diffCount);

                return new DesyncReportResponse { Status = "stored" };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[Handler] SendDesyncReportAsync error");
                return new DesyncReportResponse { Status = "error", Error = ex.Message };
            }
        }

        private void CachePatch(string entityId, string serviceName, string methodName, ReadOnlyMemory<byte> patchBytes)
        {
            var cacheSize = _transportOptions?.DesyncReportPatchCacheSize ?? 16;
            lock (_patchCacheLock)
            {
                // Remove existing entry for same (entity, service, method) — keep only newest
                var node = _patchCache.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (node.Value.EntityId == entityId
                        && node.Value.ServiceName == serviceName
                        && node.Value.MethodName == methodName)
                    {
                        _patchCache.Remove(node);
                    }
                    node = next;
                }
                _patchCache.AddLast(new CachedServerPatch
                {
                    EntityId = entityId,
                    ServiceName = serviceName,
                    MethodName = methodName,
                    PatchBytes = patchBytes
                });
                while (_patchCache.Count > cacheSize)
                    _patchCache.RemoveFirst();
            }
        }

        private ReadOnlyMemory<byte> LookupPatch(string entityId, string serviceName, string methodName)
        {
            lock (_patchCacheLock)
            {
                for (var node = _patchCache.Last; node != null; node = node.Previous)
                {
                    if (node.Value.EntityId == entityId
                        && node.Value.ServiceName == serviceName
                        && node.Value.MethodName == methodName)
                        return node.Value.PatchBytes;
                }
            }
            return default;
        }

        private void LogDesync(SharedMeta.Core.Diagnostics.DeepDesyncReport report, int diffCount)
        {
            var level = _transportOptions?.DesyncLogLevel ?? DesyncLogLevel.Warning;
            if (level == DesyncLogLevel.None) return;

            var kind = (DesyncMismatchKind)report.MismatchKind;
            switch (level)
            {
                case DesyncLogLevel.Warning:
                    _logger.LogWarning("[Desync] {Player} {Service}.{Method} kind={Kind} (patchDiffs={DiffCount})",
                        report.PlayerId, report.ServiceName, report.MethodName, kind, diffCount);
                    break;
                case DesyncLogLevel.Information:
                    _logger.LogInformation("[Desync] {Player} {Entity}/{Service}.{Method} kind={Kind}; patchDiffs={DiffCount} serverPatch={ServerLen}B clientPatch={ClientLen}B",
                        report.PlayerId, report.EntityId, report.ServiceName, report.MethodName, kind, diffCount,
                        report.ServerPatchBytes?.Length ?? 0, report.ClientPatchBytes?.Length ?? 0);
                    break;
                case DesyncLogLevel.Debug:
                    _logger.LogInformation("[Desync] {Player} {Entity}/{Service}.{Method} kind={Kind}\n{TextDiff}",
                        report.PlayerId, report.EntityId, report.ServiceName, report.MethodName, kind,
                        report.TextDiff);
                    break;
            }
        }

        private static string BuildTextDiff(List<SharedMeta.Core.Patch.PatchDiffEntry> entries)
        {
            if (entries.Count == 0) return "(no field-level differences detected)";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                sb.Append("field ").Append(e.FieldPath).Append(": ").Append(e.Type);
                if (e.ServerValue != null) sb.Append(" server=").Append(BitConverter.ToString(e.ServerValue));
                if (e.ClientValue != null) sb.Append(" client=").Append(BitConverter.ToString(e.ClientValue));
                if (i < entries.Count - 1) sb.AppendLine();
            }
            return sb.ToString();
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
                    var grain = SessionManagerGrainOrThrow;
                    // 0.24.0+ Pass SessionId so grain ignores stale graceful disconnects
                    // from superseded handlers (old client's late teardown after a fresh
                    // session has bound on the same player grain).
                    await grain.GracefulDisconnectAsync(SessionId);
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

            // PlayerId is initialised to string.Empty — a transport-level disconnect that
            // happens before SessionConnect (e.g. handshake failure, immediate close) leaves
            // it empty and there's nothing to clean up at the grain level.
            if (!IsSessionConnected) return;

            // Session was active and is closing — symmetric to the +1 in SessionConnect success.
            SharedMeta.Server.Core.Telemetry.MetricEvents.Session.Terminated("transport_drop");

            _logger.HandlerDisconnected(_connectionId, PlayerId);

            try
            {
                var grain = SessionManagerGrainOrThrow;
                await grain.OnTransportDisconnectedAsync();
            }
            catch (Exception ex)
            {
                _logger.ErrorClearingObserver(ex);
            }
        }

        private async Task RenewObserverAsync()
        {
            if (!IsSessionConnected || _observerRef == null) return;
            try
            {
                var grain = SessionManagerGrainOrThrow;
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

        // 0.24.0+ Structured "session not connected" check. Returns true when the handler
        // is bound; otherwise pushes the recovery prompt to the client and returns false so
        // the caller bails out gracefully. Replaces the older throw-then-log-ERR flow which
        // produced operator-noise spam during server restarts (every in-flight RPC/Signal
        // landed on the unbound handler and logged ERR; now they all early-return one INFO
        // line each, and the client recovers off the single push notification).
        private bool TryEnsureSessionConnected(string operation)
        {
            if (IsSessionConnected) return true;
            _broadcastSender.SendRequireSessionReconnect(
                "Server-side session handler is unbound — re-run SessionConnect to recover.");
            _logger.HandshakeSessionRecoveryPrompted(_connectionId, operation);
            return false;
        }

        public void Dispose()
        {
            _observerRenewalTimer?.Dispose();
            _observerRenewalTimer = null;
            // Remaining cleanup is handled by OnDisconnectedAsync which is called from MetaHub.OnDisconnectedAsync
        }

        // 0.24.0+ Handshake-tracing helpers: small status-array scans so the log
        // messages can quote concrete numbers ("0 rejected, 3 force-patch") without
        // each call site doing its own LINQ.
        private static (int rejected, int forcePatch) CountStatuses(MethodStatus[] statuses)
        {
            int rej = 0, fp = 0;
            for (int i = 0; i < statuses.Length; i++)
            {
                if (statuses[i] == MethodStatus.Rejected) rej++;
                else if (statuses[i] == MethodStatus.ForceServerPatch) fp++;
            }
            return (rej, fp);
        }

        private static int CountServerOnly(ushort[] serverToClient)
        {
            int n = 0;
            for (int i = 0; i < serverToClient.Length; i++)
                if (serverToClient[i] == ClientSignatureAnnotated.UnknownClientMethodId) n++;
            return n;
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
        private readonly SharedMeta.Core.Transport.MetaServerSignature? _serverSignature;
        private readonly MetaTransportOptions? _transportOptions;
        private readonly IMetaSerializer? _serializer;
        private readonly SharedMeta.Core.Patch.IPatchSchemaRegistry? _schemaRegistry;
        private readonly ClientVersionPolicy? _versionPolicy;
        private readonly Session.IClientSignatureRegistry? _signatureRegistry;

        public MetaConnectionHandlerFactory(
            IGrainFactory grainFactory,
            IEntityGrainResolver entityGrainResolver,
            ILoggerFactory loggerFactory,
            MetaTransportOptions? transportOptions = null,
            IMetaSerializer? serializer = null,
            SharedMeta.Core.Patch.IPatchSchemaRegistry? schemaRegistry = null,
            ClientVersionPolicy? versionPolicy = null,
            Session.IClientSignatureRegistry? signatureRegistry = null,
            SharedMeta.Core.Transport.MetaServerSignature? serverSignature = null)
        {
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _transportOptions = transportOptions;
            _serializer = serializer;
            _schemaRegistry = schemaRegistry;
            _versionPolicy = versionPolicy;
            _signatureRegistry = signatureRegistry;
            _serverSignature = serverSignature;
        }

        public IMetaConnectionHandler Create(string connectionId, IBroadcastSender broadcastSender)
        {
            var logger = _loggerFactory.CreateLogger<MetaConnectionHandler>();
            return new MetaConnectionHandler(connectionId, _grainFactory, _entityGrainResolver, broadcastSender, logger,
                _transportOptions, _serializer, _schemaRegistry, _versionPolicy, _signatureRegistry, _serverSignature);
        }
    }
}
