using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Orleans;
using Orleans.Configuration;
using Orleans.Hosting;
using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Framework;
using SharedMeta.Client;
using SharedMeta.Debug.InProcess;
using SharedMeta.Serialization.MemoryPack;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Orleans.Framework;
using CardGame.Shared;
using CardGame.Shared.Client;
using CardGame.Shared.Server;

// --- Parse arguments ---
int gameCount = 10;
int baseSeed = 42;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--games" or "-g" when i + 1 < args.Length:
            if (int.TryParse(args[++i], out var g)) gameCount = g;
            break;
        case "--seed" or "-s" when i + 1 < args.Length:
            if (int.TryParse(args[++i], out var s)) baseSeed = s;
            break;
        case "--help" or "-h":
            Console.WriteLine("Usage: CardGame.GameSimulation [options]");
            Console.WriteLine("  -g, --games <count>  Number of games to play (default: 10)");
            Console.WriteLine("  -s, --seed <seed>    Base seed for deterministic games (default: 42)");
            return;
    }
}

Console.WriteLine("=== The Fool - Game Simulation ===");
Console.WriteLine($"Games: {gameCount}, Base seed: {baseSeed}");
Console.WriteLine();

// --- Seeded random for deterministic shuffling ---
var seededRandom = new SeededRandomService(baseSeed);

// --- Build Orleans Silo ---
Console.WriteLine("Starting Orleans silo...");

var host = new HostBuilder()
    .ConfigureLogging(logging =>
    {
        logging.AddConsole();
        logging.SetMinimumLevel(LogLevel.Warning);
    })
    .UseOrleans(siloBuilder =>
    {
        siloBuilder
            .UseLocalhostClustering()
            .Configure<ClusterOptions>(options =>
            {
                options.ClusterId = "simulation-cluster";
                options.ServiceId = "simulation";
            })
            .AddMemoryGrainStorage("Default")
            .ConfigureServices(services =>
            {
                services.AddSingleton<IMetaSerializer>(new MemoryPackMetaSerializer());
                services.Configure<EntityGrainOptions>(o => o.SubscriberTtl = TimeSpan.FromMinutes(10));

                services.ConfigureMeta(svc =>
                {
                    svc.AddSingleton<IRandomService>(seededRandom);
                    svc.AddTransient<ILobbyRequester>(sp =>
                        new OrleansLobbyRequester(sp.GetRequiredService<IGrainFactory>()));
                });
            });
    })
    .Build();

await host.StartAsync();
Console.WriteLine("Silo started.");

// --- Create InProcessServer ---
var grainFactory = host.Services.GetRequiredService<IGrainFactory>();
var entityGrainResolver = host.Services.GetRequiredService<IEntityGrainResolver>();
var loggerFactory = host.Services.GetRequiredService<ILoggerFactory>();
var handlerFactory = new MetaConnectionHandlerFactory(grainFactory, entityGrainResolver, loggerFactory);
var inProcessServer = new InProcessServer(handlerFactory);

// --- Create 3 clients ---
var playerNames = new[] { "Alice", "Bob", "Charlie" };
var clients = new List<SimulationClient>();

Console.WriteLine("Creating 3 clients...");
for (int i = 0; i < 3; i++)
{
    var client = new SimulationClient(inProcessServer, playerNames[i]);
    await client.ConnectAsync();
    clients.Add(client);
}
Console.WriteLine("Clients connected.");
Console.WriteLine();

// --- Stats ---
var wins = new int[3];
var losses = new int[3];

