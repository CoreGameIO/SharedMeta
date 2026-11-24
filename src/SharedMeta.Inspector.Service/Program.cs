using SharedMeta.Inspector;
using SharedMeta.Inspector.Mcp;
using SharedMeta.Inspector.Nats;

// Standalone inspector: the cluster's events from NATS JetStream, served over MCP. No silo here, so
// no live state or invoke — those stay with the inspector inside a silo.
//
//   sharedmeta-inspector --nats nats://127.0.0.1:4222 --port 5300
//
// Settings: command line (--key value), environment (SHAREDMETA_INSPECTOR_<KEY> with '_' for '-',
// e.g. SHAREDMETA_INSPECTOR_API_KEY) or appsettings.json.
//   nats            server URL                                   (nats://127.0.0.1:4222)
//   port            MCP port                                     (5300)
//   stream          JetStream stream                             (SHAREDMETA_INSPECTOR)
//   prefix          subject prefix                               (sharedmeta.inspector)
//   bucket          capture-settings bucket                      (sharedmeta-inspector)
//   warm-minutes    events loaded on start                       (60)
//   max-events      events kept in memory                        (200000)
//   manage-stream   create the stream / bucket if missing        (false: the silos do)
//   api-key            required on MCP requests when set
//   allow-remote       listen on all interfaces (set api-key too)   (false)
//   env                environment badge label (e.g. DEV, STAGING)
//   path-base          optional reverse-proxy subpath (e.g. /dev/inspector)
//   max-global-minutes optional auto-revert cap for global capture level

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("SHAREDMETA_INSPECTOR_");
var config = builder.Configuration;

// Environment variable names cannot carry '-': every key is also read with '_'.
string? Setting(string key) => config[key] ?? config[key.Replace('-', '_')];
T Value<T>(string key, T fallback) => config.GetValue(key, config.GetValue(key.Replace('-', '_'), fallback))!;

var port = Value("port", 5300);
var allowRemote = Value("allow-remote", false);
var pathBase = Setting("path-base");
var maxGlobalMinutes = Value("max-global-minutes", 0);
builder.WebHost.UseUrls($"http://{(allowRemote ? "0.0.0.0" : "localhost")}:{port}");

builder.Services.AddSharedMetaInspector(o =>
{
    o.MaxEvents = Value("max-events", 200_000);
    o.ApiKey = Setting("api-key");
    o.LocalhostOnly = !allowRemote;
    o.EnvironmentName = Setting("env");
    if (maxGlobalMinutes > 0) o.MaxGlobalCaptureDuration = TimeSpan.FromMinutes(maxGlobalMinutes);
});
builder.Services.AddSharedMetaInspectorNats(o =>
{
    o.Url = Setting("nats") ?? o.Url;
    o.StreamName = Setting("stream") ?? o.StreamName;
    o.SubjectPrefix = Setting("prefix") ?? o.SubjectPrefix;
    o.ControlBucket = Setting("bucket") ?? o.ControlBucket;
    o.ManageStream = Value("manage-stream", false);
    o.WarmStart = TimeSpan.FromMinutes(Value("warm-minutes", 60));
    o.Publish = false;
});
builder.Services.AddSharedMetaInspectorMcp();

var app = builder.Build();
if (!string.IsNullOrWhiteSpace(pathBase))
{
    app.UsePathBase(pathBase.StartsWith('/') ? pathBase : "/" + pathBase);
}
app.MapSharedMetaInspectorMcp("/mcp");
app.MapSharedMetaInspectorUi("/inspector", "/mcp");
app.MapGet("/", (HttpContext ctx) => Results.Redirect((ctx.Request.PathBase.Value ?? "").TrimEnd('/') + "/inspector/"));
var hostLabel = allowRemote ? "0.0.0.0" : "localhost";
var prefixDisplay = string.IsNullOrWhiteSpace(pathBase) ? "" : (pathBase.StartsWith('/') ? pathBase.TrimEnd('/') : "/" + pathBase.TrimEnd('/'));
app.Logger.LogInformation("SharedMeta inspector MCP at http://{Host}:{Port}{Prefix}/mcp", hostLabel, port, prefixDisplay);
app.Logger.LogInformation("SharedMeta inspector UI  at http://{Host}:{Port}{Prefix}/inspector/", hostLabel, port, prefixDisplay);
app.Run();
