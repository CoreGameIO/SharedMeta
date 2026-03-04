using MessagePack;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using SharedMeta.Serialization.MessagePack;

namespace SharedMeta.Transport.SignalR
{
    /// <summary>
    /// Server-side extension for configuring MessagePack protocol on SignalR hub.
    /// Client-side extension is in SharedMeta.Transport.SignalR.MessagePack package
    /// (available transitively through this package).
    /// </summary>
    public static class MetaMessagePackServerExtensions
    {
        /// <summary>
        /// Add MessagePack protocol to SignalR server with SharedMeta's composite resolver.
        /// Usage: builder.Services.AddSignalR().AddMetaMessagePackProtocol();
        /// </summary>
        public static ISignalRServerBuilder AddMetaMessagePackProtocol(this ISignalRServerBuilder builder)
        {
            return builder.AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MetaMessagePackOptions.Instance;
            });
        }
    }
}