// --- Game loop ---
for (int gameNum = 0; gameNum < gameCount; gameNum++)
{
    var seed = baseSeed + gameNum;
    seededRandom.Reset(seed);

    var gameEntityId = $"game_{gameNum}_{seed}";
    Console.WriteLine($"--- Game {gameNum + 1}/{gameCount} (seed: {seed}) ---");

    // Subscribe all clients to this game entity
    var apis = new CardGameServiceApiClient[3];
    var resolvers = new MetaServiceResolver[3];
    for (int i = 0; i < 3; i++)
    {
        resolvers[i] = clients[i].CreateResolver();
        apis[i] = await resolvers[i].GetServiceAsync<CardGameServiceApiClient>(gameEntityId);
    }

    // Register players — wait for each client to see prior registrations before registering.
    // This is needed because [OneWay] observer notifications are async (fire-and-forget).
    for (int i = 0; i < 3; i++)
    {
        if (i > 0)
        {
            // Wait until client i's state has all previous players registered
            var expectedCount = i;
            await WaitForCondition(resolvers[i], gameEntityId,
                s => s.Players.Count >= expectedCount,
                $"client {i} to see {expectedCount} players", clients);
        }
        await apis[i].RegisterPlayerAsync(playerNames[i], clients[i].PlayerId);
    }

    // Wait for all clients to see all 3 players
    for (int i = 0; i < 3; i++)
    {
        await WaitForCondition(resolvers[i], gameEntityId,
            s => s.Players.Count >= 3,
            $"client {i} to see 3 players", clients);
    }

    // Player 0 starts the game
    await apis[0].NewGameAsync(6);

    // Wait for all clients to see the game started
    for (int i = 0; i < 3; i++)
    {
        await WaitForCondition(resolvers[i], gameEntityId,
            s => s.Phase != GamePhase.NotStarted,
            $"client {i} to see game started", clients);
    }

    // Print trump
    var initState = resolvers[0].GetState<GameState>(gameEntityId);
    Console.WriteLine($"Trump: {initState.TrumpCard}");

    // Play loop — after each move, the acting client's state is guaranteed up-to-date
    // (RPC response includes main result + trigger operations, all replayed locally).
    // We track which client last acted and use their state as the "driver" for the next
    // iteration, avoiding stale state from [OneWay] broadcast timing.
    int moveCount = 0;
    const int maxMoves = 500; // safety limit
    int driverIdx = 0; // Start with client 0 as driver

    while (moveCount < maxMoves)
    {
        // Frame tick: process pending broadcasts on all clients
        foreach (var c in clients)
            c.ProcessBroadcasts();

        var state = resolvers[driverIdx].GetState<GameState>(gameEntityId);

        if (state.Phase == GamePhase.GameOver)
            break;

        var attackerId = state.CurrentAttackerId!.Value;
        var defenderId = state.CurrentDefenderId!.Value;

        var attackerClientIdx = FindClientIndex(state, clients);
        var defenderClientIdx = FindClientIndex(state, clients, defenderId);

        if (attackerClientIdx < 0 || defenderClientIdx < 0)
        {
            Console.Error.WriteLine($"[ERROR] Could not find client for attacker={attackerId}/defender={defenderId}");
            break;
        }

        if (state.Phase == GamePhase.Attacking)
        {
            // Wait for attacker's state to be in Attacking phase
            var attackerState = await WaitForCondition(resolvers[attackerClientIdx], gameEntityId,
                s => s.Phase == GamePhase.Attacking
                     && s.CurrentAttackerId == attackerId
                     && s.CurrentDefenderId == defenderId,
                $"attacker client {attackerClientIdx} to sync", clients);

            // If wait timed out and state moved past our expected phase, resync
            if (attackerState.Phase != GamePhase.Attacking || attackerState.CurrentAttackerId != attackerId)
            {
                driverIdx = attackerClientIdx;
                moveCount++;
                continue;
            }

            var hand = attackerState.PlayerHands[attackerId];

            if (attackerState.Table.Count > 0 && attackerState.Table.All(p => p.DefenseCard != null))
            {
                var card = FindAttackCard(hand, attackerState);
                if (card != null)
                    await apis[attackerClientIdx].AttackAsync(card);
                else
                    await apis[attackerClientIdx].EndAttackAsync();
            }
            else if (attackerState.Table.Count == 0)
            {
                var card = PickFirstAttackCard(hand, attackerState.TrumpSuit);
                await apis[attackerClientIdx].AttackAsync(card);
            }
            else
            {
                await apis[attackerClientIdx].EndAttackAsync();
            }

            driverIdx = attackerClientIdx;
        }
        else if (state.Phase == GamePhase.Defending)
        {
            // Wait for defender to see Defending phase with unbeaten cards,
            // OR accept that the round already advanced (trigger fired)
            var defenderState = await WaitForCondition(resolvers[defenderClientIdx], gameEntityId,
                s => (s.Phase == GamePhase.Defending && s.Table.Any(p => p.DefenseCard == null))
                     || s.Phase == GamePhase.GameOver
                     || (s.Phase == GamePhase.Attacking && s.Table.Count == 0),
                $"defender client {defenderClientIdx} to sync", clients);

            // If trigger already advanced the round or game ended, resync
            if (defenderState.Phase != GamePhase.Defending
                || !defenderState.Table.Any(p => p.DefenseCard == null))
            {
                driverIdx = defenderClientIdx;
                moveCount++;
                continue;
            }

            var hand = defenderState.PlayerHands[defenderId];
            var unbeaten = defenderState.Table.FirstOrDefault(p => p.DefenseCard == null);

            if (unbeaten != null)
            {
                var defenseCard = FindDefenseCard(hand, unbeaten.AttackCard, defenderState.TrumpSuit);
                if (defenseCard != null)
                    await apis[defenderClientIdx].DefendAsync(unbeaten.AttackCard, defenseCard);
                else
                    await apis[defenderClientIdx].TakeCardsAsync();
            }

            driverIdx = defenderClientIdx;
        }

        moveCount++;
        await Task.Yield();
    }

    if (moveCount >= maxMoves)
    {
        Console.WriteLine($"[WARNING] Game exceeded {maxMoves} moves, aborting.");
        continue;
    }

    // Wait for broadcasts to arrive via Orleans, then process them
    await Task.Delay(200);
    foreach (var c in clients)
        c.ProcessBroadcasts();

    // --- Validate convergence ---
    var finalStates = new GameState[3];
    for (int i = 0; i < 3; i++)
    {
        finalStates[i] = resolvers[i].GetState<GameState>(gameEntityId);
    }

    bool valid = true;

    // All must be GameOver
    for (int i = 0; i < 3; i++)
    {
        if (finalStates[i].Phase != GamePhase.GameOver)
        {
            Console.Error.WriteLine($"[FAIL] Client {i} ({playerNames[i]}): Phase={finalStates[i].Phase}, expected GameOver");
            valid = false;
        }
    }

    // All must have same LoserId
    if (valid)
    {
        var loserId0 = finalStates[0].LoserId;
        for (int i = 1; i < 3; i++)
        {
            if (finalStates[i].LoserId != loserId0)
            {
                Console.Error.WriteLine($"[FAIL] LoserId mismatch: client0={loserId0}, client{i}={finalStates[i].LoserId}");
                valid = false;
            }
        }
    }

    // All must have same Winners
    if (valid)
    {
        var winners0 = string.Join(",", finalStates[0].Winners.OrderBy(x => x));
        for (int i = 1; i < 3; i++)
        {
            var winnersI = string.Join(",", finalStates[i].Winners.OrderBy(x => x));
            if (winnersI != winners0)
            {
                Console.Error.WriteLine($"[FAIL] Winners mismatch: client0=[{winners0}], client{i}=[{winnersI}]");
                valid = false;
            }
        }
    }

    // Check desync issues
    var allIssues = clients.SelectMany(c => c.DetectedIssues).ToList();
    if (allIssues.Count > 0)
    {
        Console.Error.WriteLine($"[FAIL] Detected {allIssues.Count} desync issues:");
        foreach (var issue in allIssues)
            Console.Error.WriteLine($"  {issue}");
        valid = false;
    }

    // Print result
    var finalState = finalStates[0];
    if (finalState.LoserId.HasValue)
    {
        var loserName = finalState.Players[finalState.LoserId.Value].Name;
        Console.WriteLine($"[Result] {loserName} is the Fool! ({moveCount} moves)");

        // Update stats
        for (int i = 0; i < 3; i++)
        {
            var playerId = finalState.ClientToPlayer.Values.FirstOrDefault(pid =>
                finalState.Players[pid].Name == playerNames[i]);

            if (finalState.LoserId == playerId)
                losses[i]++;
            else
                wins[i]++;
        }
    }
    else
    {
        Console.WriteLine($"[Result] Draw! ({moveCount} moves)");
    }

    Console.WriteLine(valid
        ? "[Validation] All 3 clients converged. Zero desyncs."
        : "[Validation] FAILED — see errors above.");
    Console.WriteLine();

    // Clear issues for next game
    foreach (var c in clients) c.ClearIssues();
}

