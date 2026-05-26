using System.Threading.Tasks;
using SharedMeta.Client;
using SharedMeta.Core.Transport;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// 0.24.0 end-to-end annotated-signature handshake. Verifies that the client receives a
/// populated <see cref="ClientSignatureAnnotated"/> after <c>SessionConnect</c>, the hashes
/// match the wired client signature and the server's reported hash, and the per-method
/// translation arrays are correctly sized for the test cluster's surface.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class AnnotatedSignatureHandshakeTests
{
    private readonly TestClusterFixture _fixture;

    public AnnotatedSignatureHandshakeTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionConnect_PopulatesAnnotation_WithMatchingHashesAndArrays()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);

        // First connect on this clientHash triggers phase-2; the dispatcher applies the
        // annotation regardless of whether it arrived in phase-1 (warm-restart cache hit)
        // or in phase-2 (cold start). Either way it must be populated when ConnectAsync
        // returns success.
        await client.ConnectAsync();

        var annotated = client.MetaClient.Dispatcher.Annotated;
        Assert.NotNull(annotated);

        // ClientSignatureHash echoes the wired client signature unchanged.
        var sig = SharedMeta.Test.Meta1.GameServiceDiscoveryBase.ClientSignature;
        Assert.Equal(sig.SignatureHash, annotated!.ClientSignatureHash);

        // ServerSignatureHash is non-zero (server signature was wired in the test cluster
        // via TestClusterFixture.CreateHandlerFactory).
        Assert.NotEqual(0UL, annotated.ServerSignatureHash);

        // Statuses length matches the client's known-method count (dense generator emit).
        Assert.Equal(sig.KnownMethods.Count, annotated.Statuses.Length);

        // ServerToClient length matches the server's method count (Test.Meta1 client and
        // server share the same shared assembly so the counts are equal here).
        var serverSig = SharedMeta.Test.Meta1.GameServiceDiscoveryBase.ServerSignature;
        Assert.Equal(serverSig.Methods.Count, annotated.ServerToClient.Length);
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionConnect_NoForcePatchVerdicts_WhenSidesShareSurface()
    {
        // Test.Meta1 client and server are built from the same shared assembly — no
        // method declares MinCompatibleVersion > clientCode, so the annotation must not
        // carry any ForceServerPatch entries. Rejected entries ARE expected for server-only
        // methods (GenerateClientApi=false used by Notification-mode broadcasts).
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var annotated = client.MetaClient.Dispatcher.Annotated;
        Assert.NotNull(annotated);
        Assert.DoesNotContain(MethodStatus.ForceServerPatch, annotated!.Statuses);
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionConnect_ServerToClient_IdentityMappingWhenSidesMatch()
    {
        // Same-source case: server's GlobalIndex == client's GlobalIndex for every shared
        // method, so ServerToClient[i] == i across the board.
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var annotated = client.MetaClient.Dispatcher.Annotated;
        Assert.NotNull(annotated);
        var map = annotated!.ServerToClient;
        for (ushort i = 0; i < map.Length; i++)
            Assert.Equal(i, map[i]);
    }

    [Fact(Timeout = 60_000)]
    public async Task SessionConnect_AnnotationRoundTripsThroughWire_WithinExpectedSize()
    {
        // Wire-size regression: after the annotated handshake, the serialized form of the
        // annotation must be a small multiple of method count, not pathological. Asserts
        // the byte-cost is bounded by a generous ceiling tuned to Test.Meta1's surface
        // (~50 methods today, headroom for growth in the test surface).
        var server = new InProcessServer(_fixture.CreateHandlerFactory());
        await using var client = new TestClientSetup(server);
        await client.ConnectAsync();

        var annotated = client.MetaClient.Dispatcher.Annotated;
        Assert.NotNull(annotated);

        // Re-serialize through the test serializer to measure the on-wire footprint of
        // the annotation alone (the SessionConnectResponse envelope adds session/version
        // metadata — measured separately if needed).
        var bytes = client.Serializer.Pack(annotated!);
        // Each method contributes ~3 bytes (1 status + 2 ServerToClient) plus 16 B hashes
        // plus list framing. 4 KB ceiling = ~1000 methods budget; well above Test.Meta1.
        Assert.True(bytes.Length < 4096,
            $"Annotation wire size {bytes.Length} B exceeds the 4 KB regression ceiling for the Test.Meta1 surface.");
    }
}
