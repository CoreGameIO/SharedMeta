using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Core.Packets;
using SharedMeta.Core.Transport;

namespace SharedMeta.Client.Network
{
    /// <summary>
    /// Adapts IClientDispatcher to INetwork for a specific entity.
    /// Allows API clients to work with the dispatcher architecture.
    /// </summary>
    public class DispatcherNetworkAdapter : INetwork
    {
        private readonly IClientDispatcher _dispatcher;
        private readonly IMetaSerializer _serializer;
        private readonly string _entityId;
        private readonly string? _stateTypeName;
        private readonly Func<long> _serverTimeClock;
        private IDisposable? _broadcastSubscription;

        public string ClientId => _dispatcher.Connection.ConnectionId;
        public string? PlayerId { get; set; }
        public string? EntityId => _entityId;
        public long LastKnownEntitySequence => _dispatcher.GetLastKnownEntitySequence(_entityId);
        public long ServerTimeTicks => _serverTimeClock();

        /// <summary>
        /// 0.22.0+ session-scoped capabilities, sourced from the parent <see cref="IClientDispatcher"/>.
        /// The dispatcher populates these after <c>SessionConnectAsync</c> completes (phase-1) or
        /// after <c>RegisterClientSignatureAsync</c> resolves (phase-2). Generated <c>*ApiClient</c>
        /// consults this object before going on the wire. Null = negotiation disabled / pending.
        /// <para>
        /// Setter is honored for test scaffolding (a unit test can inject a fake capabilities
        /// object directly on the adapter) but in production the value flows from the dispatcher.
        /// </para>
        /// </summary>
        /// <summary>
        /// 0.24.0+ Annotated signature view of the session capabilities. Setter-override pattern
        /// so unit tests can inject directly while production flow inherits from the parent
        /// dispatcher.
        /// </summary>
        public ClientSignatureAnnotated? Annotated
        {
            get => _annotatedOverride ?? _dispatcher.Annotated;
            set => _annotatedOverride = value;
        }
        private ClientSignatureAnnotated? _annotatedOverride;

        /// <summary>
        /// 0.22.0+ Per-entity capability overlay. Stored on the per-entity adapter so generated
        /// <c>*ApiClient</c> consults it at the gate alongside session-level
        /// <see cref="Capabilities"/>. <c>ClientDispatcher.SubscribeAsync</c> sets this from
        /// <c>SubscribeResponse.AugmentedCapabilities</c> when the server returned a non-null
        /// overlay; otherwise stays null (no per-entity restrictions for this session).
        /// </summary>
        public EntityAugmentedCapabilities? EntityCapabilities { get; set; }

        public event Action<NetworkBroadcast>? OnBroadcast;
        public event Action<string>? OnDisconnected;

        public DispatcherNetworkAdapter(
            IClientDispatcher dispatcher,
            IMetaSerializer serializer,
            string entityId,
            Func<long> serverTimeClock,
            string? stateTypeName = null)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _entityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
            _serverTimeClock = serverTimeClock ?? throw new ArgumentNullException(nameof(serverTimeClock));
            _stateTypeName = stateTypeName;

            // Subscribe to broadcasts for this entity
            _broadcastSubscription = _dispatcher.OnBroadcast(_entityId, HandleBroadcast);

            // Forward disconnect events
            _dispatcher.Connection.OnDisconnected += HandleDisconnected;
        }

        // SessionOp.OpBytes carries the pre-serialized MetaOperation produced by the server.
        // We deserialize once and project into the in-memory NetworkBroadcast / CallResponse
        // shapes the API client expects. OpBytes is a PooledPayload; on the client side the
        // Memory is byte[]-backed (transport copied bytes before delivery — see InProcess /
        // network transport contracts).
        private MetaOperation UnpackOp(SessionOp sessionOp)
            => sessionOp.OpBytes.Length > 0 ? _serializer.Unpack<MetaOperation>(sessionOp.OpBytes.Memory) : new MetaOperation();

        // 0.24.0+ Translate server's global method index → client's local index using the
        // per-signature map shipped in ClientSignatureAnnotated.ServerToClient. When the map
        // isn't available (signature negotiation disabled / not yet completed) we fall back
        // to identity — client and server built from the same protocol surface produce
        // identical sort orders, so server's id equals client's id in that case. Returns
        // UnknownClientMethodId (sentinel) only when the explicit map says "client doesn't know".
        private ushort TranslateIncomingMethodId(ushort serverMethodId)
        {
            var annotated = Annotated;
            if (annotated == null) return serverMethodId;
            var map = annotated.ServerToClient;
            if (map == null || serverMethodId >= map.Length) return serverMethodId;
            return map[serverMethodId];
        }

