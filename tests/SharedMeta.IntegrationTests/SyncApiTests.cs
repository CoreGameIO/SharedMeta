using SharedMeta.Core;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Core.Network;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Coverage for [MetaMethod(Sync = SyncApi.Generate)] — generated synchronous API overload.
///
/// - Happy path: AddHeroSync applies the local mutation in the calling frame; the background
///   server round-trip lands shortly after and produces no desync.
/// - SyncPolicy.Throw: when IExecutionModeProvider overrides the effective mode away from
///   Optimistic/Local (as a downloaded config might), the sync overload throws
///   InvalidOperationException rather than silently executing locally and diverging.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SyncApiTests
{
    private readonly TestClusterFixture _fixture;

    public SyncApiTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_AddHeroSync_AppliesLocalStateImmediately()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        var playerId = "party-sync-add-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        // Sync call: returns without awaiting the server round-trip. Local state must
        // reflect the mutation before the next statement runs.
        api.AddHeroSync(1, "Aragorn", level: 5);

        var stateRightAfterSync = resolver.GetState<PartyState>(playerId);
        Assert.Single(stateRightAfterSync.Heroes);
        Assert.Equal("Aragorn", stateRightAfterSync.Heroes[0].Name);

        // Let the background fire-and-forget server round-trip finish and replay.
        await Task.Delay(150);

        var stateAfterServer = resolver.GetState<PartyState>(playerId);
        Assert.Single(stateAfterServer.Heroes);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_AwardExpSync_ThrowsWhenModeOverriddenToServer()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        // Simulate a downloaded config promoting AwardExp to Server mode at runtime.
        // SyncPolicy defaults to Throw → the sync overload must refuse to execute.
        var modeProvider = new ExecutionModeProvider()
            .SetMode(global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IPartyService_AwardExp_v0, ExecutionMode.Server);

        var playerId = "party-sync-throw-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics, modeProvider: modeProvider);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        // Seed via the async API — only AwardExp is overridden.
        await api.AddHeroAsync(1, "Aragorn", level: 5);
        await Task.Delay(50);

        var ex = Assert.Throws<InvalidOperationException>(() => api.AwardExpSync(heroId: 1, amount: 100));
        Assert.Contains("Sync invocation blocked", ex.Message);
        Assert.Contains("Server", ex.Message);

        // State is unchanged — the sync call threw before touching anything.
        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(0, state.Heroes[0].Exp);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_CountHeroesAtLevelSync_ReadsLocalStateWithoutRoundTrip()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        var playerId = "party-localquery-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        // Seed three heroes at levels 1, 5, 10 (sync writes apply to local State immediately).
        api.AddHeroSync(1, "Frodo", level: 1);
        api.AddHeroSync(2, "Aragorn", level: 5);
        api.AddHeroSync(3, "Gandalf", level: 10);

        // LocalQuery defaults to Sync = SyncApi.OnlySync: the generator emits only the synchronous
        // CountHeroesAtLevelSync(int) — there is no CountHeroesAtLevelAsync. It runs over local State
        // with no server round-trip and returns in the calling frame.
        Assert.Equal(3, api.CountHeroesAtLevelSync(1));
        Assert.Equal(2, api.CountHeroesAtLevelSync(5));
        Assert.Equal(1, api.CountHeroesAtLevelSync(10));
        Assert.Equal(0, api.CountHeroesAtLevelSync(99));

        // Explicit Sync = Generate emits BOTH overloads — sync and an async wrapper that completes
        // synchronously over local State (no RPC). Both read the same local snapshot.
        Assert.Equal(16, api.TotalLevelsSync());
        Assert.Equal(16, await api.TotalLevelsAsync());

        // Explicit Sync = None emits ONLY the async wrapper (no HeroCountSync), honouring the author's
        // choice; it too runs locally with no round-trip.
        Assert.Equal(3, await api.HeroCountAsync());

        // The background round-trips from the AddHeroSync seeds land cleanly; the LocalQuery reads
        // themselves issued no RPC and produced no desync.
        await Task.Delay(150);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    private sealed class DesyncCollector : IDesyncDiagnostics
    {
        public List<(string Service, string Method, uint ServerCrc, uint LocalCrc)> PatchDesyncs { get; } = new();

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult) { }
        public void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes) { }
        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta) { }
        public void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc)
            => PatchDesyncs.Add((serviceName, methodName, serverCrc, localCrc));
        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
            => Task.FromResult(new StateComparisonResult { IsMatch = true });
    }
}
