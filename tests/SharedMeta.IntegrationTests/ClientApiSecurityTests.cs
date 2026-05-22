using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;
using Xunit.Abstractions;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.20.0 security hardening: verify that <c>[MetaMethod(GenerateClientApi = false)]</c>
/// methods cannot be invoked by a forged client RPC even though the client API is not
/// generated. Cross-entity and sibling-bypass paths to the same methods must continue
/// to work — those are server-internal and the client cannot reach them directly.
///
/// Threat model: an attacker has a modified client and can craft arbitrary
/// <see cref="RpcCallRequest"/> packets with any ServiceName/MethodName. The server
/// must reject calls to methods that opted out of the client API.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class ClientApiSecurityTests
{
    private readonly TestClusterFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ClientApiSecurityTests(TestClusterFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    /// <summary>
    /// <see cref="ICounterService.AddClamped"/> is declared with GenerateClientApi=false —
    /// it's the cross-entity callee invoked by AddCrossEntity. A forged client packet that
    /// names this service+method must be rejected at <c>EntityGrain.HandleCallAsync</c>
    /// before any user code runs.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task ForgedClientRpc_GenerateClientApiFalse_IsRejected()
    {
        var entityId = $"sec_forged_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        // Subscribe via the legitimate API so the entity exists and the session is active.
        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await counter.AddValueAsync(7, 1);
        Assert.Equal(7, counter.State.Sum);

        // Forge a direct RPC bypassing the (un-generated) client API — this is what an
        // attacker with a modified client would do. RequestId is the dispatcher's next
        // sequential id (1 was consumed by the AddValueAsync call above) so the server's
        // RPC ordering layer accepts it and routes to HandleCallAsync, where the
        // IsClientCallable gate rejects it.
        const long forgedRequestId = 2;
        var forgedRequest = new RpcCallRequest
        {
            EntityId = entityId,
            RequestId = forgedRequestId,
            MethodId = global::SharedMeta.Test.Meta1.Generated.GameMethodIds.ICounterService_AddClamped_v0,
            Payload = client.Serializer.Pack(42),
            ServerTimeTicks = DateTime.UtcNow.Ticks
        };

        var response = await client.Connection.RpcCallAsync(forgedRequest);

        // The framework returns an error either as the SessionResponse top-level Error or
        // on the matching SessionOp. Either way, the rejection message identifies it.
        var matchingOp = response.Operations.FirstOrDefault(op => op.RequestId == forgedRequestId);
        var error = response.Error ?? matchingOp.ErrorMessage;
        Assert.NotNull(error);
        // 0.24.0+: the rejection moved from the dispatcher's inline gate ("not callable
        // from clients") to the MethodId-translation back-stop in MetaConnectionHandler,
        // which short-circuits before the call even reaches the grain. Either wording is
        // acceptable — both indicate the forged RPC was denied.
        Assert.Contains("not callable", error);

        // State must be unchanged — the forged call never reached the impl.
        Assert.Equal(7, counter.State.Sum);
    }

    /// <summary>
    /// Cross-entity invocation of the same protected method must still succeed.
    /// AddCrossEntity is GenerateClientApi=true (the public method authorized by the
    /// client) and internally cross-entity-calls AddClamped on a different counter.
    /// The cross-entity hop lands at HandleCallFromEntityAsync, which is intentionally
    /// not gated by IsClientCallable.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task CrossEntityCall_GenerateClientApiFalse_StillWorks()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var entityA = $"sec_xentA_{Guid.NewGuid():N}";
        var entityB = $"sec_xentB_{Guid.NewGuid():N}";

        var apiA = await resolver.GetServiceAsync<CounterServiceApiClient>(entityA);
        var apiB = await resolver.GetServiceAsync<CounterServiceApiClient>(entityB);

        // AddCrossEntity (public) → server-side cross-entity call to AddClamped (protected)
        var clamped = await apiA.AddCrossEntityAsync(entityB, 33);

        Assert.Equal(33, clamped);
        Assert.Equal(33, apiB.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Sibling-bypass to a protected sibling-only method must still succeed.
    /// SiblingAuxAdd (public) sibling-calls AuxAdd (declared GenerateClientApi=false on
    /// the aux service). This dispatches in-process via the typed sibling caller — never
    /// crosses the client RPC boundary, so the IsClientCallable check is irrelevant.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task SiblingBypass_GenerateClientApiFalse_StillWorks()
    {
        var entityId = $"sec_sib_{Guid.NewGuid():N}";
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server, "alice");
        await client.ConnectAsync();
        var resolver = client.CreateResolver();

        var counter = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);

        var sumAfter = await counter.SiblingAuxAddAsync(11);

        Assert.Equal(11, sumAfter);
        Assert.Equal(11, counter.State.Sum);
        Assert.Empty(client.DetectedIssues);
    }

    /// <summary>
    /// Spec point 1 — code-gen suppression: the typed client API does NOT expose methods
    /// declared with <c>[MetaMethod(GenerateClientApi = false)]</c>. Reflection check on the
    /// generated <see cref="CounterServiceApiClient"/>: <c>AddClampedAsync</c> must not exist.
    /// User code on the client cannot accidentally call the protected method through the API.
    /// </summary>
    [Fact]
    public void GenerateClientApiFalse_PublicMethodAbsentFromApiClient()
    {
        var apiClientType = typeof(CounterServiceApiClient);

        // The protected method must not surface as any public callable on the API client.
        Assert.Null(apiClientType.GetMethod("AddClampedAsync"));
        Assert.Null(apiClientType.GetMethod("AddClamped"));

        // Sanity: regular methods on the same service still surface.
        Assert.NotNull(apiClientType.GetMethod("AddValueAsync"));
        Assert.NotNull(apiClientType.GetMethod("AddCrossEntityAsync"));

        // The replay event for AddClamped is still emitted — broadcasts from cross-entity
        // invocations must reach subscribed clients with state changes applied. Subscribers
        // can observe the event even though they cannot call the method directly.
        Assert.NotNull(apiClientType.GetEvent("OnAddClamped_Replayed"));
    }
}
