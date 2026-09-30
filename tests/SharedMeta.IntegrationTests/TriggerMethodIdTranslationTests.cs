using SharedMeta.Client;
using SharedMeta.Client.Network;
using SharedMeta.Core.Network;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Broadcasts carry server method ids; the client maps them to its own through the negotiated
/// annotation. Trigger ops nested in a broadcast name a method too, and must be mapped the same
/// way — otherwise a client whose method table differs from the server's dispatches the wrong
/// trigger body (or none).
/// </summary>
[Collection(TestClusterCollection.Name)]
public class TriggerMethodIdTranslationTests
{
    private const ushort BumpId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_Bump_v1;
    private const ushort OnBumpedId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.IForcePatchFixtureService_OnBumped_v1;

    private readonly TestClusterFixture _fixture;

    public TriggerMethodIdTranslationTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 30_000)]
    public async Task BroadcastTriggerOps_AreTranslatedToClientMethodIds()
    {
        var entityId = $"trigger_ids_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var alice = new TestClientSetup(server, $"alice-{Guid.NewGuid():N}");
        await using var bob = new TestClientSetup(server, $"bob-{Guid.NewGuid():N}");
        await alice.ConnectAsync();
        await bob.ConnectAsync();

        await alice.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);
        var bobApi = await bob.CreateResolver().GetServiceAsync<ForcePatchFixtureServiceApiClient>(entityId);

        // A client whose table puts Bump and OnBumped elsewhere than the server does.
        var map = new ushort[GameServiceDiscoveryBase.ServerSignature.Methods.Count];
        for (int i = 0; i < map.Length; i++) map[i] = (ushort)i;
        map[BumpId] = 900;
        map[OnBumpedId] = 901;

        var dispatcher = (ClientDispatcher)alice.MetaClient.Dispatcher;
        using var adapter = new DispatcherNetworkAdapter(dispatcher, alice.Serializer, entityId, () => 0,
            typeof(ForcePatchFixtureState).FullName)
        {
            Annotated = new ClientSignatureAnnotated { ServerToClient = map },
        };
        var received = new TaskCompletionSource<NetworkBroadcast>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.OnBroadcast += b => received.TrySetResult(b);

        await bobApi.BumpAsync(1);
        var broadcast = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(900, broadcast.MethodId);
        Assert.Equal(901, Assert.Single(broadcast.TriggerOperations!).MethodId);
    }
}
