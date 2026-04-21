using System;
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
        public long ServerTimeTicks => _serverTimeClock();

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

        private void HandleBroadcast(SessionOp op)
        {
            OnBroadcast?.Invoke(new NetworkBroadcast
            {
                ServiceName = op.MainOperation.Call.ServiceName,
                MethodName = op.MainOperation.Call.MethodName,
                CallerId = op.MainOperation.Call.CallerId,
                ArgsBytes = op.MainOperation.Call.Payload ?? Array.Empty<byte>(),
                ReplayContext = op.MainOperation.Response.ReplayPayload ?? Array.Empty<byte>(),
                TriggerOperations = op.TriggerOperations,
                ServerTimeTicks = op.MainOperation.Call.ServerTimeTicks,
                RandomScrollDelta = op.MainOperation.Response.RandomScrollDelta,
                NamedRandomScrollDeltas = op.MainOperation.Response.NamedRandomScrollDeltas,
                PatchBytes = op.MainOperation.Response.PatchBytes,
                StateBytes = op.MainOperation.Response.StateBytes
            });
        }

        private void HandleDisconnected(TransportDisconnectReason reason)
        {
            OnDisconnected?.Invoke(reason.ToString());
        }

        public async Task<CallResponse<T>> CallAsync<T>(string serviceName, string methodName, byte[] args, bool isCrossOptimistic = false, long serverTimeTicks = 0)
        {
            var call = new RpcCall
            {
                ServiceName = serviceName,
                MethodName = methodName,
                Payload = args,
                CallerId = PlayerId,
                IsCrossOptimistic = isCrossOptimistic,
                ServerTimeTicks = serverTimeTicks
            };

            var rpcOp = await _dispatcher.SendAsync(_entityId, call, _stateTypeName);

            if (rpcOp.HasError)
            {
                throw new InvalidOperationException($"RPC call failed: {rpcOp.ErrorMessage}");
            }

            T result = default!;
            var resultBytes = rpcOp.MainOperation.Response.ResultBytes;
            if (resultBytes != null && resultBytes.Length > 0)
            {
                result = _serializer.Unpack<T>(resultBytes);
            }

            return new CallResponse<T>
            {
                Result = result,
                ReplayContext = rpcOp.MainOperation.Response.ReplayPayload ?? Array.Empty<byte>(),
                TriggerOperations = rpcOp.TriggerOperations,
                CrossEntityOperations = rpcOp.CrossEntityOperations,
                ServerTimeTicks = rpcOp.MainOperation.Call.ServerTimeTicks,
                RandomScrollDelta = rpcOp.MainOperation.Response.RandomScrollDelta,
                NamedRandomScrollDeltas = rpcOp.MainOperation.Response.NamedRandomScrollDeltas,
                PatchBytes = rpcOp.MainOperation.Response.PatchBytes,
                StateBytes = rpcOp.MainOperation.Response.StateBytes,
                DeepDesyncCrc = rpcOp.MainOperation.Response.DeepDesyncCrc
            };
        }

        public async Task<VoidCallResponse> CallVoidAsync(string serviceName, string methodName, byte[] args, bool isCrossOptimistic = false, long serverTimeTicks = 0)
        {
            var call = new RpcCall
            {
                ServiceName = serviceName,
                MethodName = methodName,
                Payload = args,
                CallerId = PlayerId,
                IsCrossOptimistic = isCrossOptimistic,
                ServerTimeTicks = serverTimeTicks
            };

            var rpcOp = await _dispatcher.SendAsync(_entityId, call, _stateTypeName);

            if (rpcOp.HasError)
            {
                throw new InvalidOperationException($"RPC call failed: {rpcOp.ErrorMessage}");
            }

            return new VoidCallResponse
            {
                ReplayContext = rpcOp.MainOperation.Response.ReplayPayload ?? Array.Empty<byte>(),
                TriggerOperations = rpcOp.TriggerOperations,
                CrossEntityOperations = rpcOp.CrossEntityOperations,
                ServerTimeTicks = rpcOp.MainOperation.Call.ServerTimeTicks,
                RandomScrollDelta = rpcOp.MainOperation.Response.RandomScrollDelta,
                NamedRandomScrollDeltas = rpcOp.MainOperation.Response.NamedRandomScrollDeltas,
                PatchBytes = rpcOp.MainOperation.Response.PatchBytes,
                StateBytes = rpcOp.MainOperation.Response.StateBytes,
                DeepDesyncCrc = rpcOp.MainOperation.Response.DeepDesyncCrc
            };
        }

        public async Task<ByteCallResponse> CallBytesAsync(string serviceName, string methodName, byte[] args, bool isCrossOptimistic = false, long serverTimeTicks = 0)
        {
            var call = new RpcCall
            {
                ServiceName = serviceName,
                MethodName = methodName,
                Payload = args,
                CallerId = PlayerId,
                IsCrossOptimistic = isCrossOptimistic,
                ServerTimeTicks = serverTimeTicks
            };

            var rpcOp = await _dispatcher.SendAsync(_entityId, call, _stateTypeName);

            if (rpcOp.HasError)
            {
                throw new InvalidOperationException($"RPC call failed: {rpcOp.ErrorMessage}");
            }

            return new ByteCallResponse
            {
                ResultBytes = rpcOp.MainOperation.Response.ResultBytes ?? Array.Empty<byte>(),
                ReplayContext = rpcOp.MainOperation.Response.ReplayPayload ?? Array.Empty<byte>(),
                TriggerOperations = rpcOp.TriggerOperations,
                CrossEntityOperations = rpcOp.CrossEntityOperations,
                ServerTimeTicks = rpcOp.MainOperation.Call.ServerTimeTicks,
                RandomScrollDelta = rpcOp.MainOperation.Response.RandomScrollDelta,
                NamedRandomScrollDeltas = rpcOp.MainOperation.Response.NamedRandomScrollDeltas,
                PatchBytes = rpcOp.MainOperation.Response.PatchBytes,
                StateBytes = rpcOp.MainOperation.Response.StateBytes,
                DeepDesyncCrc = rpcOp.MainOperation.Response.DeepDesyncCrc
            };
        }

        /// <summary>
        /// Fire a signal through the underlying connection — no RequestId, no dispatcher tracking,
        /// no auto-retry. Completes as soon as the connection hands the message off.
        /// Server-side errors are never reported back.
        /// </summary>
        public ValueTask SendSignalAsync(string serviceName, string methodName, byte[] args)
        {
            var request = new SignalCallRequest
            {
                EntityId = _entityId,
                ServiceName = serviceName,
                MethodName = methodName,
                Payload = args ?? Array.Empty<byte>()
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
