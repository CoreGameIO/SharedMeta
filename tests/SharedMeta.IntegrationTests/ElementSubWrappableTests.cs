using SharedMeta.Core.Diagnostics;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Integration tests for the element-sub-wrappable list path (Phase 1 of granular patches).
/// PartyState.Heroes is a List&lt;Hero&gt; where Hero has [MemoryPackOrder] members; the
/// generator emits a specialized HeroPatchableList that hands out HeroPatchWrapper elements
/// bound to per-index sub-trees of the patch.
///
/// These tests verify the END-TO-END round-trip: mutations through wrapper compute the
/// same CRC on both sides (no spurious patch desyncs), and the receiving side applies the
/// per-element sub-tree correctly so client and server states converge after the call.
///
/// The compile-time guard (helper methods returning raw Hero break the _PatchTracked copy)
/// is verified manually — it's checked by the very fact that PartyService.cs compiles,
/// because <see cref="PartyService.FindById"/> is typed as PartyStatePatchWrapper.HeroPatchWrapper
/// and the same source compiles in both the regular and the _PatchTracked copies thanks
/// to the implicit operator from raw Hero.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ElementSubWrappableTests
{
    private readonly TestClusterFixture _fixture;

    public ElementSubWrappableTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_AddHero_PopulatesListWithoutDesync()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        var playerId = "party-add-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "Aragorn", level: 5);
        await api.AddHeroAsync(2, "Legolas", level: 6);
        await api.AddHeroAsync(3, "Gimli", level: 4);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(3, state.Heroes.Count);
        Assert.Equal("Aragorn", state.Heroes[0].Name);
        Assert.Equal("Legolas", state.Heroes[1].Name);
        Assert.Equal("Gimli", state.Heroes[2].Name);

        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_AwardExp_MutatesElementWithoutDesync()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        var playerId = "party-award-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "Aragorn", level: 5);
        await api.AddHeroAsync(2, "Legolas", level: 6);
        await api.AddHeroAsync(3, "Gimli", level: 4);

        // The crucial test — this mutation goes through:
        //   FindById(2) → state.Heroes.FirstOrDefault(...) → HeroPatchWrapper
        //   hero.Exp += 250 → wrapper setter → patch sub-tree at Heroes/[1]/Exp
        // Server does the same; both CRCs must match.
        await api.AwardExpAsync(heroId: 2, amount: 250);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(250, state.Heroes[1].Exp);
        // Other heroes should be untouched
        Assert.Equal(0, state.Heroes[0].Exp);
        Assert.Equal(0, state.Heroes[2].Exp);

        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_BatchUpdate_MutatesTwoElementsInOneCall()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();

        var playerId = "party-batch-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(10, "First", level: 1);
        await api.AddHeroAsync(20, "Second", level: 1);
        await api.AddHeroAsync(30, "Third", level: 1);

        // Two element subtrees in one call. Both must end up in the same patch and both
        // CRCs must match between client and server.
        await api.BatchUpdateAsync(firstId: 10, firstExpDelta: 100, secondId: 30, secondHpDelta: -25);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(100, state.Heroes[0].Exp);   // first hero (id=10)
        Assert.Equal(0, state.Heroes[1].Exp);     // untouched
        Assert.Equal(75, state.Heroes[2].Hp);     // third hero (id=30) — started at 100, -25
        Assert.Equal(100, state.Heroes[0].Hp);    // first hero hp untouched

        Assert.Empty(diagnostics.PatchDesyncs);
    }

    private sealed class DesyncCollector : IDesyncDiagnostics
    {
        public List<(string Service, string Method, uint ServerCrc, uint LocalCrc)> PatchDesyncs { get; } = new();

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult) { }
        public void OnCrossEntityResult(string entityId, string serviceName, string methodName, byte[]? resultBytes) { }
        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta) { }

        public void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc)
        {
            PatchDesyncs.Add((serviceName, methodName, serverCrc, localCrc));
        }

        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
            => Task.FromResult(new StateComparisonResult { IsMatch = true });
    }
}
