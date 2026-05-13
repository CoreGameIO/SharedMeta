using System;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Core.Logging;

namespace SharedMeta.Core.Transport
{
    /// <summary>
    /// How packet loss is simulated. Depends on the underlying transport.
    /// </summary>
    public enum PacketLossMode
    {
        /// <summary>
        /// Full connection drop — fires <see cref="DebugConnectionWrapper.SimulateDisconnect"/>.
        /// Realistic for SignalR/WebSocket/TCP where the connection is all-or-nothing.
        /// </summary>
        ConnectionDrop,

        /// <summary>
        /// Individual request hangs forever (never-completing Task).
        /// Realistic for HTTP polling where each request is independent.
        /// Triggers client-side timeout detection but does NOT break other requests.
        /// </summary>
        RequestHang,
    }

    /// <summary>
    /// Settings for network simulation. All properties are mutable at runtime
    /// so debug UI can adjust them live.
    /// </summary>
    public class DebugConnectionSettings
    {
        /// <summary>Minimum added latency in milliseconds. Default: 0.</summary>
        public int MinLatencyMs { get; set; }

        /// <summary>Maximum added latency in milliseconds. Default: 0.</summary>
        public int MaxLatencyMs { get; set; }

        /// <summary>Probability (0-100) that an RPC request is "lost". Default: 0.</summary>
        public float PacketLossPercent { get; set; }

        /// <summary>
        /// How packet loss behaves. Default: <see cref="PacketLossMode.ConnectionDrop"/>.
        /// Use <see cref="PacketLossMode.RequestHang"/> for HTTP polling transport.
        /// </summary>
        public PacketLossMode LossMode { get; set; } = PacketLossMode.ConnectionDrop;

        /// <summary>Whether simulation is active. When false, all calls pass through unmodified. Default: true.</summary>
        public bool Enabled { get; set; } = true;
    }

    /// <summary>
    /// Wraps any <see cref="IConnection"/> to simulate network problems:
    /// added latency, packet loss, and manual disconnect.
    /// Use for testing client-side connection health UI and retry behavior.
    ///
    /// <para>
    /// Packet loss behavior depends on <see cref="DebugConnectionSettings.LossMode"/>:
    /// <list type="bullet">
    /// <item><see cref="PacketLossMode.ConnectionDrop"/> — full disconnect, realistic for SignalR/TCP</item>
    /// <item><see cref="PacketLossMode.RequestHang"/> — individual request hangs, realistic for HTTP polling</item>
    /// </list>
    /// </para>
    /// </summary>
    public class DebugConnectionWrapper : IConnection
    {
        private readonly IConnection _inner;
        private readonly System.Random _rng = new();

        // When true, suppress inner connection events (during SimulateTemporaryDisconnectAsync)
        private volatile bool _suppressInnerEvents;


        /// <summary>Network simulation settings. Mutable at runtime.</summary>
        public DebugConnectionSettings Settings { get; }

        public DebugConnectionWrapper(IConnection inner, DebugConnectionSettings? settings = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Settings = settings ?? new DebugConnectionSettings();

            // Forward all events from inner connection (unless suppressed during simulated reconnect)
            _inner.OnBatch += response => { if (!_suppressInnerEvents) OnBatch?.Invoke(response); };
            _inner.OnSessionTerminated += reason => { if (!_suppressInnerEvents) OnSessionTerminated?.Invoke(reason); };
            _inner.OnDisconnected += reason => { if (!_suppressInnerEvents) OnDisconnected?.Invoke(reason); };
            _inner.OnReconnecting += () => { if (!_suppressInnerEvents) OnReconnecting?.Invoke(); };
            _inner.OnReconnected += () => { if (!_suppressInnerEvents) OnReconnected?.Invoke(); };
        }

        // --- IConnection properties ---

        public string ConnectionId => _inner.ConnectionId;
        public bool IsConnected => _inner.IsConnected;

        // --- IConnection events ---

        public event Action<SessionResponse>? OnBatch;
        public event Action<string>? OnSessionTerminated;
        public event Action<TransportDisconnectReason>? OnDisconnected;
        public event Action? OnReconnecting;
        public event Action? OnReconnected;

