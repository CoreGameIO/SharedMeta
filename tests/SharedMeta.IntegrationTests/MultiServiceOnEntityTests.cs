using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;
using Xunit.Abstractions;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Tests for the 0.14.0 multi-service-on-entity refactor. The framework allows several
/// <c>[MetaService]</c> interfaces to target the same <c>ISharedState</c>; this fixture
/// uses <see cref="ICounterService"/> + <see cref="ICounterAuxService"/> on the same
/// <see cref="CounterState"/> to verify that:
/// <list type="bullet">
///   <item>Every API client subscribed to an entity sees the same state object — including
///   after wholesale ServerReplace.</item>
///   <item>A foreign-service broadcast (a method on a different service targeting the same
///   state) updates the local state — even when the receiver only holds an API client for
///   one of the services. Requires the server to emit state-data in the broadcast (set via
///   <c>ExecutionModeProvider.SetMode</c> to ServerReplace / ServerPatch in these tests).</item>
///   <item><see cref="EntityStateContainer{TState}.MutationCount"/> is shared across all
///   API clients on the entity.</item>
///   <item><c>OnStateMutated</c> fires on every API client on the entity in lock-step.</item>
///   <item><see cref="MetaServiceResolver.GetStateContainer{TState}"/> exposes the same
///   container that backs the API clients.</item>
/// </list>
/// </summary>
[Collection(TestClusterCollection.Name)]
public class MultiServiceOnEntityTests
{
    private readonly TestClusterFixture _fixture;
    private readonly ITestOutputHelper _output;

    public MultiServiceOnEntityTests(TestClusterFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact(Timeout = 30_000)]
    public async Task TwoApiClients_ShareSameStateInstance()
    {
        var entityId = $"shared_state_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counterApi = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        var auxApi = await resolver.GetServiceAsync<CounterAuxServiceApiClient>(entityId);

        Assert.NotSame(counterApi, auxApi);
        // Both clients observe the same underlying state instance via the shared container.
        Assert.Same(counterApi.State, auxApi.State);

        var container = resolver.GetStateContainer<CounterState>(entityId);
        Assert.Same(counterApi.State, container.State);
        Assert.Same(auxApi.State, container.State);
    }

