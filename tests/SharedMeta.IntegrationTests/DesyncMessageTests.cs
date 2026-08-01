using SharedMeta.Core.Diagnostics;
using SharedMeta.Debug.InProcess;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// A desync message interpolates the two result values. For a DTO that does not override
/// <c>ToString()</c> that used to print the type name on both sides — the message announced a
/// divergence and then showed the same string twice. The generator now emits a per-type
/// formatter; this asserts the message actually carries member values.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class DesyncMessageTests
{
    private readonly TestClusterFixture _fixture;

    public DesyncMessageTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task DesyncException_Message_CarriesMemberValues()
    {
        var server = new InProcessServer(_fixture.CreateHandlerFactory());

        var playerId = "dsg-msg-" + Guid.NewGuid().ToString("N")[..8];
        await using var client = new TestClientSetup(server, playerId);
        await client.ConnectAsync();

        var resolver = client.CreateResolver();
        var api = await resolver.GetServiceAsync<DesyncMessageServiceApiClient>(playerId);

        // Sell is deterministic; SellCargoResultComparer forces the mismatch so the Server-mode
        // throw path runs.
        var ex = await Assert.ThrowsAsync<DesyncException>(() => api.SellAsync(5));

        Assert.Contains("Gold = 10", ex.Message);
        Assert.Contains("Item = \"ore\"", ex.Message);

        // Nested DTO — proves the formatter recurses instead of stopping at the type name.
        Assert.Contains("Sku = \"SKU-7\"", ex.Message);
        Assert.Contains("Quantity = 5", ex.Message);

        // Collection elements, not just a type name.
        Assert.Contains("[1, 2, 3]", ex.Message);

        // The old rendering, which said nothing about what diverged.
        Assert.DoesNotContain("server=SharedMeta.Test.Meta1.SellCargoResult", ex.Message);
    }
}
