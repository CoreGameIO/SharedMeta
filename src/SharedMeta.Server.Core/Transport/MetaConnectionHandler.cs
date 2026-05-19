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
        private readonly MetaTransportOptions? _transportOptions;
        private readonly ClientVersionPolicy? _versionPolicy;
        private readonly IMetaSerializer? _serializer;
        private readonly SharedMeta.Core.Patch.IPatchSchemaRegistry? _schemaRegistry;
        /// <summary>
        /// 0.22.0+: silo-local cache fronting the cluster's client-signature directory.
        /// Null when the host hasn't called <c>AddSharedMetaClientSignatureRegistry()</c> —
        /// in that mode the handler treats every connection as "negotiation disabled" and
        /// returns empty capabilities on every SessionConnect / RegisterClientSignature.
        /// </summary>
        private readonly Session.IClientSignatureRegistry? _signatureRegistry;
        private ISessionObserver? _observerRef;
        private Timer? _observerRenewalTimer;
        private static readonly TimeSpan ObserverRenewalInterval = TimeSpan.FromSeconds(60);

        // 0.23.0+ Cached session-manager grain reference. PlayerId is fixed for the lifetime
        // of a connection, so resolving the grain reference once at SessionConnect-time and
        // reusing it on every RPC avoids the string-heavy `GrainFactory.GetGrain<T>(playerId)`
        // path (RuntimeTypeNameParser + TypeRewriter run on every call, allocating ~6 MB/s of
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
            public byte[] PatchBytes { get; set; } = Array.Empty<byte>();
        }

        public string PlayerId { get; private set; } = string.Empty;
        public Guid SessionId { get; private set; }
        public bool IsSessionConnected => PlayerId.Length > 0;
        public bool DeepDesyncRequested { get; private set; }
        /// <summary>Client app version from SessionConnect. Passed to entity grain during subscribe
        /// so it can resolve the correct [MetaConfigVersion] branch for this client.</summary>
        private string? _clientVersion;

        /// <summary>
        /// 0.22.0+ session-scoped capabilities. Populated from
        /// <see cref="Session.IClientSignatureRegistry"/> at SessionConnect (phase-1) or
        /// RegisterClientSignature (phase-2). Consulted on every RpcCall as a back-stop:
        /// even forged clients that bypassed their local <c>CapabilitiesGate</c> get
        /// rejected here.
        /// </summary>
        private SharedMeta.Core.Transport.ClientCapabilities? _clientCapabilities;

        /// <summary>
        /// 0.23.0+ Negotiated signature hash for this connection. Captured from
        /// <c>SessionConnectRequest.ClientSignatureHash</c> on phase-1 (or zero if absent),
        /// and overwritten by <c>RegisterClientSignatureRequest.Signature.Hash</c> after
        /// phase-2 registration. Forwarded into every <c>SubscribeToEntityAsync</c> call so
        /// the target <c>EntityGrain</c> can resolve the caller's compatibility capabilities
        /// via <c>IClientSignatureRegistry</c> locally — replaces the 0.22.x push-the-list
        /// flow into <c>SessionManagerGrain._clientCapabilities</c>.
        /// </summary>
        private ulong _clientSignatureHash;

        public MetaConnectionHandler(
            string connectionId,
            IGrainFactory grainFactory,
            IEntityGrainResolver entityGrainResolver,
            IBroadcastSender broadcastSender,
            ILogger logger,
            SignatureValidator? signatureValidator = null,
            MetaTransportOptions? transportOptions = null,
            IMetaSerializer? serializer = null,
            SharedMeta.Core.Patch.IPatchSchemaRegistry? schemaRegistry = null,
            ClientVersionPolicy? versionPolicy = null,
            Session.IClientSignatureRegistry? signatureRegistry = null)
        {
            _connectionId = connectionId ?? throw new ArgumentNullException(nameof(connectionId));
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _broadcastSender = broadcastSender ?? throw new ArgumentNullException(nameof(broadcastSender));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _signatureValidator = signatureValidator;
            _transportOptions = transportOptions;
            _serializer = serializer;
            _schemaRegistry = schemaRegistry;
            _versionPolicy = versionPolicy;
            _signatureRegistry = signatureRegistry;
        }

        #region IMetaConnectionHandler

        public async Task<SessionConnectResponse> SessionConnectAsync(SessionConnectRequest request)
        {
            // 0.23.0+ Telemetry: bracket the whole handshake (version-gate, sig lookup, session
            // create/resume). The result tag is derived from the response right before return.
            using var __scActivity = SharedMeta.Server.Core.Telemetry.SharedMetaActivities.Source.StartActivity(
                SharedMeta.Server.Core.Telemetry.SharedMetaActivities.SpanSessionConnect);
            __scActivity?.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagPlayerId, request.PlayerId);
            var __scStart = System.Diagnostics.Stopwatch.GetTimestamp();
            SessionConnectResponse? __scResponse = null;
            try
            {
                if (string.IsNullOrEmpty(request.PlayerId))
                {
                    return new SessionConnectResponse { Success = false, Error = "PlayerId is required" };
                }

                // Version gate — policy encapsulates TTL caching, grain refresh, and parsing.
                if (_versionPolicy != null)
                {
                    var versionResult = await _versionPolicy.ValidateAsync(request.ClientVersion);
                    _logger.LogInformation(
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

                // 0.22.0+ compatibility negotiation. Non-zero ClientSignatureHash means the
                // client opted in. Registry lookup is per-silo cache-first; on miss we ask
                // the cluster directory before declaring the signature unknown. An unknown
                // signature ships NeedsSignatureRegistration = true so the client follows up
                // with phase-2 (RegisterClientSignature). Hash == 0 = legacy / opted-out =
                // no capabilities attached (server treats client as fully compatible).
                bool needsSignatureRegistration = false;
                ClientCapabilities? capabilities = null;
                if (request.ClientSignatureHash != 0 && _signatureRegistry != null && result.Success)
                {
                    capabilities = await _signatureRegistry.TryGetCapabilitiesAsync(request.ClientSignatureHash);
                    needsSignatureRegistration = capabilities == null;
                }
                // Stash for the per-RPC back-stop. Null capabilities (negotiation disabled, or
                // pending phase-2) means the back-stop runs in pass-through mode.
                _clientCapabilities = capabilities;

                // 0.23.0+ Stash the hash for forwarding through SubscribeToEntityAsync. EntityGrain
                // resolves its own ClientCapabilities locally via IClientSignatureRegistry, so we
                // no longer push the caps blob into SessionManagerGrain.
                _clientSignatureHash = request.ClientSignatureHash;

                __scResponse = new SessionConnectResponse
                {
                    Success = result.Success,
                    Error = result.Error,
                    SessionId = result.SessionId,
                    IsNewSession = result.IsNewSession,
                    MissedPackets = result.MissedPackets,
                    SignatureMismatches = signatureMismatches,
                    ServerTimeTicks = result.ServerTimeTicks,
                    ServerVersion = _versionPolicy?.ServerVersion ?? _transportOptions?.ServerVersion,
                    NeedsSignatureRegistration = needsSignatureRegistration,
                    Capabilities = capabilities,
                    ResubscribedEntities = result.ResubscribedEntities?.Select(e => new ResubscribedEntityInfo
                    {
                        EntityId = e.EntityId,
                        StateBytes = e.StateBytes,
                        EntitySequenceNumber = e.EntitySequenceNumber,
                        OptimisticRandomBytes = e.OptimisticRandomBytes,
                        NamedRandomsBytes = e.NamedRandomsBytes,
                        ConfigMajorVersion = e.ConfigVersion.Major,
                        ConfigMinorVersion = e.ConfigVersion.Minor,
                        ConfigPatchVersion = e.ConfigVersion.Patch
                    }).ToList()
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
                var __scElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(__scStart).TotalMilliseconds;
                var __scResult = __scResponse?.Success == true ? "success"
                    : __scResponse?.NeedsSignatureRegistration == true ? "needs_signature_registration"
                    : "rejected";
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.SessionConnectDuration.Record(__scElapsed,
                    new KeyValuePair<string, object?>("result", __scResult));
                if (__scResponse?.Success == true)
                    SharedMeta.Server.Core.Telemetry.SharedMetaMeters.SessionsActive.Add(1);
                __scActivity?.SetTag(SharedMeta.Server.Core.Telemetry.SharedMetaActivities.TagResult, __scResult);
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
                    Capabilities = new ClientCapabilities(),
                };
            }

            try
            {
                var capabilities = await _signatureRegistry.RegisterAsync(request.Signature);
                _clientCapabilities = capabilities;
                // 0.23.0+ Update the cached signature hash so subsequent SubscribeToEntityAsync
                // calls forward the freshly-registered hash to EntityGrain (which then re-reads
                // from IClientSignatureRegistry — now populated by the RegisterAsync above).
                _clientSignatureHash = request.Signature.SignatureHash;
                return new RegisterClientSignatureResponse
                {
                    Success = true,
                    Capabilities = capabilities,
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
                    Capabilities = new ClientCapabilities(),
                };
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

                var grain = SessionManagerGrainOrThrow;
                var result = await grain.SubscribeToEntityAsync(request.EntityId, request.StateTypeName, _clientVersion, _clientSignatureHash);

                _logger.HandlerSubscribe(PlayerId!, request.EntityId, result.Success);

                return new SubscribeResponse
                {
                    Success = result.Success,
                    Error = result.Error,
                    StateBytes = result.StateBytes ?? Array.Empty<byte>(),
                    EntitySequenceNumber = result.EntitySequenceNumber,
                    OptimisticRandomBytes = result.OptimisticRandomBytes,
                    NamedRandomsBytes = result.NamedRandomsBytes,
                    ConfigMajorVersion = result.ConfigVersion.Major,
                    ConfigMinorVersion = result.ConfigVersion.Minor,
                    ConfigPatchVersion = result.ConfigVersion.Patch,
                    FeatureRequirement = result.FeatureRequirement,   // 0.22.0+ structured rejection
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
            // 0.23.0+ End-to-end server-side timing. Started at the very top of the transport
            // entry point so the histogram includes SessionManagerGrain queue, grain-hop to
            // EntityGrain, the actual method body (covered separately by RpcDuration), and
            // response build. The diff with RpcDuration surfaces queue/hop overhead — large
            // gaps point at SignalR MaximumParallelInvocationsPerClient (default 1!) and at
            // hot SessionManagerGrain queues under burst load.
            var __totalStart = System.Diagnostics.Stopwatch.GetTimestamp();
            string __totalResult = "success";
            try
            {
                EnsureSessionConnected();

                if (string.IsNullOrEmpty(request.EntityId))
                {
                    __totalResult = "bad_request";
                    return SessionResponse.ForError("EntityId is required");
                }

                // 0.22.0 server-side back-stop: even if the client bypassed the local
                // CapabilitiesGate, the server enforces rejection here based on the cached
                // capabilities for this session's signature. Force-ServerPatch is intentionally
                // NOT enforced server-side — the server still executes whatever the client
                // declared, since downgrading the mode mid-flight would change semantics for
                // a well-behaved client that already complied at the gate.
                if (_clientCapabilities != null
                    && SharedMeta.Core.Transport.CapabilitiesGate.IsRejected(
                        _clientCapabilities, request.ServiceName, request.MethodName, request.MethodVersion))
                {
                    __totalResult = "rejected";
                    return SessionResponse.ForError(
                        $"Method '{request.ServiceName}.{request.MethodName}' (v{request.MethodVersion}) is rejected for this client signature.");
                }

                var call = new RpcCall
                {
                    ServiceName = request.ServiceName,
                    MethodName = request.MethodName,
                    MethodVersion = request.MethodVersion,
                    CallerId = PlayerId,
                    CallerClientVersion = _clientVersion,
                    Payload = request.Payload,
                    IsCrossOptimistic = request.IsCrossOptimistic,
                    ServerTimeTicks = request.ServerTimeTicks,
                    DeepDesyncRequested = DeepDesyncRequested
                };

                var grain = SessionManagerGrainOrThrow;

                var response = await grain.SendToEntityAsync(
                    request.EntityId,
                    request.RequestId,
                    call,
                    request.LastAcknowledgedSequence,
                    SessionId).ConfigureAwait(false);

                // Cache server patch for desync reporting (if enabled)
                if (_transportOptions?.DesyncReportingEnabled == true && response.Operations != null)
                {
                    foreach (var op in response.Operations)
                    {
                        if (op.RequestId == request.RequestId
                            && op.Op?.PatchBytes != null)
                        {
                            CachePatch(request.EntityId, request.ServiceName, request.MethodName,
                                op.Op.PatchBytes);
                            break;
                        }
                    }
                }

                return response;
            }
            catch (Exception ex)
            {
                __totalResult = "error";
                _logger.HandlerRpcCallError(ex);
                return SessionResponse.ForError(ex.Message);
            }
            finally
            {
                var __totalElapsed = System.Diagnostics.Stopwatch.GetElapsedTime(__totalStart).TotalMilliseconds;
                SharedMeta.Server.Core.Telemetry.SharedMetaMeters.ServerRpcTotalDuration.Record(__totalElapsed,
                    new KeyValuePair<string, object?>("service", request.ServiceName),
                    new KeyValuePair<string, object?>("method", request.MethodName),
                    new KeyValuePair<string, object?>("result", __totalResult));
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
                    MethodVersion = request.MethodVersion,
                    CallerId = PlayerId,
                    CallerClientVersion = _clientVersion,
                    Payload = request.Payload,
                    ServerTimeTicks = DateTime.UtcNow.Ticks
                };

                var grain = SessionManagerGrainOrThrow;
                return await grain.QueryEntityAsync(request.EntityId, request.ServiceName, call);
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
                EnsureSessionConnected();

                if (string.IsNullOrEmpty(request.EntityId) || string.IsNullOrEmpty(request.ServiceName))
                {
                    _logger.LogWarning("[Handler] Signal rejected — missing EntityId or ServiceName");
                    return;
                }

                var call = new RpcCall
                {
                    ServiceName = request.ServiceName,
                    MethodName = request.MethodName,
                    MethodVersion = request.MethodVersion,
                    CallerId = PlayerId,
                    CallerClientVersion = _clientVersion,
                    Payload = request.Payload,
                    ServerTimeTicks = DateTime.UtcNow.Ticks
                };

                var grain = SessionManagerGrainOrThrow;
                await grain.SignalEntityAsync(request.EntityId, request.ServiceName, call);
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
                EnsureSessionConnected();

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

                EnsureSessionConnected();

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
                    if (cachedServerPatch is { Length: > 0 })
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

                    report.ServerPatchCrc = cachedServerPatch is { Length: > 0 } ? SharedMeta.Core.Patch.PatchCrc.Compute(cachedServerPatch) : 0u;
                    report.ClientPatchCrc = request.ClientPatchBytes is { Length: > 0 } ? SharedMeta.Core.Patch.PatchCrc.Compute(request.ClientPatchBytes) : 0u;
                    report.ServerPatchBytes = cachedServerPatch;
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

        private void CachePatch(string entityId, string serviceName, string methodName, byte[] patchBytes)
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

        private byte[]? LookupPatch(string entityId, string serviceName, string methodName)
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
            return null;
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

            // PlayerId is initialised to string.Empty — a transport-level disconnect that
            // happens before SessionConnect (e.g. handshake failure, immediate close) leaves
            // it empty and there's nothing to clean up at the grain level.
            if (!IsSessionConnected) return;

            // 0.23.0+ Telemetry: session was active (we passed IsSessionConnected gate) and is
            // closing now. Symmetric to the +1 in SessionConnectAsync success branch.
            SharedMeta.Server.Core.Telemetry.SharedMetaMeters.SessionsActive.Add(-1);
            SharedMeta.Server.Core.Telemetry.SharedMetaMeters.SessionTerminated.Add(1,
                new KeyValuePair<string, object?>("reason", "transport_drop"));

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
        private readonly MetaTransportOptions? _transportOptions;
        private readonly IMetaSerializer? _serializer;
        private readonly SharedMeta.Core.Patch.IPatchSchemaRegistry? _schemaRegistry;
        private readonly ClientVersionPolicy? _versionPolicy;
        private readonly Session.IClientSignatureRegistry? _signatureRegistry;

        public MetaConnectionHandlerFactory(
            IGrainFactory grainFactory,
            IEntityGrainResolver entityGrainResolver,
            ILoggerFactory loggerFactory,
            SignatureValidator? signatureValidator = null,
            MetaTransportOptions? transportOptions = null,
            IMetaSerializer? serializer = null,
            SharedMeta.Core.Patch.IPatchSchemaRegistry? schemaRegistry = null,
            ClientVersionPolicy? versionPolicy = null,
            Session.IClientSignatureRegistry? signatureRegistry = null)
        {
            _grainFactory = grainFactory ?? throw new ArgumentNullException(nameof(grainFactory));
            _entityGrainResolver = entityGrainResolver ?? throw new ArgumentNullException(nameof(entityGrainResolver));
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _signatureValidator = signatureValidator;
            _transportOptions = transportOptions;
            _serializer = serializer;
            _schemaRegistry = schemaRegistry;
            _versionPolicy = versionPolicy;
            _signatureRegistry = signatureRegistry;
        }

        public IMetaConnectionHandler Create(string connectionId, IBroadcastSender broadcastSender)
        {
            var logger = _loggerFactory.CreateLogger<MetaConnectionHandler>();
            return new MetaConnectionHandler(connectionId, _grainFactory, _entityGrainResolver, broadcastSender, logger,
                _signatureValidator, _transportOptions, _serializer, _schemaRegistry, _versionPolicy, _signatureRegistry);
        }
    }
}