        // --- Pass-through methods (no simulation) ---

        public Task ConnectAsync() => _inner.ConnectAsync();
        public Task DisconnectAsync() => _inner.DisconnectAsync();
        public Task GracefulDisconnectAsync() => _inner.GracefulDisconnectAsync();

        public Task<ConnectionSessionConnectResult> SessionConnectAsync(string playerId, Guid? sessionId = null, long lastAcknowledgedSequence = 0, string? clientAppVersion = null)
            => _inner.SessionConnectAsync(playerId, sessionId, lastAcknowledgedSequence, clientAppVersion);

        public Task<ConnectionSubscribeResult> SubscribeAsync(string entityId, string stateTypeName)
            => _inner.SubscribeAsync(entityId, stateTypeName);

        public Task<bool> UnsubscribeAsync(string entityId) => _inner.UnsubscribeAsync(entityId);
        public Task AcknowledgeSequenceAsync(long sequenceNumber) => _inner.AcknowledgeSequenceAsync(sequenceNumber);
        public Task<bool> SetDebugOptionsAsync(DebugOptionsRequest request) => _inner.SetDebugOptionsAsync(request);
        public Task<DesyncReportResponse> SendDesyncReportAsync(DesyncReportRequest request) => _inner.SendDesyncReportAsync(request);
        public Task<string?> GetConfigDownloadUrlAsync(string stateTypeName, MetaConfigVersion version) => _inner.GetConfigDownloadUrlAsync(stateTypeName, version);

        // --- Simulated methods ---

        public async Task<SessionResponse> RpcCallAsync(RpcCallRequest request)
        {
            if (!Settings.Enabled)
                return await _inner.RpcCallAsync(request);

            if (Settings.PacketLossPercent > 0)
            {
                float roll;
                lock (_rng) { roll = (float)(_rng.NextDouble() * 100.0); }
                if (roll < Settings.PacketLossPercent)
                {
                    if (Settings.LossMode == PacketLossMode.RequestHang)
                    {
                        // Simulate lost HTTP request — throw immediately as if the network dropped it.
                        // SendAndCompleteAsync catches this and keeps the request pending for resend via STALL.
                        MetaLog.Debug($"[DebugConnection] Simulating lost request {request.RequestId}");
                        throw new System.Net.Http.HttpRequestException($"Simulated packet loss for request {request.RequestId}");
                    }
                    else
                    {
                        MetaLog.Debug($"[DebugConnection] Simulating connection drop (request {request.RequestId})");
                        SimulateDisconnect();
                        throw new InvalidOperationException("Simulated connection drop");
                    }
                }
            }

            // Added latency
            var min = Settings.MinLatencyMs;
            var max = Settings.MaxLatencyMs;
            if (min > 0 || max > 0)
            {
                int delay;
                lock (_rng) { delay = max > min ? _rng.Next(min, max + 1) : min; }
                if (delay > 0)
                {
                    MetaLog.Debug($"[DebugConnection] Adding {delay}ms latency to request {request.RequestId}");
                    await Task.Delay(delay);
                }
            }

            return await _inner.RpcCallAsync(request);
        }

        public async Task<QueryCallResponse> QueryCallAsync(QueryCallRequest request)
        {
            if (!Settings.Enabled)
                return await _inner.QueryCallAsync(request);

            // Same simulation for queries
            if (Settings.PacketLossPercent > 0)
            {
                float roll;
                lock (_rng) { roll = (float)(_rng.NextDouble() * 100.0); }
                if (roll < Settings.PacketLossPercent)
                {
                    if (Settings.LossMode == PacketLossMode.RequestHang)
                    {
                        MetaLog.Debug("[DebugConnection] Simulating lost query");
                        throw new System.Net.Http.HttpRequestException("Simulated packet loss for query");
                    }
                    else
                    {
                        MetaLog.Debug("[DebugConnection] Simulating connection drop (query)");
                        SimulateDisconnect();
                        throw new InvalidOperationException("Simulated connection drop");
                    }
                }
            }

            var min = Settings.MinLatencyMs;
            var max = Settings.MaxLatencyMs;
            if (min > 0 || max > 0)
            {
                int delay;
                lock (_rng) { delay = max > min ? _rng.Next(min, max + 1) : min; }
                if (delay > 0)
                    await Task.Delay(delay);
            }

            return await _inner.QueryCallAsync(request);
        }