// --- Summary ---
Console.WriteLine("=== Summary ===");
Console.WriteLine($"Games played: {gameCount}");
for (int i = 0; i < 3; i++)
{
    Console.WriteLine($"  {playerNames[i]}: {wins[i]} wins, {losses[i]} losses");
}

// Cleanup
foreach (var c in clients) await c.DisposeAsync();
await host.StopAsync();

Console.WriteLine("Done.");

// ============================================
// Helper methods
// ============================================

static async Task<GameState> WaitForCondition(MetaServiceResolver resolver, string entityId,
    Func<GameState, bool> condition, string description, List<SimulationClient> allClients, int timeoutMs = 5000)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < timeoutMs)
    {
        // Frame tick: process pending broadcasts on all clients
        foreach (var c in allClients)
            c.ProcessBroadcasts();

        var state = resolver.GetState<GameState>(entityId);
        if (condition(state))
            return state;
        // Yield to let [OneWay] observer notifications propagate through the Orleans thread pool
        await Task.Delay(5);
    }

    var finalState = resolver.GetState<GameState>(entityId);
    Console.Error.WriteLine($"[WARN] WaitForCondition timeout ({description}): Phase={finalState.Phase}, Players={finalState.Players.Count}, Table={finalState.Table.Count}");
    return finalState;
}

