using Orleans;
using Orleans.Core.Internal;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Subscribers live in their own record (<see cref="EntitySubscriptionStorage"/>). An entity that
/// deactivates while players are subscribed restores them from it; when the record itself is lost,
/// the next call from each affected player is detected and its subscription repaired.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SubscriberStoreTests
{
    private readonly TestClusterFixture _fixture;

    public SubscriberStoreTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task Deactivation_SubscribersRestoredFromRecord()
    {
        var entityId = $"sub_store_restore_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();
        var aliceApi = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var bobApi = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        await DeactivateAsync(entityId);

        await bobApi.AddValueAsync(3, 1);
        await aliceApi.PingAsync();   // barrier: bob's broadcast is ahead of this response

        Assert.Equal(3, aliceApi.State.Sum);
        Assert.Empty(alice.DetectedIssues);
    }

    [Fact(Timeout = 30_000)]
    public async Task LostRecord_CallerMissedNothing_RepairedSilently()
    {
        var entityId = $"sub_store_silent_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        var aliceApi = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        LosableSubscriptionStorage.Lose(EntityGrain(entityId).GetGrainId());
        await DeactivateAsync(entityId);

        // First call after the loss: nothing happened on the entity meanwhile, so it succeeds and
        // the session re-registers alice without her client noticing.
        await aliceApi.AddValueAsync(2, 1);
        Assert.Equal(2, aliceApi.State.Sum);

        // Proof she is a broadcast target again.
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await bob.ConnectAsync();
        var bobApi = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        await bobApi.AddValueAsync(5, 1);
        await aliceApi.PingAsync();

        Assert.Equal(7, aliceApi.State.Sum);
        Assert.Empty(alice.DetectedIssues);
    }

    [Fact(Timeout = 30_000)]
    public async Task LostRecord_CallerMissedOps_CallFailsAndStateReloads()
    {
        var entityId = $"sub_store_reload_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();
        var aliceApi = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var bobApi = await bob.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);

        LosableSubscriptionStorage.Lose(EntityGrain(entityId).GetGrainId());
        await DeactivateAsync(entityId);

        // Bob calls first: he is repaired silently, alice — no longer a broadcast target — misses it.
        await bobApi.AddValueAsync(5, 1);
        Assert.Equal(0, aliceApi.State.Sum);

        // Alice's call lands after an op she never received. Replaying it on her view would be
        // wrong, so it fails — but the server applied it, and her client reloads the entity.
        await Assert.ThrowsAnyAsync<Exception>(() => aliceApi.AddValueAsync(2, 1));
        await WaitUntilAsync(() => aliceApi.State.Sum == 7);

        // Re-subscribed: later writes reach her again.
        await bobApi.AddValueAsync(1, 1);
        await aliceApi.PingAsync();
        Assert.Equal(8, aliceApi.State.Sum);
    }

    private IEntityGrainBase EntityGrain(string entityId)
        => new SharedMeta.Test.Meta1.Server.GeneratedEntityGrainResolver()
            .GetEntityGrain(_fixture.GrainFactory, typeof(CounterState).FullName!, entityId)!;

    private async Task DeactivateAsync(string entityId)
    {
        await EntityGrain(entityId).Cast<IGrainManagementExtension>().DeactivateOnIdle();
        // DeactivateOnIdle schedules; let the activation go before the next call lands.
        await Task.Delay(200);
    }

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