        private void HandleBroadcast(SessionOp sessionOp)
        {
            var op = UnpackOp(sessionOp);
            // Translate server's global method index → client's local index via the
            // per-signature ServerToClientMethodIds map sent in ClientCapabilities. When the
            // server emits a method the client doesn't know (e.g. server-only), the sentinel
            // ushort.MaxValue propagates and generated dispatch ignores it.
            ushort clientMethodId = TranslateIncomingMethodId(op.MethodId);

            OnBroadcast?.Invoke(new NetworkBroadcast
            {
                MethodId = clientMethodId,
                CallerId = op.CallerId,
                ArgsBytes = op.Payload.ToArray(),
                ReplayContext = op.ReplayPayload.ToArray(),
                TriggerOperations = op.Triggers,
                ServerTimeTicks = op.ServerTimeTicks,
                RandomScrollDelta = op.RandomScrollDelta,
                NamedRandomScrollDeltas = op.NamedRandomScrollDeltas,
                PatchBytes = op.PatchBytes.IsEmpty ? null : op.PatchBytes.ToArray(),
                StateBytes = op.StateBytes.IsEmpty ? null : op.StateBytes.ToArray(),
                ExecutedConfigVersion = op.ExecutedConfigVersion
            });
        }

        private void HandleDisconnected(TransportDisconnectReason reason)
        {
            OnDisconnected?.Invoke(reason.ToString());
        }

        public async Task<CallResponse<T>> CallAsync<T>(ushort methodId, ReadOnlyMemory<byte> args, bool isCrossOptimistic = false, long serverTimeTicks = 0)
        {
            // 0.24.0+ RpcCall no longer carries ServiceName/MethodName/MethodVersion — only
            // MethodId addresses the dispatch. ServiceName/methodName/methodVersion are still
            // accepted here for signature symmetry with older generated callers and used by
            // ClientDispatcher to keep RpcCallRequest's diagnostic fields populated; they
            // do not travel inside RpcCall anymore.
            var call = new RpcCall
            {
                MethodId = methodId,
                Payload = args,
                CallerId = PlayerId,
                IsCrossOptimistic = isCrossOptimistic,
                ServerTimeTicks = serverTimeTicks
            };

            var sessionOp = await _dispatcher.SendAsync(_entityId, call, _stateTypeName);

            if (sessionOp.HasError)
            {
                throw new InvalidOperationException($"RPC call failed: {sessionOp.ErrorMessage}");
            }

            var op = UnpackOp(sessionOp);
            T result = default!;
            var resultBytes = op.ResultBytes;
            if (!resultBytes.IsEmpty)
            {
                result = _serializer.Unpack<T>(resultBytes);
            }

            return new CallResponse<T>
            {
                Result = result,
                ReplayContext = op.ReplayPayload.ToArray(),
                TriggerOperations = op.Triggers,
                CrossEntityOperations = sessionOp.CrossEntityOperations,
                ServerTimeTicks = op.ServerTimeTicks,
                RandomScrollDelta = op.RandomScrollDelta,
                NamedRandomScrollDeltas = op.NamedRandomScrollDeltas,
                PatchBytes = op.PatchBytes.IsEmpty ? null : op.PatchBytes.ToArray(),
                StateBytes = op.StateBytes.IsEmpty ? null : op.StateBytes.ToArray(),
                DeepDesyncCrc = op.DeepDesyncCrc,
                ExecutedConfigVersion = op.ExecutedConfigVersion,
                Debug = op.Debug,
            };
        }

