using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharedMeta.Debug.Mux;

namespace ClanWars.Client.Common
{
    /// <summary>
    /// Spawns <see cref="StressTestOptions.PlayerCount"/> simulated players in parallel,
    /// runs them for <see cref="StressTestOptions.DurationSeconds"/>, then renders the
    /// metrics.
    /// <para>
    /// Two transport modes:
    /// <list type="bullet">
    /// <item><b>Plain SignalR</b> (<c>MuxChannels = 0</c>, default): one WebSocket per player.</item>
    /// <item><b>Mux</b> (<c>MuxChannels = N &gt; 0</c>): N WebSockets to <c>/meta-mux</c>;
    /// each player gets a logical <c>MuxConnection</c> with its own tag. Lets a single client
    /// process drive thousands of simulated players without exhausting socket / thread-pool
    /// resources.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class StressTestRunner
    {
        public static async Task RunAsync(StressTestOptions options)
        {
            var transportNote = options.MuxChannels > 0
                ? $"mux@{options.MuxServerUrl} (channels={options.MuxChannels}, ~{options.PlayerCount / Math.Max(1, options.MuxChannels)} players/socket)"
                : options.ServerUrl;
            Console.WriteLine($"[stress] Starting {options.PlayerCount} players for {options.DurationSeconds}s @ {transportNote}");
            Console.WriteLine($"[stress] ClientAppVersion = {options.ClientAppVersion}  (max clans = {options.MaxClans})");

            var metrics = new MetricsCollector();
            var sharedClans = new SharedClanRegistry();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));

            // Optional Mux channel pool. Built and started BEFORE simulators spin up so the
            // physical sockets are live before any SessionConnect fires.
            MuxChannel[]? channels = null;
            if (options.MuxChannels > 0)
            {
                channels = new MuxChannel[options.MuxChannels];
                for (int i = 0; i < channels.Length; i++)
                    channels[i] = new MuxChannel(options.MuxServerUrl);
                await Task.WhenAll(channels.Select(c => c.StartAsync()));
                Console.WriteLine($"[stress] Mux pool ready: {channels.Length} physical SignalR sockets connected.");
            }

            try
            {
                var simulators = Enumerable.Range(0, options.PlayerCount)
                    .Select(i => new PlayerSimulator(
                        options, metrics, sharedClans,
                        playerId: $"{options.PlayerPrefix}-{i}",
                        rngSeed: i * 31 + (int)(DateTime.UtcNow.Ticks & 0xFFFF),
                        // Pick channel by round-robin; tag = simulator index for uniqueness within the channel.
                        muxChannel: channels?[i % channels.Length],
                        muxTag: channels != null ? i : (int?)null))
                    .ToList();

                var tasks = simulators.Select(s => Task.Run(() => s.RunAsync(cts.Token))).ToList();

                try
                {
                    await Task.WhenAll(tasks);
                }
                catch (OperationCanceledException) { /* expected at end-of-run */ }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[stress] unexpected failure: {ex}");
                }
            }
            finally
            {
                if (channels != null)
                    foreach (var c in channels)
                        await c.DisposeAsync();
            }

            metrics.Render(Console.Out);
            Console.WriteLine($"[stress] Done. Clans this client created: {sharedClans.OwnCreatedCount}, total clans known (own + recommended): {sharedClans.KnownCount}");
        }
    }
}
