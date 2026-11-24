using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SharedMeta.Inspector.Mcp;

public static class InspectorUiExtensions
{
    private const string ResourcePrefix = "SharedMeta.Inspector.Mcp.Ui.";

    private static readonly Dictionary<string, string> s_assets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["styles.css"] = "text/css; charset=utf-8",
        ["mcp.js"] = "application/javascript; charset=utf-8",
        ["json-view.js"] = "application/javascript; charset=utf-8",
        ["timeline.js"] = "application/javascript; charset=utf-8",
        ["graph.js"] = "application/javascript; charset=utf-8",
        ["app.js"] = "application/javascript; charset=utf-8",
    };

    /// <summary>
    /// Maps the embedded Inspector Web Viewer (TimeMachine timeline, dependency graph, live event
    /// stream, entity/session explorer, live state, method invoker and capture settings). All data
    /// calls go through the MCP endpoint at <paramref name="mcpPath"/>, which continues to enforce
    /// <see cref="InspectorOptions.ApiKey"/>; static UI routes enforce
    /// <see cref="InspectorOptions.LocalhostOnly"/> host/origin checks.
    /// </summary>
    public static IEndpointRouteBuilder MapSharedMetaInspectorUi(
        this IEndpointRouteBuilder endpoints,
        string path = "/inspector",
        string mcpPath = "/mcp")
    {
        var options = endpoints.ServiceProvider.GetRequiredService<InspectorRuntime>().Options;
        var basePath = NormalizeBasePath(path);
        var normalizedMcp = string.IsNullOrWhiteSpace(mcpPath)
            ? "/mcp"
            : mcpPath.StartsWith('/') ? mcpPath : "/" + mcpPath;

        var indexBytes = Encoding.UTF8.GetBytes(
            ReadResource("index.html").Replace("{{MCP_PATH}}", WebUtility.HtmlEncode(normalizedMcp)));

        var rootRoute = basePath.Length > 0 ? basePath : "/";
        endpoints.MapGet(rootRoute, async context =>
        {
            if (!await PassHostCheckAsync(context, options)) return;
            if (basePath.Length > 0 && !(context.Request.Path.Value?.EndsWith('/') ?? false))
            {
                context.Response.Redirect(basePath + "/" + context.Request.QueryString.Value, permanent: false);
                return;
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.Body.WriteAsync(indexBytes);
        });

        foreach (var (fileName, contentType) in s_assets)
        {
            var payload = Encoding.UTF8.GetBytes(ReadResource(fileName));
            endpoints.MapGet(basePath + "/" + fileName, async context =>
            {
                if (!await PassHostCheckAsync(context, options)) return;
                context.Response.ContentType = contentType;
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.Body.WriteAsync(payload);
            });
        }

        return endpoints;
    }

    private static async ValueTask<bool> PassHostCheckAsync(HttpContext context, InspectorOptions options)
    {
        var refusal = InspectorMcpExtensions.CheckHostAndOrigin(context, options);
        if (refusal == null) return true;
        context.Response.StatusCode = refusal.Value.Status;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(refusal.Value.Message);
        return false;
    }

    private static string NormalizeBasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/") return "";
        var trimmed = path.Trim().TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string ReadResource(string fileName)
    {
        var assembly = typeof(InspectorUiExtensions).Assembly;
        var resourceName = ResourcePrefix + fileName;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded UI resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
