using SharedMeta.Core.Diagnostics;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Who deep desync analysis is switched on for, and what the client does about it.
/// <para>
/// The service under test carries <c>[MetaServiceImpl(..., DeepDesync = true)]</c> and has a
/// deliberately non-deterministic method, so it diverges on every call. That makes "was anything
/// reported?" a direct read on whether the client bothered to track and compare at all.
/// </para>
/// </summary>
[Collection(TestClusterCollection.Name)]
public class DeepDesyncActivationTests
{
    private readonly TestClusterFixture _fixture;

    public DeepDesyncActivationTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// The regression this whole change exists for: with the analysis off the client must not build
    /// a patch tree, and must therefore report nothing — even though the silo is still computing
    /// CRCs and the call genuinely diverges. Delete the runtime branch in the generated client and
    /// this fails, because the untracked local CRC of 0 would be compared against a real one.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnalysisOff_DivergingCall_ReportsNothing()
    {
        var diagnostics = new DesyncCollector();
        var server = new InProcessServer(_fixture.CreateHandlerFactory(deepDesyncMode: DeepDesyncMode.Off));

        await using var client = new TestClientSetup(server, NewPlayerId("dd-off"), diagnostics: diagnostics);
        await client.ConnectAsync();

        var api = await client.CreateResolver().GetServiceAsync<DesyncTestServiceApiClient>(client.PlayerId);
        await api.AddNonDeterministicAsync();
        await Task.Delay(200);

        Assert.False(client.MetaClient.Dispatcher.DeepDesyncActive);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    /// <summary>
    /// PerPlayer with nobody flagged is the default posture of a silo that ships the capability but
    /// isn't using it — it must behave exactly like Off.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PerPlayer_PlayerNotFlagged_ReportsNothing()
    {
        var diagnostics = new DesyncCollector();
        var server = new InProcessServer(_fixture.CreateHandlerFactory(deepDesyncMode: DeepDesyncMode.PerPlayer));

        await using var client = new TestClientSetup(server, NewPlayerId("dd-unflagged"), diagnostics: diagnostics);
        await client.ConnectAsync();

        var api = await client.CreateResolver().GetServiceAsync<DesyncTestServiceApiClient>(client.PlayerId);
        await api.AddNonDeterministicAsync();
        await Task.Delay(200);

        Assert.False(client.MetaClient.Dispatcher.DeepDesyncActive);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    /// <summary>
    /// The same silo, the same build, one flagged player — and now the divergence is reported.
    /// The flag is written before the session connects because the verdict is resolved once, there.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PerPlayer_PlayerFlagged_ReportsTheDivergence()
    {
        var playerId = NewPlayerId("dd-flagged");
        await _fixture.GrainFactory.GetGrain<IDesyncReportGrain>(playerId).SetAnalysisEnabledAsync(true);

        var diagnostics = new DesyncCollector();
        var server = new InProcessServer(_fixture.CreateHandlerFactory(deepDesyncMode: DeepDesyncMode.PerPlayer));

        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var api = await client.CreateResolver().GetServiceAsync<DesyncTestServiceApiClient>(client.PlayerId);
        await api.AddNonDeterministicAsync();
        await Task.Delay(200);

        Assert.True(client.MetaClient.Dispatcher.DeepDesyncActive);
        Assert.NotEmpty(diagnostics.PatchDesyncs);
    }

    /// <summary>
    /// Off outranks the client. Before this change the two were OR-ed, so a client could switch CRC
    /// computation back on against the operator's kill switch.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task AnalysisOff_ClientRequest_IsRefused()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory(deepDesyncMode: DeepDesyncMode.Off));

        await using var client = new TestClientSetup(server, NewPlayerId("dd-refused"));
        await client.ConnectAsync();

        Assert.False(await client.MetaClient.SetDeepDesyncAsync(true));
        Assert.False(client.MetaClient.Dispatcher.DeepDesyncActive);
    }

    /// <summary>
    /// Switching the analysis on mid-session takes effect on this session, and on calls issued from
    /// that point on — no reconnect. The same divergence goes unreported before and is reported
    /// after, which is what makes this a statement about the switch rather than about the method.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PerPlayer_EnabledMidSession_ReportsFromThatPointOn()
    {
        var diagnostics = new DesyncCollector();
        var server = new InProcessServer(_fixture.CreateHandlerFactory(deepDesyncMode: DeepDesyncMode.PerPlayer));

        await using var client = new TestClientSetup(server, NewPlayerId("dd-mid"), diagnostics: diagnostics);
        await client.ConnectAsync();

        var api = await client.CreateResolver().GetServiceAsync<DesyncTestServiceApiClient>(client.PlayerId);
        await api.AddNonDeterministicAsync();
        await Task.Delay(200);
        Assert.Empty(diagnostics.PatchDesyncs);

        Assert.True(await client.MetaClient.SetDeepDesyncAsync(true));
        Assert.True(client.MetaClient.Dispatcher.DeepDesyncActive);

        await api.AddNonDeterministicAsync();
        await Task.Delay(200);
        Assert.NotEmpty(diagnostics.PatchDesyncs);
    }

    /// <summary>
    /// The request is also stored against the player, not the connection, so an investigation
    /// survives the reconnect that a flaky client will do on its own.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PerPlayer_ClientRequest_IsPersistedForTheNextSession()
    {
        var playerId = NewPlayerId("dd-persist");
        var server = new InProcessServer(_fixture.CreateHandlerFactory(deepDesyncMode: DeepDesyncMode.PerPlayer));

        await using (var client = new TestClientSetup(server, playerId))
        {
            await client.ConnectAsync();
            Assert.True(await client.MetaClient.SetDeepDesyncAsync(true));
        }

        Assert.True(await _fixture.GrainFactory.GetGrain<IDesyncReportGrain>(playerId).IsAnalysisEnabledAsync());
    }

    /// <summary>
    /// Enable, then come back as the same player on a fresh session: the analysis is on again
    /// without asking a second time. This is the shape of a real investigation — you flag a player,
    /// they restart the game, and the flag has to still be there.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task PerPlayer_EnabledThenReconnected_ComesBackOn()
    {
        var playerId = NewPlayerId("dd-reconnect");
        var server = new InProcessServer(_fixture.CreateHandlerFactory(deepDesyncMode: DeepDesyncMode.PerPlayer));

        await using (var first = new TestClientSetup(server, playerId))
        {
            await first.ConnectAsync();
            Assert.True(await first.MetaClient.SetDeepDesyncAsync(true));
        }

        var diagnostics = new DesyncCollector();
        await using var second = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await second.ConnectAsync();

        Assert.True(second.MetaClient.Dispatcher.DeepDesyncActive);

        var api = await second.CreateResolver().GetServiceAsync<DesyncTestServiceApiClient>(playerId);
        await api.AddNonDeterministicAsync();
        await Task.Delay(200);
        Assert.NotEmpty(diagnostics.PatchDesyncs);
    }

    /// <summary>
    /// Forced cannot honour "off for me", and says so instead of accepting and ignoring it.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task Forced_ClientAsksToOptOut_IsRefused()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory(deepDesyncMode: DeepDesyncMode.Forced));

        await using var client = new TestClientSetup(server, NewPlayerId("dd-optout"));
        await client.ConnectAsync();

        Assert.False(await client.MetaClient.SetDeepDesyncAsync(false));
        Assert.True(client.MetaClient.Dispatcher.DeepDesyncActive);
    }

    private static string NewPlayerId(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    /// <summary>Records only what these tests assert on — whether a patch desync surfaced.</summary>
    private sealed class DesyncCollector : IDesyncDiagnostics
    {
        public List<string> PatchDesyncs { get; } = new();

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult) { }

        public void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes) { }

        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta) { }

        public void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc)
            => PatchDesyncs.Add($"{serviceName}.{methodName}");

        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
            => Task.FromResult(new StateComparisonResult { IsMatch = true });
    }
}
