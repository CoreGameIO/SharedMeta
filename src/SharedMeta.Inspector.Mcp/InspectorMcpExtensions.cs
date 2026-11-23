using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharedMeta.Inspector.Mcp;

public static class InspectorMcpExtensions
{
    /// <summary>Header carrying <see cref="InspectorOptions.ApiKey"/> (alternatively <c>Authorization: Bearer</c>).</summary>
    public const string ApiKeyHeader = "X-Inspector-Key";

    /// <summary>
    /// Registers the MCP server with the inspector tools (Streamable HTTP, stateless). Call after
    /// <c>AddSharedMetaInspector</c>, then map the endpoint with <see cref="MapSharedMetaInspectorMcp"/>.
    /// </summary>
    public static IServiceCollection AddSharedMetaInspectorMcp(this IServiceCollection services)
    {
        services.AddMcpServer(o =>
            {
                o.ServerInfo = new() { Name = "sharedmeta-inspector", Version = typeof(InspectorTools).Assembly.GetName().Version?.ToString() ?? "1.0" };
                o.ServerInstructions =
                    "Inspector of a running SharedMeta game server. Start with inspector_status and list_methods. " +
                    "Capture is off by default: use watch_entity / watch_player (or set_capture_config) before reproducing a scenario, " +
                    "then entity_timeline, entity_graph, get_call and entity_state_at to see what happened. Arguments are shown in wire form.";
            })
            .WithHttpTransport(o => o.Stateless = true)
            .WithTools<InspectorTools>();
        return services;
    }

    /// <summary>
    /// Maps the MCP endpoint. Requests are refused unless they pass the inspector's access rules:
    /// with <see cref="InspectorOptions.LocalhostOnly"/>, a loopback peer, a localhost Host and no
    /// foreign Origin (DNS rebinding / cross-site requests from a browser); with
    /// <see cref="InspectorOptions.ApiKey"/>, the key. The Origin check runs before the handler, so a
    /// host-wide permissive CORS policy cannot open the route to other sites.
    /// </summary>
    public static IEndpointConventionBuilder MapSharedMetaInspectorMcp(this IEndpointRouteBuilder endpoints, string path = "/mcp")
    {
        var options = endpoints.ServiceProvider.GetRequiredService<InspectorRuntime>().Options;
        if (!options.LocalhostOnly && string.IsNullOrEmpty(options.ApiKey))
        {
            endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SharedMeta.Inspector")
                .LogWarning("Inspector MCP at {Path} accepts remote connections without an ApiKey{Invoke}.",
                    path, options.AllowInvoke ? " and allows invoke_method" : "");
        }
        // No DisableCors metadata: it makes ASP.NET throw on hosts without CORS middleware.
        var builder = endpoints.MapMcp(path);
        builder.Finally(endpoint =>
        {
            var inner = endpoint.RequestDelegate;
            if (inner == null) return;
            endpoint.RequestDelegate = async context =>
            {
                var refusal = Check(context, options);
                if (refusal != null)
                {
                    context.Response.StatusCode = refusal.Value.Status;
                    await context.Response.WriteAsync(refusal.Value.Message);
                    return;
                }
                await inner(context);
            };
        });
        return builder;
    }

    internal static (int Status, string Message)? Check(HttpContext context, InspectorOptions options)
    {
        if (options.LocalhostOnly)
        {
            var remote = context.Connection.RemoteIpAddress;
            // A dual-stack listener reports IPv4 peers as ::ffff:a.b.c.d, which IsLoopback rejects.
            if (remote is { IsIPv4MappedToIPv6: true }) remote = remote.MapToIPv4();
            if (remote != null && !IPAddress.IsLoopback(remote))
                return (StatusCodes.Status403Forbidden, "Inspector MCP accepts loopback connections only.");
            if (!IsLocalHost(context.Request.Host.Host))
                return (StatusCodes.Status403Forbidden, "Inspector MCP requires a localhost Host header.");
            var origin = context.Request.Headers.Origin.ToString();
            if (!string.IsNullOrEmpty(origin)
                && (!Uri.TryCreate(origin, UriKind.Absolute, out var originUri) || !IsLocalHost(originUri.Host)))
                return (StatusCodes.Status403Forbidden, "Inspector MCP refuses cross-origin requests.");
        }

        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            var presented = context.Request.Headers[ApiKeyHeader].ToString();
            if (string.IsNullOrEmpty(presented))
            {
                var auth = context.Request.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) presented = auth.Substring(7).Trim();
            }
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(options.ApiKey!)))
                return (StatusCodes.Status401Unauthorized, "Inspector MCP key missing or wrong.");
        }
        return null;
    }

    private static bool IsLocalHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip)) return false;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return IPAddress.IsLoopback(ip);
    }
}