        public async Task SignalCallAsync(SignalCallRequest request)
        {
            if (!Settings.Enabled)
            {
                await _inner.SignalCallAsync(request);
                return;
            }

            // Signals are fire-and-forget; simulated packet loss is the realistic failure mode
            // (they vanish into the network with no retry). Latency is still simulated so tests
            // can observe ordering behavior with other traffic.
            if (Settings.PacketLossPercent > 0)
            {
                float roll;
                lock (_rng) { roll = (float)(_rng.NextDouble() * 100.0); }
                if (roll < Settings.PacketLossPercent)
                {
                    MetaLog.Debug("[DebugConnection] Simulating lost signal");
                    return;  // drop silently — that is the real-world failure mode for signals
                }
            }

            var min = Settings.MinLatencyMs;
            var max = Settings.MaxLatencyMs;
            if (min > 0 || max > 0)
            {
                int delay;
                lock (_rng) { delay = max > min ? _rng.Next(min, max + 1) : min; }
                if (delay > 0)
                    await Task.Delay(delay);
            }

            await _inner.SignalCallAsync(request);
        }

        // --- Debug controls ---

        /// <summary>
        /// Simulate a permanent connection drop. Fires <see cref="OnDisconnected"/>.
        /// Use <see cref="SimulateTemporaryDisconnectAsync"/> for metro-style reconnect testing.
        /// </summary>
        public void SimulateDisconnect()
        {
            MetaLog.Debug("[DebugConnection] Simulating permanent disconnect");
            OnDisconnected?.Invoke(TransportDisconnectReason.NetworkError);
        }

        /// <summary>
        /// Simulate a temporary connection loss (metro/tunnel scenario).
        /// Performs a real disconnect→reconnect cycle on the inner connection:
        /// <list type="number">
        /// <item>Fires <see cref="OnReconnecting"/></item>
        /// <item>Disconnects inner connection (server sees <c>OnDisconnectedAsync</c>, saves subscriptions)</item>
        /// <item>Waits <paramref name="durationMs"/> (simulated outage)</item>
        /// <item>Reconnects inner connection</item>
        /// <item>Fires <see cref="OnReconnected"/> → <c>ClientDispatcher</c> re-establishes session,
        ///   server returns missed packets and re-subscribes entities</item>
        /// </list>
        /// </summary>
        /// <param name="durationMs">How long the "outage" lasts before reconnecting. Default: 3000ms.</param>
        public async Task SimulateTemporaryDisconnectAsync(int durationMs = 3000)
        {
            MetaLog.Debug($"[DebugConnection] Simulating temporary disconnect ({durationMs}ms)");

            // Suppress inner events so the real disconnect/reconnect doesn't
            // fire OnDisconnected (which would be a permanent drop to ClientDispatcher).
            // We control the event flow: OnReconnecting → wait → OnReconnected.
            _suppressInnerEvents = true;

            OnReconnecting?.Invoke();

            // Real disconnect — server sees OnDisconnectedAsync, saves subscriptions
            try { await _inner.DisconnectAsync(); }
            catch (Exception ex) { MetaLog.Debug($"[DebugConnection] Disconnect during simulation: {ex.Message}"); }

            await Task.Delay(durationMs);

            // Real reconnect — new transport connection, new connectionId on server
            MetaLog.Debug("[DebugConnection] Reconnecting inner transport...");
            await _inner.ConnectAsync();

            _suppressInnerEvents = false;

            // ClientDispatcher will call ConnectSessionAsync → server resumes session,
            // returns missed packets and re-subscribes entities
            MetaLog.Debug("[DebugConnection] Inner reconnected, firing OnReconnected");
            OnReconnected?.Invoke();
        }

        public void Dispose() => _inner.Dispose();
    }
}
