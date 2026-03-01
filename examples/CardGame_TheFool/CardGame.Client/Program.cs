using System.Linq;
using System.Text;
using Spectre.Console;
using CardGame.Client;
using CardGame.Shared;
using CardGame.Shared.Client;
using SharedMeta.Client;
using SharedMeta.Core.Logging;
using SharedMeta.Core.Transport;

Console.OutputEncoding = Encoding.UTF8;

// Configure MessagePack with source-generated resolvers (must be before creating serializer/SignalR)
GeneratedMetaMessagePackConfiguration.Configure();

MetaLog.SetLogger(new ConsoleMetaLogger(MetaLogLevel.Info));

// Connection state (shared across modes)
string connectionStatusMessage = "";
bool connectionFailed = false;
bool sessionSuperseded = false;

// Parse command-line arguments
var serverUrl = "http://localhost:5000/meta";
var playerName = "Player";
var playerCount = 2;
var mode = "interactive"; // interactive, test-connect, test-match, auto

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server" or "-s" when i + 1 < args.Length:
            serverUrl = args[++i];
            break;
        case "--name" or "-n" when i + 1 < args.Length:
            playerName = args[++i];
            break;
        case "--players" or "-p" when i + 1 < args.Length:
            if (int.TryParse(args[++i], out var pc)) playerCount = pc;
            break;
        case "--test-connect":
            mode = "test-connect";
            break;
        case "--test-match":
            mode = "test-match";
            break;
        case "--auto":
            mode = "auto";
            break;
        case "--help" or "-h":
            ShowHelp();
            return;
        default:
            // Legacy positional args support
            if (i == 0 && !args[i].StartsWith("-"))
                serverUrl = args[i];
            else if (i == 1 && !args[i].StartsWith("-"))
                playerName = args[i];
            else if (i == 2 && !args[i].StartsWith("-") && int.TryParse(args[i], out var pc2))
                playerCount = pc2;
            break;
    }
}

AnsiConsole.MarkupLine("[bold magenta]CardGame Client[/]");
AnsiConsole.MarkupLine($"Server: [cyan]{serverUrl}[/]");
AnsiConsole.MarkupLine($"Player: [green]{playerName}[/]");
AnsiConsole.MarkupLine($"Mode: [yellow]{mode}[/]");
AnsiConsole.WriteLine();

try
{
    switch (mode)
    {
        case "test-connect":
            await TestConnection(serverUrl);
            break;
        case "test-match":
            await TestMatchmaking(serverUrl, playerName, playerCount);
            break;
        case "auto":
            await RunAutoMode(serverUrl, playerName, playerCount);
            break;
        default:
            await RunInteractiveMode(serverUrl, playerName, playerCount);
            break;
    }
}
catch (Exception ex)
{
    AnsiConsole.MarkupLine($"[red]Error: {Markup.Escape(ex.Message)}[/]");
    Console.Error.WriteLine(ex.StackTrace);
}

void ShowHelp()
{
    AnsiConsole.MarkupLine("[bold]Usage:[/] CardGame.Client [options]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Options:[/]");
    AnsiConsole.MarkupLine("  -s, --server <url>     Server URL (default: http://localhost:5000/meta)");
    AnsiConsole.MarkupLine("  -n, --name <name>      Player name (default: Player)");
    AnsiConsole.MarkupLine("  -p, --players <count>  Player count for matchmaking (default: 2)");
    AnsiConsole.MarkupLine("  --test-connect         Test connection only");
    AnsiConsole.MarkupLine("  --test-match           Test matchmaking only");
    AnsiConsole.MarkupLine("  --auto                 Auto-play mode (makes random moves)");
    AnsiConsole.MarkupLine("  -h, --help             Show this help");
}

