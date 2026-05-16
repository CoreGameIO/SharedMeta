using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace SharedMeta.Debug.Mux
{
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
