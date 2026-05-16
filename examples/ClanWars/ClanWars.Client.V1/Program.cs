using ClanWars.Client.Common;

// Legacy client. Advertises ClientAppVersion = "1.0.0" → server resolves ClanConfig 1.0.
// On clans pinned at config 2.0 (created by v2 clients) the server force-patches the
// IClanService surface for this session via the [MetaConfigStructureBoundary("2.0")]
// declaration. ClanGetSummary / clan mutation latency will reflect the patch path.

var options = new StressTestOptions
{
    ClientAppVersion = "1.0.0",
    PlayerPrefix = "v1",
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
        }
    }
}