void WireConnectionEvents(ProductionClientSetup setup)
{
    setup.Client.Dispatcher.OnConnectionStatusChanged += (status, detail) =>
    {
        switch (status)
        {
            case ConnectionStatus.Reconnecting:
                connectionStatusMessage = "[yellow]Connection lost. Reconnecting...[/]";
                break;
            case ConnectionStatus.Reconnected:
                connectionStatusMessage = "[cyan]Reconnected! Restoring session...[/]";
                break;
            case ConnectionStatus.Connected:
                connectionStatusMessage = "";
                break;
            case ConnectionStatus.Failed:
                connectionStatusMessage = $"[red]Connection failed: {Markup.Escape(detail ?? "unknown")}[/]";
                connectionFailed = true;
                break;
            case ConnectionStatus.Disconnected:
                connectionStatusMessage = "[yellow]Disconnected. Waiting for reconnect...[/]";
                break;
        }
    };

    setup.Client.OnSessionSuperseded += reason =>
    {
        sessionSuperseded = true;
        connectionStatusMessage = $"[yellow]Session taken over: {Markup.Escape(reason)}[/]";
    };
}

async Task TestConnection(string url)
{
    AnsiConsole.MarkupLine("[yellow]Testing connection...[/]");

    await using var setup = new ProductionClientSetup(url);
    await setup.ConnectAsync();

    AnsiConsole.MarkupLine($"[green]Connected! ConnectionId: {setup.ConnectionId}[/]");

    // Test subscribing to profile entity
    var resolver = setup.CreateResolver();
    var profileApi = await resolver.GetServiceAsync<ProfileServiceApiClient>(setup.PlayerId);

    AnsiConsole.MarkupLine($"[green]Subscribed to profile entity: {setup.PlayerId}[/]");

    // Test setting name
    await profileApi.SetNameAsync("TestPlayer");
    var state = resolver.GetState<ProfileState>(setup.PlayerId);

    AnsiConsole.MarkupLine($"[green]Profile name set to: {state.DisplayName}[/]");
    AnsiConsole.MarkupLine("[bold green]Connection test PASSED![/]");
}

async Task TestMatchmaking(string url, string name, int players)
{
    AnsiConsole.MarkupLine($"[yellow]Testing matchmaking ({players} players)...[/]");
    AnsiConsole.MarkupLine("[yellow]NOTE: You need to run multiple clients for matchmaking to complete[/]");

    await using var setup = new ProductionClientSetup(url);
    await setup.ConnectAsync();

    var resolver = setup.CreateResolver();
    var profileApi = await resolver.GetServiceAsync<ProfileServiceApiClient>(setup.PlayerId);
    await profileApi.SetNameAsync(name);

    string? matchId = null;
    profileApi.OnMatchFound_Replayed += e =>
    {
        AnsiConsole.MarkupLine($"[green]Match found: {e.MatchId}[/]");
        matchId = e.MatchId;
    };

    AnsiConsole.MarkupLine($"[yellow]Requesting match for {players} players...[/]");
    await profileApi.RequestMatchAsync(players);

    var state = resolver.GetState<ProfileState>(setup.PlayerId);
    AnsiConsole.MarkupLine($"[cyan]Searching: {state.IsSearching}[/]");
    AnsiConsole.MarkupLine($"[cyan]Game mode: {state.SearchGameMode}[/]");

    // Wait for match with polling (must call ProcessPendingBroadcasts to receive events)
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (matchId == null && DateTime.UtcNow < deadline)
    {
        setup.Client.Dispatcher.ProcessPendingBroadcasts();
        await Task.Delay(33);
    }

    if (matchId == null)
    {
        AnsiConsole.MarkupLine("[yellow]Matchmaking timeout (30s) - need more players[/]");
        AnsiConsole.MarkupLine("[bold yellow]Matchmaking test PARTIAL - waiting for players[/]");
    }
    else
    {
        AnsiConsole.MarkupLine($"[green]Game created: {matchId}[/]");
        AnsiConsole.MarkupLine("[bold green]Matchmaking test PASSED![/]");
    }
}