static int FindClientIndex(GameState state, List<SimulationClient> clients, int? targetPlayerId = null)
{
    var pid = targetPlayerId ?? state.CurrentAttackerId!.Value;
    foreach (var (callerId, playerId) in state.ClientToPlayer)
    {
        if (playerId == pid)
        {
            // ClientToPlayer keys are CallerId (= PlayerId from session), not ConnectionId
            for (int i = 0; i < clients.Count; i++)
            {
                if (clients[i].PlayerId == callerId)
                    return i;
            }
        }
    }
    return -1;
}

static Card PickFirstAttackCard(List<Card> hand, Suit trumpSuit)
{
    // Prefer non-trump, lowest rank
    var nonTrump = hand.Where(c => c.Suit != trumpSuit).OrderBy(c => c.Rank).FirstOrDefault();
    return nonTrump ?? hand.OrderBy(c => c.Rank).First();
}

static Card? FindAttackCard(List<Card> hand, GameState state)
{
    // Can throw a card if its rank matches any rank on the table
    var tableRanks = new HashSet<Rank>();
    foreach (var pair in state.Table)
    {
        tableRanks.Add(pair.AttackCard.Rank);
        if (pair.DefenseCard != null)
            tableRanks.Add(pair.DefenseCard.Rank);
    }

    // Check if defender can still take more
    var defenderId = state.CurrentDefenderId!.Value;
    var defenderHand = state.PlayerHands[defenderId];
    var unbeatCount = state.Table.Count(p => p.DefenseCard == null);
    if (unbeatCount + 1 > defenderHand.Count)
        return null;

    // Find a matching card, prefer non-trump lowest rank
    return hand.Where(c => tableRanks.Contains(c.Rank))
               .OrderBy(c => c.Suit == state.TrumpSuit ? 1 : 0)
               .ThenBy(c => c.Rank)
               .FirstOrDefault();
}

static Card? FindDefenseCard(List<Card> hand, Card attackCard, Suit trumpSuit)
{
    // Same suit, higher rank
    var sameSuit = hand.Where(c => c.Suit == attackCard.Suit && c.Rank > attackCard.Rank)
                      .OrderBy(c => c.Rank)
                      .FirstOrDefault();
    if (sameSuit != null) return sameSuit;

    // Trump (if attack is not trump)
    if (attackCard.Suit != trumpSuit)
    {
        var trump = hand.Where(c => c.Suit == trumpSuit)
                       .OrderBy(c => c.Rank)
                       .FirstOrDefault();
        if (trump != null) return trump;
    }

    return null;
}

// ============================================
// SimulationClient — wraps MetaClient for the simulation
// ============================================

class SimulationClient : IAsyncDisposable
{
    private readonly MetaClient _client;
    private readonly List<string> _issues = new();

    public string PlayerName { get; }
    public string PlayerId => _client.PlayerId;
    public string ConnectionId => _client.ConnectionId;
    public IReadOnlyList<string> DetectedIssues => _issues;

    public SimulationClient(InProcessServer server, string playerName)
    {
        PlayerName = playerName;

        var diagnostics = new SimulationDesyncDiagnostics(_issues);

        _client = new MetaClient(
            server.CreateConnection(),
            new MemoryPackMetaSerializer(),
            new MetaClientOptions
            {
                PlayerId = playerName.ToLowerInvariant(),
                Diagnostics = diagnostics
            }
        );

        // Register transformers and services
        TransformerRegistrations.RegisterAll(_client.TransformerRegistry);
        _client.Resolver.RegisterAllServices();
    }

    public Task ConnectAsync() => _client.ConnectAsync();

    /// <summary>
    /// Process pending broadcasts (frame tick). Call from game loop.
    /// </summary>
    public int ProcessBroadcasts() => _client.Dispatcher.ProcessPendingBroadcasts();

    public MetaServiceResolver CreateResolver() => (MetaServiceResolver)_client.Resolver;

    public void ClearIssues() => _issues.Clear();

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    private class SimulationDesyncDiagnostics : IDesyncDiagnostics
    {
        private readonly List<string> _issues;

        public SimulationDesyncDiagnostics(List<string> issues) => _issues = issues;

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult)
        {
            var msg = $"[DESYNC] {serviceName}.{methodName}: server={serverResult}, local={localResult}";
            _issues.Add(msg);
            Console.Error.WriteLine(msg);
        }

        public void OnCrossEntityResult(string entityId, string serviceName, string methodName, byte[]? resultBytes)
        {
            // Log cross-entity results for debugging
        }

        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta)
        {
            var msg = $"[RANDOM DESYNC] {serviceName}.{methodName}: serverDelta={serverDelta}, localDelta={localDelta}";
            _issues.Add(msg);
            Console.Error.WriteLine(msg);
        }

        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
        {
            return Task.FromResult(new StateComparisonResult { IsMatch = true });
        }
    }
}
