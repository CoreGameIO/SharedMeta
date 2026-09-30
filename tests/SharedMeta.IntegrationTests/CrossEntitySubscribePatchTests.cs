using SharedMeta.Client;
using SharedMeta.Core;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// An entity subscribed on demand for a CrossOptimistic call gets its broadcast handler from the
/// state type's registration. The primary path picks the patch applier from any service on the
/// state; the on-demand path took only the last-registered config's, so a hand-rolled config
/// without one left that entity unable to apply patches.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class CrossEntitySubscribePatchTests
{
    private readonly TestClusterFixture _fixture;

    public CrossEntitySubscribePatchTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task OnDemandSubscription_AppliesPatches_WhenLastConfigHasNoApplier()
    {
        var entityId = $"xe_patch_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        // A hand-rolled registration for the same state, registered last and carrying no applier.
        var resolver = alice.CreateResolver();
        resolver.RegisterService<HandRolledMarker>(new MetaServiceConfig
        {
            ServiceName = "HandRolledForcePatchFixture",
            ApiClientType = typeof(HandRolledMarker),
            StateType = typeof(ForcePatchFixtureState),
            StateContainerFactory = state => new EntityStateContainer<ForcePatchFixtureState>((ForcePatchFixtureState)state),
        });

        await ((ICrossEntityResolver)resolver).EnsureSubscribedAsync(entityId, typeof(ForcePatchFixtureState).FullName!);
        var bobApi = await bob.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);

        // Declared ServerPatch: every subscriber takes the diff.
        var value = await bobApi.BumpPatchAsync(4);
        await WaitUntilAsync(() => resolver.GetState<ForcePatchFixtureState>(entityId).Value == value);
    }

    private sealed class HandRolledMarker { }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not reached in 5s.");
            await Task.Delay(20);
        }
    }
}
