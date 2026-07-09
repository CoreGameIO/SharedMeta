using SharedMeta.Core;
using SharedMeta.Core.Network;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;
using SharedMeta.Core.Reactive;
using SharedMeta.Client;
using SharedMeta.Serialization.MemoryPack;
using Expedition.Shared;
using Expedition.Shared.Client;
using SharedMeta.Serialization.MessagePack;
#if USE_HTTP_POLLING
using SharedMeta.Transport.HttpPolling;
#else
using SharedMeta.Transport.SignalR;
#endif

// Configure MessagePack with source-generated resolvers (must be before creating serializer)
GeneratedMetaMessagePackConfiguration.Configure();

// Setup logging
MetaLog.SetLogger(new ConsoleMetaLogger(MetaLogLevel.Info));

var useServerPatch = args.Contains("--server-patch");
// Client app version drives [MetaConfigVersion] branch resolution on the server.
// Default 2.0.0 matches the server's primary supported branch in Expedition.Server.
// Pass --client-version=1.2.0 to exercise the legacy branch (lean economy + schema gate).
var clientVersionArg = args.FirstOrDefault(a => a.StartsWith("--client-version="));
var clientAppVersion = clientVersionArg?.Split('=', 2)[1] ?? "2.0.0";
var positionalArgs = args.Where(a => !a.StartsWith("--")).ToArray();
var serverUrl = positionalArgs.Length > 0 ? positionalArgs[0] : "http://localhost:5100";
var deviceId = positionalArgs.Length > 1 ? positionalArgs[1] : Guid.NewGuid().ToString("N")[..8];

Console.OutputEncoding = System.Text.Encoding.UTF8;

// Step 1: Authenticate with DeviceId
Console.WriteLine($"Authenticating with DeviceId: {deviceId}...");
var login = await MetaClient.LoginAsync($"{serverUrl}/meta/auth", deviceId);
Console.WriteLine($"Authenticated as: {login.PlayerId} (new={login.IsNewPlayer})");

// Step 2: Connect with JWT token
var metaUrl = $"{serverUrl}/meta";

// ServerPatch mode: override CrossOptimistic methods to use server-side patching
IExecutionModeProvider? modeProvider = null;
if (useServerPatch) {
    var provider = new ExecutionModeProvider();
    provider.SetMode(global::Expedition.Shared.Generated.GameMethodIds.IExpeditionService_Move_v0, ExecutionMode.ServerPatch);
    provider.SetMode(global::Expedition.Shared.Generated.GameMethodIds.IExpeditionService_RemoveObstacle_v0, ExecutionMode.ServerPatch);
    modeProvider = provider;
    Console.WriteLine("ServerPatch mode ENABLED for Move and RemoveObstacle");
}

var client = new MetaClient(
#if USE_HTTP_POLLING
    new HttpPollingConnection(new HttpPollingConnectionOptions { ServerUrl = metaUrl, AccessToken = login.Token }),
#else
    new SignalRConnection(metaUrl, login.Token),
#endif
    new MessagePackMetaSerializer(),
    //new MemoryPackMetaSerializer(),
    new MetaClientOptions {
        PlayerId = login.PlayerId,
        // Required by ExpeditionConfig's [MetaConfigVersion(Client = "1.x.*"/"2.x.*")] rules:
        // server resolves the config branch per-subscribe from this string. Subscribe throws
        // server-side with "clientAppVersion is required" when null.
        ClientAppVersion = clientAppVersion,
        Diagnostics = new ConsoleDesyncDiagnostics(),
        ModeProvider = modeProvider,
        ClientSignature = GameServiceDiscoveryBase.ClientSignature,
    }
);
Console.WriteLine($"Client app version: {clientAppVersion}");

// Wire a downloading provider for ExpeditionConfig before registering services so the
// generator-emitted default StaticConfigProvider doesn't take precedence. The provider
// caches downloaded configs to disk so subsequent sessions skip the network round-trip.
client.RegisterDownloadingConfigProvider<ExpeditionConfig>(url => {
    var http = new HttpClient();
    return http.GetByteArrayAsync(url);
});
client.RegisterDownloadingConfigProvider<PlayerConfig>(url => {
    var http = new HttpClient();
    return http.GetByteArrayAsync(url);
});