        public async Task<VoidCallResponse> CallVoidAsync(ushort methodId, ReadOnlyMemory<byte> args, bool isCrossOptimistic = false, long serverTimeTicks = 0)
        {
            var call = new RpcCall
            {
                MethodId = methodId,
                Payload = args,
                CallerId = PlayerId,
                IsCrossOptimistic = isCrossOptimistic,
                ServerTimeTicks = serverTimeTicks
            };

            var sessionOp = await _dispatcher.SendAsync(_entityId, call, _stateTypeName);

            if (sessionOp.HasError)
            {
                throw new InvalidOperationException($"RPC call failed: {sessionOp.ErrorMessage}");
            }

            var op = UnpackOp(sessionOp);
            return new VoidCallResponse
            {
                ReplayContext = op.ReplayPayload.ToArray(),
                TriggerOperations = op.Triggers,
                CrossEntityOperations = sessionOp.CrossEntityOperations,
                ServerTimeTicks = op.ServerTimeTicks,
                RandomScrollDelta = op.RandomScrollDelta,
                NamedRandomScrollDeltas = op.NamedRandomScrollDeltas,
                PatchBytes = op.PatchBytes.IsEmpty ? null : op.PatchBytes.ToArray(),
                StateBytes = op.StateBytes.IsEmpty ? null : op.StateBytes.ToArray(),
                DeepDesyncCrc = op.DeepDesyncCrc,
                ExecutedConfigVersion = op.ExecutedConfigVersion
            };
        }

        public async Task<ByteCallResponse> CallBytesAsync(ushort methodId, ReadOnlyMemory<byte> args, bool isCrossOptimistic = false, long serverTimeTicks = 0)
        {
            var call = new RpcCall
            {
                MethodId = methodId,
                Payload = args,
                CallerId = PlayerId,
                IsCrossOptimistic = isCrossOptimistic,
                ServerTimeTicks = serverTimeTicks
            };

            var sessionOp = await _dispatcher.SendAsync(_entityId, call, _stateTypeName);

            if (sessionOp.HasError)
            {
                throw new InvalidOperationException($"RPC call failed: {sessionOp.ErrorMessage}");
            }

            var op = UnpackOp(sessionOp);
            return new ByteCallResponse
            {
                ResultBytes = op.ResultBytes.ToArray(),
                ReplayContext = op.ReplayPayload.ToArray(),
                TriggerOperations = op.Triggers,
                CrossEntityOperations = sessionOp.CrossEntityOperations,
                ServerTimeTicks = op.ServerTimeTicks,
                RandomScrollDelta = op.RandomScrollDelta,
                NamedRandomScrollDeltas = op.NamedRandomScrollDeltas,
                PatchBytes = op.PatchBytes.IsEmpty ? null : op.PatchBytes.ToArray(),
                StateBytes = op.StateBytes.IsEmpty ? null : op.StateBytes.ToArray(),
                DeepDesyncCrc = op.DeepDesyncCrc,
                ExecutedConfigVersion = op.ExecutedConfigVersion,
                Debug = op.Debug,
            };
        }

        /// <summary>
        /// Fire a signal through the underlying connection — no RequestId, no dispatcher tracking,
        /// no auto-retry. Completes as soon as the connection hands the message off.
        /// Server-side errors are never reported back.
        /// </summary>
        public ValueTask SendSignalAsync(ushort methodId, ReadOnlyMemory<byte> args)
        {
            var request = new SignalCallRequest
            {
                EntityId = _entityId,
                MethodId = methodId,
                Payload = args
            };

            // Fire-and-forget at the dispatcher level too — we do NOT await the Task here.
            // Returning a completed ValueTask mirrors the "signal leaves immediately" contract;
            // the actual wire-send runs on the continuation scheduled by SignalCallAsync.
            // Errors from the transport layer are logged by IConnection implementations if at all.
            _ = _dispatcher.Connection.SignalCallAsync(request);
            return default;
        }

        public async Task<DesyncReportResponse?> SendDesyncReportAsync(DesyncReportRequest request)
        {
            try
            {
                SharedMeta.Core.Logging.MetaLog.Debug($"[Desync] Sending report: {request.ServiceName}.{request.MethodName} entity={request.EntityId} clientPatch={request.ClientPatchBytes?.Length ?? 0}B");
                var response = await _dispatcher.Connection.SendDesyncReportAsync(request);
                SharedMeta.Core.Logging.MetaLog.Debug($"[Desync] Server response: status={response?.Status} error={response?.Error}");
                return response;
            }
            catch (Exception ex)
            {
                SharedMeta.Core.Logging.MetaLog.Error($"[Desync] SendDesyncReportAsync failed: {ex.Message}", ex);
                return null;
            }
        }

        public void SuppressBroadcasts()
        {
            _dispatcher.SuppressBroadcasts();
        }

        public void ResumeBroadcasts()
        {
            _dispatcher.ResumeBroadcasts();
        }

        public void Dispose()
        {
            _broadcastSubscription?.Dispose();
            _broadcastSubscription = null;
            _dispatcher.Connection.OnDisconnected -= HandleDisconnected;
        }
    }
}
