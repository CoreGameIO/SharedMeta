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
/// <see cref="SharedMeta.Core.IStateLoadedHook"/> normalizes state read from storage before any
/// client sees it, and a reported change is persisted even when no call follows.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class StateLoadedHookTests
{
    private readonly TestClusterFixture _fixture;

    public StateLoadedHookTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task FreshEntity_SubscriberSeesNormalizedState()
    {
        var entityId = $"load_hook_fresh_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await alice.ConnectAsync();

        var api = await alice.CreateResolver().GetServiceAsync<LoadHookServiceApiClient>(entityId);

        Assert.NotNull(api.State.Tags);
    }

    [Fact(Timeout = 30_000)]
    public async Task OldRecord_NormalizedOnLoad_PersistedWithoutCalls()
    {
        var entityId = $"load_hook_old_{Guid.NewGuid():N}";
        var marker = Guid.NewGuid().ToString("N");
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        var aliceApi = await alice.CreateResolver().GetServiceAsync<LoadHookServiceApiClient>(entityId);

        await aliceApi.MakeLegacyAsync(marker);
        await DeactivateAsync(entityId);   // stored record now lacks Tags

        // Subscribe alone activates the entity — no call marks it dirty, only the hook does.
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await bob.ConnectAsync();
        var bobApi = await bob.CreateResolver().GetServiceAsync<LoadHookServiceApiClient>(entityId);
        Assert.NotNull(bobApi.State.Tags);

        await DeactivateAsync(entityId);

        await using var carol = new TestClientSetup(server, $"carol-{Guid.NewGuid():N}");
        await carol.ConnectAsync();
        await carol.CreateResolver().GetServiceAsync<LoadHookServiceApiClient>(entityId);

        // First load found the old record; the second found the normalized one written on deactivation.
        Assert.Equal(new[] { true, false }, LoadHookState.Loads[marker].ToArray());
        Assert.Empty(bob.DetectedIssues);
    }

    private IEntityGrainBase EntityGrain(string entityId)
        => new SharedMeta.Test.Meta1.Server.GeneratedEntityGrainResolver()
            .GetEntityGrain(_fixture.GrainFactory, typeof(LoadHookState).FullName!, entityId)!;

    private async Task DeactivateAsync(string entityId)
    {
        await EntityGrain(entityId).Cast<IGrainManagementExtension>().DeactivateOnIdle();
        // DeactivateOnIdle schedules; let the activation go before the next call lands.
        await Task.Delay(200);
    }
}
