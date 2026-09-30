using SharedMeta.Client;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// The server runs a method's triggers right after its body. An Optimistic caller executes the
/// body itself and never replays the confirmation, so it has to run the triggers itself too —
/// otherwise it keeps a state the server and every other subscriber moved past.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class OptimisticTriggerTests
{
    private readonly TestClusterFixture _fixture;

    public OptimisticTriggerTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task OptimisticCaller_RunsTriggersLocally_Once()
    {
        var entityId = $"opt_trigger_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();
        var aliceApi = await alice.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);
        var bobApi = await bob.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);

        await bobApi.BumpAsync(5);
        Assert.Equal(1, bobApi.State.Bumps);

        // Once the confirmation is back, nothing applies the trigger a second time.
        await WaitUntilAsync(() => aliceApi.State.Bumps == 1);
        await WaitUntilAsync(() => ((ClientDispatcher)bob.MetaClient.Dispatcher).PendingRequestCount == 0);
        Assert.Equal(1, bobApi.State.Bumps);
        Assert.Empty(bob.DetectedIssues);
    }

    /// <summary>
    /// ServerReplace ships the state as it stood after the triggers ran; replaying the trigger ops
    /// on top of it applies them a second time.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ServerReplace_TriggersAppliedOnce()
    {
        var entityId = $"replace_trigger_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();
        var aliceApi = await alice.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);
        var bobApi = await bob.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);

        await bobApi.BumpReplaceAsync(5);
        // Barrier: broadcasts are applied in order, so once this one lands the replace is done.
        var value = await bobApi.BumpRandomAsync();
        await WaitUntilAsync(() => aliceApi.State.Value == value);

        Assert.Equal(1, bobApi.State.Bumps);
        Assert.Equal(1, aliceApi.State.Bumps);
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