client.Resolver.RegisterAllServices();

// Track connection status for UI
string connectionStatusMessage = "";
bool connectionFailed = false;
bool sessionSuperseded = false;

client.Dispatcher.OnConnectionStatusChanged += (status, detail) =>
{
    switch (status)
    {
        case ConnectionStatus.Reconnecting:
            connectionStatusMessage = "Connection lost. Reconnecting...";
            break;
        case ConnectionStatus.Reconnected:
            connectionStatusMessage = "Reconnected! Restoring session...";
            break;
        case ConnectionStatus.Connected:
            connectionStatusMessage = "";
            break;
        case ConnectionStatus.Failed:
            connectionStatusMessage = $"Connection failed: {detail}. Press Q to exit.";
            connectionFailed = true;
            break;
        case ConnectionStatus.Disconnected:
            connectionStatusMessage = "Disconnected. Waiting for reconnect...";
            break;
    }
};

client.OnSessionSuperseded += reason =>
{
    sessionSuperseded = true;
    connectionStatusMessage = $"Session taken over: {reason}";
};

// Register reactive subscriptions for push-based UI updates
TrackedProfileState.Register();
bool reactiveNeedsRender = false;
TrackedProfileState.OnChanged += args =>
{
    reactiveNeedsRender = true;
    if (args.HasChange((int)TrackingProperty.ProfileState_Energy))
    {
        var leaf = args.FindLeaf((int)TrackingProperty.ProfileState_Energy);
        if (leaf != null)
            Console.Title = $"Energy: {leaf.Value.OldValue.IntValue} -> {leaf.Value.NewValue.IntValue}";
    }
};

Console.WriteLine("Connecting to server...");
await client.ConnectAsync();
Console.WriteLine($"Connected! PlayerId: {client.PlayerId}");

// Outer loop: restart game session on supersede
while (true)
{
    sessionSuperseded = false;
    connectionFailed = false;
    connectionStatusMessage = "";

    bool shouldRestart;
    try
    {
        shouldRestart = await RunGameAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\nGame error: {ex.Message}");
        Console.Error.WriteLine(ex);
        if (sessionSuperseded)
        {
            // Session was superseded during gameplay — restart
            shouldRestart = true;
        }
        else
        {
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);
            break;
        }
    }

    if (!shouldRestart)
    {
        Console.WriteLine("Exiting (shouldRestart=false)...");
        break;
    }

    // Restart session
    Console.WriteLine("Restarting session...");
    try
    {
        await client.RestartSessionAsync();
        Console.WriteLine("Session restarted!");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to restart session: {ex.Message}");
        break;
    }
}

await client.DisposeAsync();

// === Game session logic ===

