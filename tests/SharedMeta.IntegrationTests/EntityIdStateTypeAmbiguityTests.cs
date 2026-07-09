using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Regression coverage for the entityId/StateType identity fix: the server addresses entities
/// by (state type, entityId) — an Orleans grain identity — so the same entityId string is a
/// valid, independent identity across different state types (e.g. Inventory/Profile/Wallet all
/// keyed by a playerId under the UserOwned convention). Before this fix, MetaServiceResolver
/// threw on a second state type sharing an entityId, and a confirmed bug let ServerReplace/
/// ServerPatch broadcasts for one state type corrupt a sibling connection's state.
///
/// Uses <see cref="ICounterService"/> (CounterState) + <see cref="IDesyncTestService"/>
/// (DesyncTestState) as the two independent state types sharing one entityId, and
/// <see cref="IWalletService"/> (WalletState, config CounterConfig) to exercise the
/// ambiguous-config-match throw against <see cref="ICounterService"/> (also CounterConfig).
/// </summary>
[Collection(TestClusterCollection.Name)]
public class EntityIdStateTypeAmbiguityTests
{
    private readonly TestClusterFixture _fixture;

    public EntityIdStateTypeAmbiguityTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoStateTypes_ShareEntityId_NoThrow_IndependentStateAndConfig()
    {
        var entityId = $"dual_state_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        // Pre-fix: the second GetServiceAsync under the same entityId with a different
        // StateType threw InvalidOperationException. Must not throw now.
        var counterApi = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        var desyncApi = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(entityId);

        Assert.NotSame((object)counterApi.State, desyncApi.State);

        await counterApi.AddValueAsync(10, 1);
        await desyncApi.AddAsync(99);

        // Independent state containers — mutating one must never touch the other.
        Assert.Equal(10, resolver.GetState<CounterState>(entityId).Sum);
        Assert.Equal(99, resolver.GetState<DesyncTestState>(entityId).Value);

        // Independent config resolution — no ambiguity since only one connection under this
        // entityId exposes CounterConfig (DesyncTestService declares no config at all).
        var counterConfig = resolver.GetEntityConfig<CounterConfig>(entityId);
        Assert.NotNull(counterConfig);
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerReplaceBroadcast_ForOneStateType_DoesNotCorruptSiblingStateSharingEntityId()
    {
        // Regression test for the confirmed HandleEntityBroadcast bug: before the ownership
        // guard, a ServerReplace/ServerPatch broadcast for one state type was applied
        // unconditionally to every connection registered under the entityId — including a
        // sibling connection for a completely different state type.
        _fixture.ExecutionModeProvider.SetMode(
            global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_ReplaceReset_v0,
            ExecutionMode.ServerReplace);
        try
        {
            var entityId = $"dual_state_srep_{Guid.NewGuid():N}";
            var server = new InProcessServer(_fixture.CreateHandlerFactory());
            await using var alice = new TestClientSetup(server, "alice");
            await alice.ConnectAsync();
            var resolver = alice.CreateResolver();

            var counterApi = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
            var desyncApi = await resolver.GetServiceAsync<DesyncTestServiceApiClient>(entityId);

            // Give DesyncTestState real, distinctive data before the sibling's ServerReplace.
            await desyncApi.AddAsync(777);
            await desyncApi.SetLabelAsync("untouched");
            Assert.Equal(777, resolver.GetState<DesyncTestState>(entityId).Value);

            await counterApi.AddValueAsync(5, 1);
            await counterApi.ReplaceResetAsync(0);
            await Task.Delay(300);

            // CounterState was wholesale-replaced (ServerReplace) — expected.
            Assert.Equal(0, resolver.GetState<CounterState>(entityId).Sum);

            // DesyncTestState (the sibling connection sharing this entityId) must be completely
            // untouched by CounterState's ServerReplace broadcast.
            var desyncState = resolver.GetState<DesyncTestState>(entityId);
            Assert.Equal(777, desyncState.Value);
            Assert.Equal("untouched", desyncState.Label);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task GetEntityConfig_ThrowsWhenTwoConnectionsShareEntityIdAndConfigType()
    {
        var entityId = $"dual_state_ambiguous_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        // CounterState (via ICounterService) and WalletState (via IWalletService) both declare
        // CounterConfig — two independent connections under one entityId now both expose it.
        await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await resolver.GetServiceAsync<WalletServiceApiClient>(entityId);

        Assert.Throws<InvalidOperationException>(() => resolver.GetEntityConfig<CounterConfig>(entityId));

        // The disambiguating overload still resolves correctly for either side.
        var counterSideConfig = resolver.GetEntityConfig<CounterState, CounterConfig>(entityId);
        var walletSideConfig = resolver.GetEntityConfig<WalletState, CounterConfig>(entityId);
        Assert.NotNull(counterSideConfig);
        Assert.NotNull(walletSideConfig);
    }

    [Fact(Timeout = 30_000)]
    public async Task DisconnectAsyncTState_DisconnectsOnlyThatStateType()
    {
        var entityId = $"dual_state_disconnect_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await resolver.GetServiceAsync<DesyncTestServiceApiClient>(entityId);

        await resolver.DisconnectAsync<CounterState>(entityId);

        // Counter side gone, DesyncTest side survives untouched.
        Assert.Throws<InvalidOperationException>(() => resolver.GetState<CounterState>(entityId));
        Assert.NotNull(resolver.GetState<DesyncTestState>(entityId));
    }
}
