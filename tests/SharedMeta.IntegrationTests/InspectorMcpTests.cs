using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SharedMeta.Inspector;
using SharedMeta.Inspector.Mcp;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Server;
using Xunit;
using Ids = global::SharedMeta.Test.Meta1.Generated.GameMethodIds;

namespace SharedMeta.IntegrationTests;

/// <summary>The inspector over a real MCP endpoint (Kestrel, Streamable HTTP), and its access rules.</summary>
[Collection(InspectorClusterCollection.Name)]
public class InspectorMcpTests
{
    private readonly InspectorClusterFixture _fixture;

    public InspectorMcpTests(InspectorClusterFixture fixture) => _fixture = fixture;

    [Fact(Timeout = 60_000)]
    public async Task McpClient_WatchesAnEntity_AndReadsItsTimeline()
    {
        await _fixture.ResetAsync();
        await using var host = await StartAsync(_fixture.Runtime);
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(host.BaseUrl + "/mcp") }));

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "entity_timeline");
        Assert.Contains(tools, t => t.Name == "invoke_method");

        var status = await CallAsync(client, "inspector_status");
        Assert.True(status["captureAvailable"]!.GetValue<bool>());

        var entityId = $"insp_mcp_{Guid.NewGuid():N}";
        await CallAsync(client, "watch_entity", new() { ["entityId"] = entityId, ["level"] = "Full" });

        var bumpId = Ids.IForcePatchFixtureService_Bump_v1;
        var grain = _fixture.GrainFactory.GetGrain<IEntityGrain<ForcePatchFixtureState>>(entityId);
        Assert.False((await grain.HandleCallFromEntityAsync(new SharedMeta.Core.RpcCall
        {
            MethodId = bumpId,
            Payload = GeneratedMetaMethodDescriptors.Instance.EncodeArgs(bumpId, new object?[] { 2 }, _fixture.Serializer),
            ServerTimeTicks = DateTime.UtcNow.Ticks,
        })).HasError);
        await _fixture.Runtime.FlushAsync(TimeSpan.FromSeconds(10));

        var timeline = await CallAsync(client, "entity_timeline", new()
        {
            ["stateType"] = nameof(ForcePatchFixtureState),
            ["entityId"] = entityId,
            ["includePayloads"] = true,
        });
        var events = timeline["events"]!.AsArray();
        Assert.Contains(events, e => e!["method"]?.GetValue<string>() == "Bump" && e["args"]!["amount"]!.GetValue<int>() == 2);
        Assert.Contains(events, e => e!["callKind"]?.GetValue<string>() == "trigger");
        Assert.Contains("sequenceDiagram", timeline["mermaid"]!.GetValue<string>());

        var live = await CallAsync(client, "get_entity_state", new()
        {
            ["stateType"] = nameof(ForcePatchFixtureState),
            ["entityId"] = entityId,
            ["path"] = "Value",
        });
        Assert.Equal(2, live["state"]!.GetValue<int>());

        // A refusal reaches the client with its explanation, not the SDK's generic error text.
        var refused = await client.CallToolAsync("get_entity_state", new Dictionary<string, object?>
        {
            ["stateType"] = nameof(ForcePatchFixtureState),
            ["entityId"] = $"never_seen_{Guid.NewGuid():N}",
        });
        Assert.True(refused.IsError);
        Assert.Contains("activate=true", Assert.IsType<TextContentBlock>(Assert.Single(refused.Content)).Text);
    }

    [Theory(Timeout = 60_000)]
    [InlineData("Origin", "http://evil.example")]
    [InlineData("Host", "evil.example")]
    public async Task ForeignOriginOrHost_IsRefused(string header, string value)
    {
        await using var host = await StartAsync(_fixture.Runtime);
        using var http = new HttpClient();
        using var request = InitializeRequest(host.BaseUrl);
        if (header == "Host") request.Headers.Host = value;
        else request.Headers.Add(header, value);

        var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact(Timeout = 60_000)]
    public async Task ApiKey_IsRequiredWhenConfigured()
    {
        await using var runtime = new InspectorRuntime(new InspectorOptions { ApiKey = "s3cret" });
        await using var host = await StartAsync(runtime);
        using var http = new HttpClient();

        using var without = InitializeRequest(host.BaseUrl);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(without)).StatusCode);

        using var wrong = InitializeRequest(host.BaseUrl);
        wrong.Headers.Add(InspectorMcpExtensions.ApiKeyHeader, "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

        using var right = InitializeRequest(host.BaseUrl);
        right.Headers.Add("Authorization", "Bearer s3cret");
        Assert.Equal(HttpStatusCode.OK, (await http.SendAsync(right)).StatusCode);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static HttpRequestMessage InitializeRequest(string baseUrl)
    {
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1\"}}}";
        var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Accept", "application/json, text/event-stream");
        return request;
    }

    private static async Task<JsonNode> CallAsync(McpClient client, string tool, Dictionary<string, object?>? args = null)
    {
        var result = await client.CallToolAsync(tool, args ?? new Dictionary<string, object?>());
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return JsonNode.Parse(text)!;
    }

    private static async Task<HostHandle> StartAsync(InspectorRuntime runtime)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSharedMetaInspector(runtime);
        builder.Services.AddSharedMetaInspectorMcp();
        var app = builder.Build();
        app.MapSharedMetaInspectorMcp("/mcp");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HostHandle(app, address.TrimEnd('/'));
    }

    private sealed class HostHandle(WebApplication app, string baseUrl) : IAsyncDisposable
    {
        public string BaseUrl { get; } = baseUrl;

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
