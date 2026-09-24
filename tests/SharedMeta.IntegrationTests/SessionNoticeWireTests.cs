using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharedMeta.Core;
using SharedMeta.IntegrationTests.Infrastructure;
using SharedMeta.Server.Core.Grains;
using SharedMeta.Server.Core.Transport;
using SharedMeta.Test.Meta1;
using SharedMeta.Test.Meta1.Client;
using SharedMeta.Transport.HttpPolling;
using Xunit;

namespace SharedMeta.IntegrationTests;

/// <summary>
/// Session notices over a real wire: Kestrel, the HTTP polling endpoints, JSON both ways and the
/// shipped client connection. The in-process transport hands the notice object across by reference,
/// so it cannot catch a transport that never raises the event, a poll body that drops the field, or
/// a JSON context that does not know the type. Each of those leaves a permission change applied on
/// the server while the client keeps refusing calls against the set it heard at connect.
/// </summary>
[Collection(TestClusterCollection.Name)]
public class SessionNoticeWireTests
{
    private readonly TestClusterFixture _fixture;

    public SessionNoticeWireTests(TestClusterFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact(Timeout = 60_000)]
    public async Task GrantWhileConnected_ReachesClientOverHttpPolling()
    {
        await using var host = await StartHttpPollingHostAsync();

        var playerId = "notice_http_" + Guid.NewGuid().ToString("N");
        var connection = new HttpPollingConnection(new HttpPollingConnectionOptions
        {
            ServerUrl = host.BaseUrl + "/meta",
            ClientVersion = "1.0.0",
        });
        var setup = new TestClientSetup(connection, playerId);
        await using var _ = setup;
        await setup.ConnectAsync();

        var entityId = $"notice_http_{Guid.NewGuid():N}";
        var resolver = setup.CreateResolver();
        var api = await resolver.GetServiceAsync<CounterServiceApiClient>(entityId);
        await Assert.ThrowsAsync<MetaPermissionDeniedException>(() => api.CheatSetSumAsync(1));

        var pushed = new TaskCompletionSource<PlayerPermissions>(TaskCreationOptions.RunContinuationsAsynchronously);
        setup.MetaClient.Dispatcher.PermissionsChanged += p => pushed.TrySetResult(p);

        await new SharedMeta.Server.Core.Permissions.GrainPlayerEntitlements(_fixture.GrainFactory)
            .GrantAsync(playerId, new[] { "Cheat" });

        var set = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(set.Has("Cheat"));

        // Admitted by the client's gate and by the server's, which adopted the same notice.
        await api.CheatSetSumAsync(55);
        Assert.Equal(55, resolver.GetState<CounterState>(entityId).Sum);
    }

    private async Task<HostHandle> StartHttpPollingHostAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new HttpPollingConnectionManager(
            _fixture.CreateHandlerFactory(), _fixture.LoggerFactory));
        // Resolved by the endpoints as an optional service; left unregistered, minimal APIs would
        // bind it from the request body instead.
        builder.Services.AddSingleton(new MetaTransportOptions());

        var app = builder.Build();
        app.MapMetaHttpPolling("/meta");
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HostHandle(app, address.TrimEnd('/'));
    }

    private sealed class HostHandle : IAsyncDisposable
    {
        private readonly WebApplication _app;
        public string BaseUrl { get; }

        public HostHandle(WebApplication app, string baseUrl)
        {
            _app = app;
            BaseUrl = baseUrl;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
