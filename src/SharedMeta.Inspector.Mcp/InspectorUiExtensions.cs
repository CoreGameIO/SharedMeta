using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace SharedMeta.Inspector.Mcp;

public static class InspectorUiExtensions
{
    private const string ResourcePrefix = "SharedMeta.Inspector.Mcp.Ui.";
    private const string McpPathToken = "{{MCP_PATH}}";

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
    /// stream, entity/session explorer, live state, method invoker and capture settings) as a
    /// <see cref="RouteGroupBuilder"/> so hosts can chain conventions such as
    /// <c>.RequireAuthorization("DevAdmin")</c>. Redirects and the injected MCP endpoint path honor
    /// <see cref="HttpRequest.PathBase"/> for reverse-proxy subpath deployments.
    /// </summary>
    public static RouteGroupBuilder MapSharedMetaInspectorUi(
        this IEndpointRouteBuilder endpoints,
        string path = "/inspector",
        string mcpPath = "/mcp")
    {
        var options = endpoints.ServiceProvider.GetRequiredService<InspectorRuntime>().Options;
        var basePath = NormalizeBasePath(path);
        var normalizedMcp = string.IsNullOrWhiteSpace(mcpPath)
            ? "/mcp"
            : mcpPath.StartsWith('/') ? mcpPath : "/" + mcpPath;

        var indexTemplate = ReadResource("index.html");
        var tokenIndex = indexTemplate.IndexOf(McpPathToken, StringComparison.Ordinal);
        if (tokenIndex < 0)
            throw new InvalidOperationException($"Embedded index.html is missing '{McpPathToken}'.");
        var indexPrefix = Encoding.UTF8.GetBytes(indexTemplate.Substring(0, tokenIndex));
        var indexSuffix = Encoding.UTF8.GetBytes(indexTemplate.Substring(tokenIndex + McpPathToken.Length));

        var group = endpoints.MapGroup(basePath);

        group.MapGet(basePath.Length > 0 ? "" : "/", async context =>
        {
            if (!await PassHostCheckAsync(context, options)) return;
            var pathBase = (context.Request.PathBase.Value ?? "").TrimEnd('/');
            if (basePath.Length > 0 && !(context.Request.Path.Value?.EndsWith('/') ?? false))
            {
                context.Response.Redirect(pathBase + basePath + "/" + context.Request.QueryString.Value, permanent: false);
                return;
            }
            var resolvedMcp = WebUtility.HtmlEncode(pathBase + normalizedMcp);
            var mcpBytes = Encoding.UTF8.GetBytes(resolvedMcp);
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.Body.WriteAsync(indexPrefix);
            await context.Response.Body.WriteAsync(mcpBytes);
            await context.Response.Body.WriteAsync(indexSuffix);
        });

        foreach (var (fileName, contentType) in s_assets)
        {
            var payload = Encoding.UTF8.GetBytes(ReadResource(fileName));
            group.MapGet("/" + fileName, async context =>
            {
                if (!await PassHostCheckAsync(context, options)) return;
                context.Response.ContentType = contentType;
                context.Response.Headers.CacheControl = "no-cache";
                await context.Response.Body.WriteAsync(payload);
            });
        }

        return group;
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
