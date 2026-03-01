using MessagePack;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SharedMeta.Serialization.MessagePack;

namespace SharedMeta.Transport.SignalR
{
    /// <summary>
    /// Extension methods for configuring MessagePack protocol on SignalR
    /// with the custom OrleansIdResolver that reads [Id(n)] attributes.
    /// </summary>
    public static class MetaMessagePackExtensions
    {
        /// <summary>
        /// Add MessagePack protocol to SignalR server with OrleansId-based serialization.
        /// Usage: builder.Services.AddSignalR().AddMetaMessagePackProtocol();
        /// </summary>
        public static ISignalRServerBuilder AddMetaMessagePackProtocol(this ISignalRServerBuilder builder)
        {
            return builder.AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MetaMessagePackOptions.Instance;
            });
        }

        /// <summary>
        /// Add MessagePack protocol to SignalR client with OrleansId-based serialization.
        /// Usage: new HubConnectionBuilder().AddMetaMessagePackProtocol();
        /// </summary>
        public static IHubConnectionBuilder AddMetaMessagePackProtocol(this IHubConnectionBuilder builder)
        {
            return builder.AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MetaMessagePackOptions.Instance;
            });
        }
    }
}
