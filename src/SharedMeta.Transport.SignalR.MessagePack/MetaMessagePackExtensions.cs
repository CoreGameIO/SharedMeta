using MessagePack;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using SharedMeta.Serialization.MessagePack;

namespace SharedMeta.Transport.SignalR
{
    /// <summary>
    /// Extension methods for configuring MessagePack protocol on SignalR client
    /// with the MetaMessagePackOptions composite resolver.
    /// </summary>
    public static class MetaMessagePackClientExtensions
    {
        /// <summary>
        /// Add MessagePack protocol to SignalR client with SharedMeta's composite resolver.
        /// Call <c>GeneratedMetaMessagePackConfiguration.Configure()</c> at startup before using this.
        /// Usage (with <c>CoreGame.SharedMeta.Transport.SignalR.Client</c>):
        /// <c>new SignalRConnection(url, token, b => b.AddMetaMessagePackProtocol())</c>
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
