using ClanWars.Client.Common;

// Modern client. Advertises ClientAppVersion = "2.0.0" → server resolves ClanConfig 2.0.
// Native execution on every clan (v2 clients run the latest method bodies directly).
// When a v2 client creates a clan, the clan is pinned at config 2.0 — and any v1 client
// that later subscribes to that clan gets force-patch via the boundary, demonstrating the
// per-entity capability overlay end-to-end against a real cluster.

var options = new StressTestOptions
{
    ClientAppVersion = "2.0.0",
    PlayerPrefix = "v2",
};

ParseArgs(args, options);
await StressTestRunner.RunAsync(options);

static void ParseArgs(string[] args, StressTestOptions o)
{
    for (int i = 0; i < args.Length - 1; i += 2)
    {
        var key = args[i];
        var val = args[i + 1];
        switch (key)
        {
            case "--url": o.ServerUrl = val; break;
            case "--players": o.PlayerCount = int.Parse(val); break;
            case "--max-clans": o.MaxClans = int.Parse(val); break;
            case "--duration": o.DurationSeconds = int.Parse(val); break;
            case "--delay": o.MeanDelayMs = int.Parse(val); break;
            case "--prefix": o.PlayerPrefix = val; break;
            case "--mux-channels": o.MuxChannels = int.Parse(val); break;
            case "--mux-url": o.MuxServerUrl = val; break;
            case "--mux-batch": o.MuxBatching = bool.Parse(val); break;
            case "--mux-batch-size": o.MuxBatchSize = int.Parse(val); break;
            case "--mux-batch-flush": o.MuxBatchFlushMs = int.Parse(val); break;
        }
    }
}
