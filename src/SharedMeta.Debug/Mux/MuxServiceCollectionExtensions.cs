using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using SharedMeta.Server.Core.Telemetry;

namespace SharedMeta.Debug.Mux
{
    public static class MuxTelemetryExtensions
    {
        /// <summary>
        /// Per-hub-method transport metrics (<c>transport="mux"</c>) for <see cref="MuxHub"/>, active
        /// when <see cref="MetaTelemetryOptions.TransportMetrics"/> is on. A <c>BatchRpcCall</c> counts
        /// as one invocation; <c>sharedmeta.transport.batch_size</c> records how many calls it carried.
        /// </summary>
        public static ISignalRServerBuilder AddMetaMuxTelemetry(this ISignalRServerBuilder builder)
            => builder.AddMetaHubTelemetry<MuxHub>(TransportTelemetry.Mux);
    }

    /// <summary>
    /// ASP.NET endpoint helper for hosting <see cref="MuxHub"/>. Use alongside the
    /// regular <c>MetaHub</c> mapping — production / regular clients keep using
    /// <c>"/meta"</c> while stress tests opt in to the multiplexed endpoint.
    /// </summary>
    public static class MuxEndpointExtensions
    {
        /// <summary>
        /// Map the multiplexed hub at <paramref name="path"/> (default <c>"/meta-mux"</c>).
        /// </summary>
        public static IEndpointRouteBuilder MapMetaMuxHub(this IEndpointRouteBuilder endpoints, string path = "/meta-mux")
        {
            endpoints.MapHub<MuxHub>(path);
            return endpoints;
        }
    }
}