async Task RunAutoMode(string url, string name, int players)
{
    AnsiConsole.MarkupLine("[yellow]Auto-play mode[/]");

    await using var setup = new ProductionClientSetup(url);
    await setup.ConnectAsync();
    WireConnectionEvents(setup);

    var resolver = setup.CreateResolver();
    var profileApi = await resolver.GetServiceAsync<ProfileServiceApiClient>(setup.PlayerId);
    await profileApi.SetNameAsync(name);

    string? gameId = null;
    profileApi.OnMatchFound_Replayed += e =>
    {
        AnsiConsole.MarkupLine($"[green]Match found: {e.MatchId}[/]");
        gameId = e.MatchId;
    };

    await profileApi.RequestMatchAsync(players);
    AnsiConsole.MarkupLine($"[yellow]Waiting for {players}-player game...[/]");

    // Poll for match (must call ProcessPendingBroadcasts to receive events)
    var deadline = DateTime.UtcNow.AddMinutes(5);
    while (gameId == null)
    {
        setup.Client.Dispatcher.ProcessPendingBroadcasts();
        if (DateTime.UtcNow > deadline)
        {
            AnsiConsole.MarkupLine("[red]Matchmaking timed out[/]");
            return;
        }
        await Task.Delay(33);
    }

    // Join game
    var gameApi = await resolver.GetServiceAsync<CardGameServiceApiClient>(gameId);
    await gameApi.RegisterPlayerAsync(name, setup.PlayerId);

    AnsiConsole.MarkupLine("[green]Joined game![/]");

    // Auto-play loop — simulates game loop with frame-based broadcast processing
    var random = new Random();
    while (true)
    {
        // Frame tick: process server broadcasts
        setup.Client.Dispatcher.ProcessPendingBroadcasts();

        if (connectionFailed)
        {
            AnsiConsole.MarkupLine(connectionStatusMessage);
            break;
        }
        if (sessionSuperseded)
        {
            AnsiConsole.MarkupLine(connectionStatusMessage);
            break;
        }

        var state = resolver.GetState<GameState>(gameId);

        // Find our player index
        var playerIndex = -1;
        if (state.ClientToPlayer.TryGetValue(setup.ConnectionId, out var idx))
            playerIndex = idx;

        if (playerIndex < 0)
        {
            AnsiConsole.MarkupLine("[dim]Waiting for game to start...[/]");
            await Task.Delay(1000);
            continue;
        }

        // Check if game ended
        if (state.Phase == GamePhase.GameOver)
        {
            AnsiConsole.MarkupLine("[bold]Game Over![/]");
            if (state.LoserId.HasValue)
            {
                var loser = state.Players.FirstOrDefault(p => p.Id == state.LoserId.Value);
                AnsiConsole.MarkupLine($"[red]Loser (The Fool): {loser?.Name}[/]");
            }
            break;
        }

        // Start game if we have enough players and game not started
        if (state.Phase == GamePhase.NotStarted && state.Players.Count >= 2)
        {
            // Only first player starts the game to avoid race condition
            if (playerIndex == 0)
            {
                AnsiConsole.MarkupLine("[cyan]Auto: Starting game...[/]");
                await gameApi.NewGameAsync(6);
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]Waiting for game to start...[/]");
            }
            await Task.Delay(1000);
            continue;
        }

        if (!string.IsNullOrEmpty(connectionStatusMessage))
            AnsiConsole.MarkupLine(connectionStatusMessage);

        var hand = state.PlayerHands[playerIndex];
        var isAttacker = state.Phase == GamePhase.Attacking && state.CurrentAttackerId == playerIndex;
        var isDefender = state.Phase == GamePhase.Defending && state.CurrentDefenderId == playerIndex;

        if (isAttacker && hand.Count > 0)
        {
            // Attack with random card or end attack
            if (state.Table.Count > 0 && random.Next(3) == 0)
            {
                AnsiConsole.MarkupLine("[cyan]Auto: Ending attack[/]");
                await gameApi.EndAttackAsync();
            }
            else
            {
                var card = hand[random.Next(hand.Count)];
                AnsiConsole.MarkupLine($"[cyan]Auto: Attacking with {FormatCard(card)}[/]");
                try
                {
                    await gameApi.AttackAsync(card);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[dim]Attack failed: {Markup.Escape(ex.Message)}[/]");
                    await gameApi.EndAttackAsync();
                }
            }
        }
        else if (isDefender && hand.Count > 0)
        {
            // Try to defend or take cards
            var undefended = state.Table.FirstOrDefault(p => p.DefenseCard == null);
            if (undefended != null)
            {
                // Try to find a valid defense card
                var defenseCard = FindDefenseCard(hand, undefended.AttackCard, state.TrumpCard?.Suit);
                if (defenseCard != null)
                {
                    AnsiConsole.MarkupLine($"[cyan]Auto: Defending with {FormatCard(defenseCard)}[/]");
                    try
                    {
                        await gameApi.DefendAsync(undefended.AttackCard, defenseCard);
                    }
                    catch
                    {
                        AnsiConsole.MarkupLine("[dim]Defense failed, taking cards[/]");
                        await gameApi.TakeCardsAsync();
                    }
                }
                else
                {
                    AnsiConsole.MarkupLine("[cyan]Auto: Taking cards (no valid defense)[/]");
                    await gameApi.TakeCardsAsync();
                }
            }
        }

        // Frame delay
        await Task.Delay(500);
    }
}