async Task<bool> RunGameAsync()
{
    var profileApi = await client.GetExpeditionProfileServiceAsync();

    await profileApi.UpdateEnergyAsync();

    var expeditionResult = await profileApi.ResumeOrStartExpeditionAsync();
    var expeditionEntityId = expeditionResult.EntityId;
    var expApi = await client.GetExpeditionServiceAsync(expeditionEntityId);

    Console.WriteLine(expeditionResult.IsNew
        ? $"New expedition: {expeditionEntityId}"
        : $"Resuming expedition: {expeditionEntityId}");

    string statusMessage = "Use arrow keys to move. R+arrow to remove obstacle. B to buy energy. Q to quit.";
    bool awaitingRemoveDirection = false;
    bool needsRender = true;
    bool supersededShown = false;

    // Game loop — simulates Unity Update() with frame-based broadcast processing.
    // ProcessPendingBroadcasts() drains queued server broadcasts on this thread,
    // ensuring state mutations happen on the same thread as API calls (no race conditions).
    while (true)
    {
        // --- Frame start: process pending server broadcasts ---
        if (client.Dispatcher.ProcessPendingBroadcasts() > 0)
            needsRender = true;

        // Reactive subscriptions may have triggered re-render
        if (reactiveNeedsRender)
        {
            needsRender = true;
            reactiveNeedsRender = false;
        }

        // Session superseded — only accept R (restart) or Q (quit)
        if (sessionSuperseded)
        {
            if (needsRender || !supersededShown)
            {
                Console.Clear();
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(connectionStatusMessage);
                Console.ResetColor();
                Console.WriteLine("Press R to restart session, Q to quit.");
                needsRender = false;
                supersededShown = true;
            }
            if (Console.KeyAvailable)
            {
                var superKey = Console.ReadKey(true);
                if (superKey.Key == ConsoleKey.R) return true;
                if (superKey.Key == ConsoleKey.Q) return false;
            }
            await Task.Delay(33);
            continue;
        }

        var expState = client.GetState<ExpeditionState>(expeditionEntityId);
        var profileState = client.GetProfileState();

        // Render when state may have changed
        if (needsRender)
        {
            var displayStatus = !string.IsNullOrEmpty(connectionStatusMessage)
                ? connectionStatusMessage
                : awaitingRemoveDirection ? "Direction? (arrow key):"
                : statusMessage;

            Render(expState, profileState, displayStatus);

            if (expState.IsComplete)
            {
                Console.WriteLine("\nCongratulations! All treasures collected!");
                Console.WriteLine("Press N for new expedition, Q to quit.");
            }
            needsRender = false;
        }

        // Handle input (non-blocking, like Unity Input.GetKeyDown)
        if (Console.KeyAvailable)
        {
            var key = Console.ReadKey(true);
            needsRender = true;
            statusMessage = "";

            if (connectionFailed)
            {
                if (key.Key == ConsoleKey.Q) { Console.WriteLine("\nGoodbye!"); return false; }
                needsRender = false;
            }
            else if (expState.IsComplete)
            {
                if (key.Key == ConsoleKey.N)
                {
                    expeditionResult = await profileApi.ResumeOrStartExpeditionAsync();
                    expeditionEntityId = expeditionResult.EntityId;
                    expApi = await client.GetExpeditionServiceAsync(expeditionEntityId);
                    statusMessage = $"New expedition: {expeditionEntityId}";
                }
                else if (key.Key == ConsoleKey.Q) return false;
                else needsRender = false;
            }
            else if (awaitingRemoveDirection)
            {
                awaitingRemoveDirection = false;
                int dx = 0, dy = 0;
                switch (key.Key)
                {
                    case ConsoleKey.UpArrow: dy = -1; break;
                    case ConsoleKey.DownArrow: dy = 1; break;
                    case ConsoleKey.LeftArrow: dx = -1; break;
                    case ConsoleKey.RightArrow: dx = 1; break;
                    default: statusMessage = "Invalid direction."; break;
                }
                if (dx != 0 || dy != 0)
                {
                    try
                    {
                        bool removed = await expApi.RemoveObstacleAsync(dx, dy);
                        statusMessage = removed ? "Obstacle removed! (-5 energy)" : "Cannot remove obstacle.";
                    }
                    catch (Exception ex)
                    {
                        if (sessionSuperseded) { await Task.Delay(33); continue; }
                        statusMessage = $"Error: {ex.Message}";
                    }
                }
            }
            else
            {
                try
                {
                    switch (key.Key)
                    {
                        case ConsoleKey.UpArrow: statusMessage = await DoMove(expApi, 0, -1); break;
                        case ConsoleKey.DownArrow: statusMessage = await DoMove(expApi, 0, 1); break;
                        case ConsoleKey.LeftArrow: statusMessage = await DoMove(expApi, -1, 0); break;
                        case ConsoleKey.RightArrow: statusMessage = await DoMove(expApi, 1, 0); break;
                        case ConsoleKey.R:
                            awaitingRemoveDirection = true;
                            break;
                        case ConsoleKey.B:
                            var bought = await profileApi.BuyEnergyAsync(10, 50);
                            statusMessage = bought ? "Bought 10 energy for 50 money!" : "Not enough money!";
                            break;
                        case ConsoleKey.U:
                            await profileApi.UpdateEnergyAsync();
                            statusMessage = "Energy updated.";
                            break;
                        case ConsoleKey.Q:
                            Console.WriteLine("\nGoodbye!");
                            return false;
                        default:
                            needsRender = false;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    if (sessionSuperseded) { await Task.Delay(33); continue; }
                    statusMessage = $"Error: {ex.Message}";
                }
            }
        }

        // Frame delay (~30 FPS for console rendering)
        await Task.Delay(33);
    }
}

// === Helper methods ===

static async Task<string> DoMove(ExpeditionServiceApiClient api, int dx, int dy)
{
    var result = await api.MoveAsync(dx, dy);
    return result switch
    {
        MoveResult.Ok => "",
        MoveResult.Treasure => "Found treasure! +25 money",
        MoveResult.NoEnergy => "Not enough energy! Press B to buy or U to regen.",
        MoveResult.Blocked => "Blocked!",
        MoveResult.OutOfBounds => "Edge of map!",
        MoveResult.Complete => "All treasures found!",
        _ => ""
    };
}

static void Render(ExpeditionState exp, ProfileState profile, string statusMessage)
{
    Console.Clear();

    int boxWidth = Math.Max(exp.Width * 2 + 3, 35);

    // Title
    var title = " Forest Expedition ";
    Console.WriteLine("+" + new string('-', boxWidth - 2) + "+");
    Console.WriteLine("|" + title.PadLeft((boxWidth - 2 + title.Length) / 2).PadRight(boxWidth - 2) + "|");
    Console.WriteLine("+" + new string('-', boxWidth - 2) + "+");

    // Map
    for (int y = 0; y < exp.Height; y++)
    {
        Console.Write("| ");
        for (int x = 0; x < exp.Width; x++)
        {
            int idx = y * exp.Width + x;

            if (x == exp.PlayerX && y == exp.PlayerY)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("@ ");
                Console.ResetColor();
                continue;
            }

            if (!exp.Revealed[idx])
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write("~ ");
                Console.ResetColor();
                continue;
            }

            var cell = (CellType)exp.Cells[idx];
            switch (cell)
            {
                case CellType.Empty:
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.Write(". ");
                    break;
                case CellType.Wall:
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.Write("# ");
                    break;
                case CellType.Obstacle:
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write("X ");
                    break;
                case CellType.Treasure:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("$ ");
                    break;
            }
            Console.ResetColor();
        }
        // Pad remaining space
        var mapChars = exp.Width * 2;
        var remaining = boxWidth - 3 - mapChars;
        Console.Write(new string(' ', Math.Max(0, remaining)));
        Console.WriteLine("|");
    }

    Console.WriteLine("+" + new string('-', boxWidth - 2) + "+");

    // Stats
    var energyBar = $" Energy: {profile.Energy}/{profile.MaxEnergy}    Money: {profile.Money}";
    Console.WriteLine("|" + energyBar.PadRight(boxWidth - 2) + "|");

    var treasureBar = $" Treasures: {exp.TreasuresCollected}/{exp.TotalTreasures}";
    Console.WriteLine("|" + treasureBar.PadRight(boxWidth - 2) + "|");

    Console.WriteLine("+" + new string('-', boxWidth - 2) + "+");

    // Controls
    Console.WriteLine("|" + " Arrows=Move  R+arrow=Remove obstacle ".PadRight(boxWidth - 2) + "|");
    Console.WriteLine("|" + " B=Buy energy  U=Update energy  Q=Quit".PadRight(boxWidth - 2) + "|");
    Console.WriteLine("+" + new string('-', boxWidth - 2) + "+");

    // Status message
    if (!string.IsNullOrEmpty(statusMessage))
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(statusMessage);
        Console.ResetColor();
    }
}

class ConsoleDesyncDiagnostics : IDesyncDiagnostics
{
    public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult)
    {
        Console.Error.WriteLine($"[DESYNC] {serviceName}.{methodName}: server={serverResult}, local={localResult}");
    }

    public void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes)
    {
        // Expected for CrossOptimistic calls
    }

    public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta)
    {
        Console.Error.WriteLine($"[RANDOM DESYNC] {serviceName}.{methodName}: serverDelta={serverDelta}, localDelta={localDelta}");
    }

    public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
    {
        return Task.FromResult(new StateComparisonResult { IsMatch = true });
    }
}
