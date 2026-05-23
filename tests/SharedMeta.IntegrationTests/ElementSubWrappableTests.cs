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

    // ─────────────────────────────────────────────────────────────────────
    // Phase 2: granular structural ops
    // ─────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 60_000)]
    public async Task PartyService_RemoveHero_ProducesGranularOp()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();
        var playerId = "party-remove-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "A", 1);
        await api.AddHeroAsync(2, "B", 2);
        await api.AddHeroAsync(3, "C", 3);
        await api.RemoveHeroAsync(1);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(2, state.Heroes.Count);
        Assert.Equal("A", state.Heroes[0].Name);
        Assert.Equal("C", state.Heroes[1].Name);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_InsertHero_ProducesGranularOp()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();
        var playerId = "party-insert-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "A", 1);
        await api.AddHeroAsync(3, "C", 3);
        await api.InsertHeroAsync(1, 2, "B", 2);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(3, state.Heroes.Count);
        Assert.Equal("A", state.Heroes[0].Name);
        Assert.Equal("B", state.Heroes[1].Name);
        Assert.Equal("C", state.Heroes[2].Name);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_ReplaceHero_ProducesSetOp()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();
        var playerId = "party-replace-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "A", 1);
        await api.AddHeroAsync(2, "B", 2);
        await api.ReplaceHeroAsync(1, 99, "Replaced", 99);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(2, state.Heroes.Count);
        Assert.Equal("A", state.Heroes[0].Name);
        Assert.Equal(99, state.Heroes[1].Id);
        Assert.Equal("Replaced", state.Heroes[1].Name);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_ClearHeroes_ProducesClearOp()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();
        var playerId = "party-clear-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "A", 1);
        await api.AddHeroAsync(2, "B", 2);
        await api.AddHeroAsync(3, "C", 3);
        await api.ClearHeroesAsync();
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Empty(state.Heroes);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_AddItemToHero_NestedListWorksEndToEnd()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();
        var playerId = "party-additem-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "A", 1);
        await api.AddHeroAsync(2, "B", 2);
        await api.AddHeroAsync(3, "C", 3);

        // The crucial two-level call: state.Heroes[1].Equipment.Add(...). The patch must
        // contain a Heroes element subtree at index 1, and inside it an Equipment
        // collection node with a single Insert structural op.
        await api.AddItemToHeroAsync(heroIndex: 1, itemId: 42, itemName: "Sword", power: 10);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Empty(state.Heroes[0].Equipment);
        Assert.Single(state.Heroes[1].Equipment);
        Assert.Equal(42, state.Heroes[1].Equipment[0].ItemId);
        Assert.Equal("Sword", state.Heroes[1].Equipment[0].Name);
        Assert.Equal(10, state.Heroes[1].Equipment[0].Power);
        Assert.Empty(state.Heroes[2].Equipment);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_UpgradeItem_NestedElementMutationWorks()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();
        var playerId = "party-upgrade-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "A", 1);
        await api.AddItemToHeroAsync(heroIndex: 0, itemId: 42, itemName: "Sword", power: 10);
        await api.AddItemToHeroAsync(heroIndex: 0, itemId: 43, itemName: "Shield", power: 5);

        // Two-level nested element mutation: Heroes[0].Equipment[1].Power = 25
        await api.UpgradeItemAsync(heroIndex: 0, itemIndex: 1, newPower: 25);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(2, state.Heroes[0].Equipment.Count);
        Assert.Equal(10, state.Heroes[0].Equipment[0].Power);  // unchanged
        Assert.Equal(25, state.Heroes[0].Equipment[1].Power);  // upgraded
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_MixedShift_StructuralOpShiftsElementChild()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();
        var playerId = "party-mixed-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        await api.AddHeroAsync(1, "A", 1);
        await api.AddHeroAsync(2, "B", 2);
        await api.AddHeroAsync(3, "C", 3);
        await api.AddHeroAsync(4, "D", 4);

        // Mutate hero with id=4 (currently at index 3) and remove hero with id=2 (index 1)
        // in the SAME call. The element child for id=4 must shift from idx 3 to idx 2 on
        // the sender side via ShiftElementChildren, and apply correctly on the receiver.
        await api.MixedShiftAsync(firstIdxToRemove: 1, secondHeroId: 4, expDelta: 500);
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Equal(3, state.Heroes.Count);
        Assert.Equal("A", state.Heroes[0].Name);   // id=1, untouched
        Assert.Equal("C", state.Heroes[1].Name);   // id=3, was at 2, now at 1
        Assert.Equal("D", state.Heroes[2].Name);   // id=4, was at 3, now at 2
        Assert.Equal(500, state.Heroes[2].Exp);    // its element mutation applied at the post-shift index
        Assert.Equal(0, state.Heroes[0].Exp);      // others untouched
        Assert.Equal(0, state.Heroes[1].Exp);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    [Fact(Timeout = 60_000)]
    public async Task PartyService_RegenerateAndAdd_AssignThenMutateWorksEndToEnd()
    {
        // Regression test: state.Heroes = new List<Hero>(); state.Heroes.Add(...) used
        // to silently lose the Add on the receiver because the list-field setter wrote
        // a terminal Value and the receiver returned early before applying the structural
        // op the Add appended. Now the setter records a FullReplace structural op so
        // both operations chain in submission order on the receiver.
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var diagnostics = new DesyncCollector();
        var playerId = "party-regen-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId, diagnostics: diagnostics);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<PartyServiceApiClient>(playerId);

        // Pre-populate so the wholesale replacement actually has something to throw away.
        await api.AddHeroAsync(1, "Old1", 1);
        await api.AddHeroAsync(2, "Old2", 2);

        await api.RegenerateAndAddAsync(99, "Fresh");
        await Task.Delay(150);

        var state = resolver.GetState<PartyState>(playerId);
        Assert.Single(state.Heroes);
        Assert.Equal(99, state.Heroes[0].Id);
        Assert.Equal("Fresh", state.Heroes[0].Name);
        Assert.Empty(diagnostics.PatchDesyncs);
    }

    private sealed class DesyncCollector : IDesyncDiagnostics
    {
        public List<(string Service, string Method, uint ServerCrc, uint LocalCrc)> PatchDesyncs { get; } = new();

        public void OnResultMismatch<T>(string serviceName, string methodName, T serverResult, T localResult) { }
        public void OnCrossEntityResult(string entityId, ushort methodId, byte[]? resultBytes) { }
        public void OnRandomDesync(string serviceName, string methodName, long serverDelta, long localDelta) { }

        public void OnPatchDesync(string serviceName, string methodName, uint serverCrc, uint localCrc)
        {
            PatchDesyncs.Add((serviceName, methodName, serverCrc, localCrc));
        }

        public Task<StateComparisonResult> CompareFullStateAsync(string entityId)
            => Task.FromResult(new StateComparisonResult { IsMatch = true });
    }
}