    [Fact(Timeout = 30_000)]
    public async Task ForeignServiceBroadcast_UpdatesStateForAllApiClients()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterAuxService", "AuxAdd", ExecutionMode.Server);
        try
        {
            var entityId = $"foreign_bcast_{Guid.NewGuid():N}";
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var alice = new TestClientSetup(server, "alice");
            await using var bob = new TestClientSetup(server, "bob");
            await alice.ConnectAsync();
            await bob.ConnectAsync();

            // Alice has only ICounterService — no AUX service ApiClient locally.
            var aliceCounter = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

            // Bob holds AUX. He'll trigger the mutation through a different service.
            var bobAux = await bob.CreateResolver().GetServiceAsync<CounterAuxServiceApiClient>(entityId);

            await bobAux.AuxAddAsync(42);
            await bobAux.AuxAddAsync(8);

            await Task.Delay(300);

            // Alice's CounterServiceApiClient holds the post-broadcast state because the
            // entity-level handler in MetaServiceResolver applied the StateBytes payload to
            // the shared container — pre-0.14.0 the per-ApiClient ServiceName filter dropped it.
            Assert.Equal(50, aliceCounter.State.Sum);
            Assert.Equal(2, aliceCounter.State.Operations.Count);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task MutationCount_IsSharedAcrossApiClientsOnSameEntity()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterAuxService", "AuxAdd", ExecutionMode.ServerReplace);
        try
        {
            var entityId = $"mc_shared_{Guid.NewGuid():N}";
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var alice = new TestClientSetup(server, "alice");
            await using var bob = new TestClientSetup(server, "bob");
            await alice.ConnectAsync();
            await bob.ConnectAsync();

            var aliceResolver = alice.CreateResolver();
            var aliceCounter = await aliceResolver.GetServiceAsync<CounterServiceApiClient>(entityId);
            var aliceAux = await aliceResolver.GetServiceAsync<CounterAuxServiceApiClient>(entityId);

            // Both Alice's API clients return the same value, sourced from the entity container.
            Assert.Equal(aliceCounter.MutationCount, aliceAux.MutationCount);

            var startCount = aliceCounter.MutationCount;

            var bobAux = await bob.CreateResolver().GetServiceAsync<CounterAuxServiceApiClient>(entityId);
            await bobAux.AuxAddAsync(7);
            await Task.Delay(300);

            // One foreign-service broadcast → one container bump → both API clients see +1.
            Assert.Equal(aliceCounter.MutationCount, aliceAux.MutationCount);
            Assert.Equal(startCount + 1, aliceCounter.MutationCount);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task OnStateMutated_FiresOnAllApiClientsForSameEntity()
    {
        _fixture.ExecutionModeProvider.SetMode("ICounterAuxService", "AuxAdd", ExecutionMode.ServerReplace);
        try
        {
            var entityId = $"on_mutated_{Guid.NewGuid():N}";
            var server = new InProcessServer(_fixture.CreateHandlerFactory());

            await using var alice = new TestClientSetup(server, "alice");
            await using var bob = new TestClientSetup(server, "bob");
            await alice.ConnectAsync();
            await bob.ConnectAsync();

            var aliceResolver = alice.CreateResolver();
            var aliceCounter = await aliceResolver.GetServiceAsync<CounterServiceApiClient>(entityId);
            var aliceAux = await aliceResolver.GetServiceAsync<CounterAuxServiceApiClient>(entityId);

            int counterFires = 0, auxFires = 0;
            aliceCounter.OnStateMutated += () => counterFires++;
            aliceAux.OnStateMutated += () => auxFires++;

            var bobAux = await bob.CreateResolver().GetServiceAsync<CounterAuxServiceApiClient>(entityId);
            await bobAux.AuxAddAsync(11);
            await Task.Delay(300);

            // One mutation source → both API clients on the entity receive OnStateMutated once.
            Assert.Equal(1, counterFires);
            Assert.Equal(1, auxFires);
        }
        finally
        {
            _fixture.ExecutionModeProvider.Clear();
        }
    }

    [Fact(Timeout = 30_000)]
    public async Task ServerReplace_BroadcastUpdatesShared_StateInstanceOnReceiver()
    {
        // Two-client setup: alice calls ReplaceReset (caller path), bob receives the
        // broadcast (receiver path). Receiver's container is replaced wholesale via Replace,
        // so both ApiClients on bob's resolver see the new state instance.
        var entityId = $"srep_recv_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, "alice");
        await using var bob = new TestClientSetup(server, "bob");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        var aliceCounter = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var bobCounter = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var bobAux = await bob.CreateResolver().GetServiceAsync<CounterAuxServiceApiClient>(entityId);

        // Pre-fill so we can observe the wholesale replacement.
        await aliceCounter.AddValueAsync(123, 1);
        await Task.Delay(200);
        Assert.Equal(123, bobCounter.State.Sum);
        Assert.Same(bobCounter.State, bobAux.State);

        var bobPrev = bobCounter.State;
        await aliceCounter.ReplaceResetAsync(0);
        await Task.Delay(300);

        // Both Bob's API clients still observe the same instance — replace happened
        // through the shared container.
        Assert.Same(bobCounter.State, bobAux.State);
        Assert.Equal(0, bobCounter.State.Sum);
        Assert.Equal(0, bobAux.State.Sum);
    }

    [Fact(Timeout = 30_000)]
    public async Task MutationCount_BumpsOnLocalOptimisticExecution()
    {
        var entityId = $"mc_local_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, "alice");
        await alice.ConnectAsync();

        var aliceCounter = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        var startCount = aliceCounter.MutationCount;

        // ThrowIfNegative is Optimistic mode — local execution mutates state via the
        // method body. Container's NotifyMutated must bump even though no broadcast involved.
        await aliceCounter.ThrowIfNegativeAsync(0);

        Assert.True(aliceCounter.MutationCount > startCount,
            $"MutationCount should bump on Optimistic execution; was {startCount}, is {aliceCounter.MutationCount}");
    }
}
