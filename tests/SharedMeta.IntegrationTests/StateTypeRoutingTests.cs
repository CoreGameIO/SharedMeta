using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Two state types under one entityId share the dispatcher's entity routing. A broadcast whose
/// method this client does not know cannot be matched to a service, so its state bytes used to be
/// applied to every state type under the id. Ops carry their state type; routing must use it.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class StateTypeRoutingTests
{
    private const ushort BumpReplaceId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_BumpReplace_v0;

    private readonly TestClusterFixture _fixture;

    public StateTypeRoutingTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task UnknownMethodBroadcast_ReachesOnlyItsOwnStateType()
    {
        var entityId = $"routing_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        // An older build without BumpReplace: its broadcasts arrive with an unknown method id.
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}",
            clientSignature: SignatureWithout(BumpReplaceId));
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        var aliceCounter = await alice.CreateResolver().GetServiceAsync<CounterServiceApiClient>(entityId);
        var aliceFixture = await alice.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);
        var bobFixture = await bob.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);

        await aliceCounter.AddValueAsync(7, 1);
        var counterBefore = aliceCounter.State;

        // A second CounterState listener on the same entityId: it must not see the fixture's op.
        int counterProbeHits = 0;
        using var counterProbe = new SharedMeta.Client.Network.DispatcherNetworkAdapter(
            (SharedMeta.Client.ClientDispatcher)alice.MetaClient.Dispatcher, alice.Serializer, entityId, () => 0,
            typeof(CounterState).FullName);
        counterProbe.OnBroadcast += _ => Interlocked.Increment(ref counterProbeHits);

        // ServerReplace: the broadcast carries the whole ForcePatchFixtureState.
        var value = await bobFixture.BumpReplaceAsync(5);
        await WaitUntilAsync(() => aliceFixture.State.Value == value);

        Assert.Equal(0, counterProbeHits);
        Assert.Same(counterBefore, aliceCounter.State);
        Assert.Equal(7, aliceCounter.State.Sum);
    }

    private static MetaClientSignature SignatureWithout(ushort methodId)
    {
        var current = GameServiceDiscoveryBase.ClientSignature;
        return new MetaClientSignature
        {
            SignatureHash = (ulong)Random.Shared.NextInt64(1, long.MaxValue),
            ClientVersion = current.ClientVersion,
            KnownMethods = current.KnownMethods.Where(m => m.GlobalIndex != methodId).ToList(),
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