Card? FindDefenseCard(List<Card> hand, Card attackCard, Suit? trumpSuit)
{
    // First try same suit, higher rank
    var sameSuit = hand.Where(c => c.Suit == attackCard.Suit && c.Rank > attackCard.Rank)
                      .OrderBy(c => c.Rank)
                      .FirstOrDefault();
    if (sameSuit != null) return sameSuit;

    // Then try trump (if attack is not trump)
    if (trumpSuit.HasValue && attackCard.Suit != trumpSuit.Value)
    {
        var trump = hand.Where(c => c.Suit == trumpSuit.Value)
                       .OrderBy(c => c.Rank)
                       .FirstOrDefault();
        if (trump != null) return trump;
    }

    return null;
}

async Task RunInteractiveMode(string url, string name, int players)
{
    await using var setup = new ProductionClientSetup(url);
    await setup.ConnectAsync();
    WireConnectionEvents(setup);

    // Outer loop: restart session on supersede
    while (true)
    {
        sessionSuperseded = false;
        connectionFailed = false;
        connectionStatusMessage = "";

        var resolver = setup.CreateResolver();
        var profileApi = await resolver.GetServiceAsync<ProfileServiceApiClient>(setup.PlayerId);
        await profileApi.SetNameAsync(name);

        string? gameId = null;
        profileApi.OnMatchFound_Replayed += e =>
        {
            AnsiConsole.MarkupLine($"[green]Match found: {e.MatchId}[/]");
            gameId = e.MatchId;
        };

        AnsiConsole.MarkupLine($"[yellow]Looking for {players}-player game...[/]");
        await profileApi.RequestMatchAsync(players);

        // Poll for match (must call ProcessPendingBroadcasts to receive events)
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (gameId == null)
        {
            setup.Client.Dispatcher.ProcessPendingBroadcasts();
            if (DateTime.UtcNow > deadline)
            {
                AnsiConsole.MarkupLine("[red]Matchmaking timed out[/]");
                return;
            }
            await Task.Delay(33);
        }

        // Join game
        var gameApi = await resolver.GetServiceAsync<CardGameServiceApiClient>(gameId);
        await gameApi.RegisterPlayerAsync(name, setup.PlayerId);

        AnsiConsole.MarkupLine("[green]Joined game![/]");

        bool shouldRestart;
        try
        {
            shouldRestart = await RunGameLoop(resolver, gameId, gameApi, setup);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Game error: {Markup.Escape(ex.Message)}[/]");
            if (sessionSuperseded)
            {
                shouldRestart = true;
            }
            else
            {
                AnsiConsole.MarkupLine("[dim]Press any key to exit...[/]");
                Console.ReadKey(true);
                return;
            }
        }

        if (!shouldRestart)
            return;

        // Restart session
        AnsiConsole.MarkupLine("[yellow]Restarting session...[/]");
        try
        {
            await setup.Client.RestartSessionAsync();
            AnsiConsole.MarkupLine("[green]Session restarted![/]");
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to restart: {Markup.Escape(ex.Message)}[/]");
            return;
        }
    }
}

