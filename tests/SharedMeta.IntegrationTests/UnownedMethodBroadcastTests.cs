using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Broadcasts for a method whose ApiClient this client does not hold are applied at entity level.
/// That handler is then the only one that runs, so it has to do everything an ApiClient would:
/// advance the optimistic random past a patch, and apply the method's trigger ops.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class UnownedMethodBroadcastTests
{
    private const ushort BumpId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_Bump_v1;
    private const ushort BumpRandomId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_BumpRandom_v1;

    private readonly TestClusterFixture _fixture;

    public UnownedMethodBroadcastTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task PatchBroadcast_AdvancesOptimisticRandom()
    {
        // Legacy build: BumpRandom at v0 → ForceServerPatch, so its broadcasts arrive as patches.
        await using var pair = await ConnectPairAsync(LegacySignature(BumpRandomId));

        var value = await pair.BobApi.BumpRandomAsync();
        await WaitUntilAsync(() => pair.AliceView.State.Value == value);

        // Bob's random is in step with the server (he ran BumpRandom himself) and he replays
        // Alice's roll from its broadcast, so his LastRoll is the server's.
        var aliceRoll = await pair.AliceView.RollAsync();
        await WaitUntilAsync(() => pair.BobApi.State.Rolls == 1);

        Assert.Equal(pair.BobApi.State.LastRoll, aliceRoll);
    }

    [Fact(Timeout = 30_000)]
    public async Task ReplayBroadcast_AppliesTriggers()
    {
        await using var pair = await ConnectPairAsync(null);

        var value = await pair.BobApi.BumpAsync(5);
        await WaitUntilAsync(() => pair.AliceView.State.Value == value);

        Assert.Equal(1, pair.AliceView.State.Bumps);
    }

    [Fact(Timeout = 30_000)]
    public async Task PatchBroadcast_AppliesTriggerPatches()
    {
        await using var pair = await ConnectPairAsync(LegacySignature(BumpId));

        var value = await pair.BobApi.BumpAsync(5);
        await WaitUntilAsync(() => pair.AliceView.State.Value == value);

        Assert.Equal(1, pair.AliceView.State.Bumps);
    }

    /// <summary>Alice holds only the view service on the entity; Bob holds the fixture service.</summary>
    private async Task<Pair> ConnectPairAsync(MetaClientSignature? aliceSignature)
    {
        var entityId = $"unowned_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}", clientSignature: aliceSignature);
        var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();
        var aliceView = await alice.CreateResolver().GetServiceAsync<ForcePatchFixtureViewServiceApiClient>(entityId);
        var bobApi = await bob.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);
        return new Pair(alice, bob, aliceView, bobApi);
    }

    private sealed record Pair(TestClientSetup Alice, TestClientSetup Bob,
        ForcePatchFixtureViewServiceApiClient AliceView, ForcePatchFixtureServiceApiClient BobApi) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Alice.DisposeAsync();
            await Bob.DisposeAsync();
        }
    }

    private static MetaClientSignature LegacySignature(ushort legacyMethodId)
    {
        var current = GameServiceDiscoveryBase.ClientSignature;
        return new MetaClientSignature
        {
            SignatureHash = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
            ClientVersion = current.ClientVersion,
            KnownMethods = current.KnownMethods.Select(m => new KnownMethodEntry
            {
                ServiceName = m.ServiceName,
                Alias = m.Alias,
                Version = m.GlobalIndex == legacyMethodId ? 0 : m.Version,
                ArgHash = m.ArgHash,
                GlobalIndex = m.GlobalIndex,
            }).ToList(),
            KnownStateTypes = current.KnownStateTypes,
        };
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
