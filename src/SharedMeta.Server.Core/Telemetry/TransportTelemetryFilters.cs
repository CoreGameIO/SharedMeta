using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Hub filter behind the opt-in transport metrics: one <c>sharedmeta.transport.*</c> record
    /// per hub invocation (tagged by hub method) plus an <c>ILogger</c> scope with connection id,
    /// player id and hub method around it. Serves <c>MetaHub</c> and the debug mux hub alike;
    /// registered through <see cref="TransportTelemetryExtensions.AddMetaSignalRTelemetry"/>.
    /// </summary>
    public sealed class HubTelemetryFilter : IHubFilter
    {
        private readonly string _transport;
        private readonly ILogger _logger;
        private readonly Func<bool> _logScopes;

        public HubTelemetryFilter(string transport, ILoggerFactory loggerFactory, bool logScopes)
            : this(transport, loggerFactory, () => logScopes)
        {
        }

        /// <param name="logScopes">Read per invocation, so <c>LogScopes</c> follows configuration reloads.</param>
        public HubTelemetryFilter(string transport, ILoggerFactory loggerFactory, Func<bool> logScopes)
        {
            _transport = transport;
            _logger = loggerFactory.CreateLogger("SharedMeta.Transport");
            _logScopes = logScopes;
        }

        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            var method = invocationContext.HubMethodName;
            var start = Stopwatch.GetTimestamp();
            using var scope = _logScopes() ? _logger.BeginScope(ScopeOf(invocationContext.Context, method)) : null;
            try
            {
                var result = await next(invocationContext);
                TransportTelemetry.Record(_transport, method, TransportTelemetry.Success, start);
                return result;
            }
            catch (Exception ex)
            {
                TransportTelemetry.Record(_transport, method,
                    ex is UnauthorizedAccessException ? TransportTelemetry.Unauthorized : TransportTelemetry.Error, start);
                throw;
            }
        }

        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                await next(context);
                TransportTelemetry.Record(_transport, "OnConnected", TransportTelemetry.Success, start);
            }
            catch
            {
                TransportTelemetry.Record(_transport, "OnConnected", TransportTelemetry.Error, start);
                throw;
            }
        }

        public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
            Func<HubLifetimeContext, Exception?, Task> next)
        {
            var start = Stopwatch.GetTimestamp();
            try
            {
                await next(context, exception);
                TransportTelemetry.Record(_transport, "OnDisconnected",
                    exception == null ? TransportTelemetry.Success : TransportTelemetry.Error, start);
            }
            catch
            {
                TransportTelemetry.Record(_transport, "OnDisconnected", TransportTelemetry.Error, start);
                throw;
            }
        }

        // No connection id here: SignalR's own scope already carries it as TransportConnectionId,
        // and ASP.NET's ConnectionId is the Kestrel connection — a third one would only confuse.
        private static KeyValuePair<string, object?>[] ScopeOf(HubCallerContext context, string method)
        {
            var player = context.User?.FindFirst("sub")?.Value
                         ?? context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return player != null
                ? new[]
                {
                    new KeyValuePair<string, object?>("PlayerId", player),
                    new KeyValuePair<string, object?>("HubMethod", method),
                }
                : new[] { new KeyValuePair<string, object?>("HubMethod", method) };
        }
    }

    /// <summary>
    /// Endpoint filter behind the opt-in transport metrics for HTTP endpoints. The <c>endpoint</c>
    /// tag is the route pattern (bounded by the app's routes, never the request path).
    /// </summary>
    public sealed class HttpTelemetryEndpointFilter : IEndpointFilter
    {
        private readonly string _transport;
        private readonly Func<string, bool>? _skipDuration;

        public HttpTelemetryEndpointFilter(string transport, Func<string, bool>? skipDuration = null)
        {
            _transport = transport;
            _skipDuration = skipDuration;
        }

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var endpoint = (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unknown";
            var start = Stopwatch.GetTimestamp();
            bool recordDuration = _skipDuration == null || !_skipDuration(endpoint);
            try
            {
                var result = await next(context);
                var status = result is IStatusCodeHttpResult { StatusCode: { } code } ? code : http.Response.StatusCode;
                TransportTelemetry.Record(_transport, endpoint, TransportTelemetry.ResultOf(status), start, recordDuration);
                return result;
            }
            catch
            {
                TransportTelemetry.Record(_transport, endpoint, TransportTelemetry.Error, start, recordDuration);
                throw;
            }
        }
    }

    public static class TransportTelemetryExtensions
    {
        /// <summary>
        /// Adds <see cref="HubTelemetryFilter"/> to hub <typeparamref name="THub"/> when
        /// <see cref="MetaTelemetryOptions.TransportMetrics"/> is on. The flag is read once, when
        /// the hub options are built: off means the filter is not in the pipeline at all.
        /// </summary>
        public static ISignalRServerBuilder AddMetaHubTelemetry<THub>(this ISignalRServerBuilder builder, string transport)
            where THub : Hub
        {
            builder.Services.AddSharedMetaTelemetry();
            // AddHubOptions is what makes SignalR read per-hub options at all: it marks them as
            // set (copying the global ones). A bare Configure<HubOptions<THub>> is silently
            // ignored and the hub keeps using the global filter list.
            builder.AddHubOptions<THub>(_ => { });
            builder.Services.AddOptions<HubOptions<THub>>()
                .Configure<IOptionsMonitor<MetaTelemetryOptions>, ILoggerFactory>((hub, telemetry, loggers) =>
                {
                    if (telemetry.CurrentValue.TransportMetrics)
                        hub.AddFilter(new HubTelemetryFilter(transport, loggers, () => telemetry.CurrentValue.LogScopes));
                });
            return builder;
        }

        /// <summary>
        /// Transport metrics for <c>MetaHub</c> (or a hub derived from it):
        /// <code>
        /// builder.Services.AddSignalR().AddMetaMessagePackProtocol().AddMetaSignalRTelemetry&lt;MetaHub&gt;();
        /// builder.Services.AddSharedMetaTelemetry(o =&gt; o.TransportMetrics = true);
        /// </code>
        /// </summary>
        public static ISignalRServerBuilder AddMetaSignalRTelemetry<THub>(this ISignalRServerBuilder builder)
            where THub : Hub
            => builder.AddMetaHubTelemetry<THub>(TransportTelemetry.SignalR);

        /// <summary>
        /// Adds <see cref="HttpTelemetryEndpointFilter"/> to <paramref name="builder"/> when
        /// <see cref="MetaTelemetryOptions.TransportMetrics"/> is on at mapping time; otherwise
        /// returns it untouched.
        /// </summary>
        public static TBuilder WithMetaTransportTelemetry<TBuilder>(
            this TBuilder builder, IServiceProvider services, string transport, Func<string, bool>? skipDuration = null)
            where TBuilder : IEndpointConventionBuilder
        {
            var options = services.GetService<IOptions<MetaTelemetryOptions>>()?.Value;
            if (options?.TransportMetrics != true) return builder;
            builder.AddEndpointFilter(new HttpTelemetryEndpointFilter(transport, skipDuration));
            return builder;
        }
    }
}
