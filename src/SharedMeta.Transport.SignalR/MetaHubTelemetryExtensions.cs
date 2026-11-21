using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using SharedMeta.Server.Core.Telemetry;

namespace SharedMeta.Transport.SignalR
{
    public static class MetaHubTelemetryExtensions
    {
        /// <summary>
        /// Per-hub-method transport metrics and log scope for <see cref="MetaHub"/>, active when
        /// <see cref="MetaTelemetryOptions.TransportMetrics"/> is on (read once at startup).
        /// <code>
        /// builder.Services.AddSignalR().AddMetaMessagePackProtocol().AddMetaSignalRTelemetry();
        /// builder.Services.AddSharedMetaTelemetry(o =&gt; o.TransportMetrics = true);
        /// </code>
        /// A host hub derived from <see cref="MetaHub"/> uses <c>AddMetaSignalRTelemetry&lt;THub&gt;()</c>.
        /// </summary>
        public static ISignalRServerBuilder AddMetaSignalRTelemetry(this ISignalRServerBuilder builder)
            => builder.AddMetaSignalRTelemetry<MetaHub>();
    }
}