async Task<bool> RunGameLoop(MetaServiceResolver resolver, string gameId, CardGameServiceApiClient gameApi, ProductionClientSetup setup)
{
    var playerIndex = -1;
    while (playerIndex < 0)
    {
        // Frame tick: process server broadcasts
        setup.Client.Dispatcher.ProcessPendingBroadcasts();

        if (connectionFailed || sessionSuperseded)
            return sessionSuperseded;

        var state = resolver.GetState<GameState>(gameId);

        if (state.ClientToPlayer.TryGetValue(setup.PlayerId, out var idx)) {
            playerIndex = idx;
        }

        if (playerIndex < 0) {
            AnsiConsole.MarkupLine("[yellow]Waiting for game to start...[/]");
            await Task.Delay(1000);
        }
    }

    AnsiConsole.MarkupLine($"[green] Player index {playerIndex} [/]");

    while (true)
    {
        // Frame tick: process server broadcasts
        setup.Client.Dispatcher.ProcessPendingBroadcasts();

        // Handle session supersede — offer restart
        if (sessionSuperseded)
        {
            Console.Clear();
            AnsiConsole.MarkupLine(connectionStatusMessage);
            AnsiConsole.MarkupLine("[dim]Press R to restart session, Q to quit.[/]");

            while (Console.KeyAvailable) Console.ReadKey(true);
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.R) return true;  // restart
                if (key.Key == ConsoleKey.Q) return false; // quit
            }
        }

        // Handle connection failure — wait for Q
        if (connectionFailed)
        {
            Console.Clear();
            AnsiConsole.MarkupLine(connectionStatusMessage);
            AnsiConsole.MarkupLine("[dim]Press Q to exit.[/]");
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Q) return false;
            }
        }

        var state = resolver.GetState<GameState>(gameId);

        Console.Clear();
        DisplayGameState(state, playerIndex);

        if (state.Phase == GamePhase.GameOver)
        {
            AnsiConsole.MarkupLine("[bold]Game Over![/]");
            return false;
        }

        // Auto-start game when enough players have registered
        if (state.Phase == GamePhase.NotStarted)
        {
            if (state.Players.Count >= 2)
            {
                if (playerIndex == 0)
                {
                    AnsiConsole.MarkupLine("[cyan]Starting game...[/]");
                    await gameApi.NewGameAsync(6);
                }
                else
                {
                    AnsiConsole.MarkupLine($"[dim]Waiting for Player 0 to start... ({state.Players.Count} players registered)[/]");
                    await Task.Delay(1000);
                }
            }
            else
            {
                AnsiConsole.MarkupLine($"[dim]Waiting for players... ({state.Players.Count}/{2} registered)[/]");
                await Task.Delay(1000);
            }
            continue;
        }

        var isOurTurn = (state.Phase == GamePhase.Attacking && state.CurrentAttackerId == playerIndex) ||
                       (state.Phase == GamePhase.Defending && state.CurrentDefenderId == playerIndex);

        if (isOurTurn)
        {
            await HandlePlayerTurn(gameApi, state, playerIndex);
        }
        else
        {
            AnsiConsole.MarkupLine("[dim]Waiting for other player's turn...[/]");
            await Task.Delay(1000);
        }
    }
}

