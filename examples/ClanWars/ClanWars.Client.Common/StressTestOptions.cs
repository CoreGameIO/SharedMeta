namespace ClanWars.Client.Common
{
    public class StressTestOptions
    {
        public string ServerUrl { get; set; } = "http://localhost:5050/meta";
        public string ClientAppVersion { get; set; } = "1.0.0";

        /// <summary>Number of player connections to spawn.</summary>
        public int PlayerCount { get; set; } = 100;

        /// <summary>Cap on how many clans players are allowed to create cluster-wide.
        /// Above the cap, "create clan" actions skip to "apply to existing clan."</summary>
        public int MaxClans { get; set; } = 20;

        /// <summary>Test duration in seconds.</summary>
        public int DurationSeconds { get; set; } = 30;

        /// <summary>Mean inter-action sleep per player (ms). Actual sleep is Uniform(0, 2*MeanDelayMs).</summary>
        public int MeanDelayMs { get; set; } = 500;

        /// <summary>Player-id prefix — distinguishes v1/v2 clients on the same server.</summary>
        public string PlayerPrefix { get; set; } = "p";

        /// <summary>
        /// When &gt; 0, simulators share a pool of <c>MuxChannels</c> SignalR sockets via the
        /// <c>SharedMeta.Debug.Mux</c> transport (<c>/meta-mux</c> endpoint) instead of opening
        /// one socket per player. Each simulator picks <c>tag = playerIndex / playersPerChannel</c>.
        /// Set <c>0</c> (default) to use plain <c>SignalRConnection</c> per player.
        /// </summary>
        public int MuxChannels { get; set; } = 0;

        /// <summary>Mux endpoint URL. Ignored when <see cref="MuxChannels"/> = 0.</summary>
        public string MuxServerUrl { get; set; } = "http://localhost:5050/meta-mux";

        /// <summary>Enable batched RPC mode (multiple RpcCallRequests packed into one
        /// BatchRpcCall frame). Reduces SignalR per-frame overhead. Ignored unless
        /// <see cref="MuxChannels"/> &gt; 0.</summary>
        public bool MuxBatching { get; set; } = false;

        /// <summary>Maximum entries per batch frame (when MuxBatching is on).</summary>
        public int MuxBatchSize { get; set; } = 64;

        /// <summary>Flush interval for the batching pump in ms (when MuxBatching is on).</summary>
        public int MuxBatchFlushMs { get; set; } = 1;
    }
}