void DisplayGameState(GameState state, int playerIndex)
{
    if (!string.IsNullOrEmpty(connectionStatusMessage))
        AnsiConsole.MarkupLine(connectionStatusMessage);

    var player = state.Players[playerIndex];

    AnsiConsole.MarkupLine($"[bold]Phase: {state.Phase}  Player {playerIndex} [/]");
    AnsiConsole.MarkupLine($"Trump: {FormatCard(state.TrumpCard)} | Deck: {state.Deck.Count} cards");
    AnsiConsole.WriteLine();

    AnsiConsole.MarkupLine("[bold]Table:[/]");
    if (state.Table.Count == 0)
    {
        AnsiConsole.MarkupLine("[dim]Empty[/]");
    }
    else
    {
        foreach (var pair in state.Table)
        {
            var defense = pair.DefenseCard != null ? FormatCard(pair.DefenseCard) : "[dim]?[/]";
            AnsiConsole.MarkupLine($"  {FormatCard(pair.AttackCard)} <- {defense}");
        }
    }
    AnsiConsole.WriteLine();

    AnsiConsole.MarkupLine("[bold]Your hand:[/]");
    var hand = state.PlayerHands[playerIndex];
    for (int i = 0; i < hand.Count; i++)
    {
        AnsiConsole.MarkupLine($"  {i + 1}: {FormatCard(hand[i])}");
    }
    AnsiConsole.WriteLine();
}

string FormatCard(Card? card)
{
    if (card == null) return "[dim]none[/]";

    var suitSymbol = card.Suit switch
    {
        Suit.Hearts => "[red]H[/]",
        Suit.Diamonds => "[red]D[/]",
        Suit.Clubs => "C",
        Suit.Spades => "S",
        _ => "?"
    };

    var rankStr = card.Rank switch
    {
        Rank.Six => "6",
        Rank.Seven => "7",
        Rank.Eight => "8",
        Rank.Nine => "9",
        Rank.Ten => "T",
        Rank.Jack => "J",
        Rank.Queen => "Q",
        Rank.King => "K",
        Rank.Ace => "A",
        _ => "?"
    };

    return $"{rankStr}{suitSymbol}";
}

async Task HandlePlayerTurn(CardGameServiceApiClient gameApi, GameState state, int playerIndex)
{
    var hand = state.PlayerHands[playerIndex];

    if (state.Phase == GamePhase.Attacking)
    {
        AnsiConsole.MarkupLine("[bold green]Your turn to attack![/]");
        AnsiConsole.MarkupLine("Enter card number (1-{0}) or 'e' to end attack:", hand.Count);

        var input = Console.ReadLine()?.Trim().ToLower();

        if (input == "e")
        {
            await gameApi.EndAttackAsync();
        }
        else if (int.TryParse(input, out var cardNum) && cardNum >= 1 && cardNum <= hand.Count)
        {
            var card = hand[cardNum - 1];
            await gameApi.AttackAsync(card);
        }
    }
    else if (state.Phase == GamePhase.Defending)
    {
        AnsiConsole.MarkupLine("[bold yellow]Your turn to defend![/]");
        AnsiConsole.MarkupLine("Enter card number (1-{0}) or 't' to take cards:", hand.Count);

        var input = Console.ReadLine()?.Trim().ToLower();

        if (input == "t")
        {
            await gameApi.TakeCardsAsync();
        }
        else if (int.TryParse(input, out var cardNum) && cardNum >= 1 && cardNum <= hand.Count)
        {
            var defenseCard = hand[cardNum - 1];
            var undefendedPair = state.Table.FirstOrDefault(p => p.DefenseCard == null);
            if (undefendedPair != null)
            {
                await gameApi.DefendAsync(undefendedPair.AttackCard, defenseCard);
            }
        }
    }
}
