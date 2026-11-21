using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>DI wiring for SharedMeta observability.</summary>
    public static class TelemetryServiceCollectionExtensions
    {
        /// <summary>
        /// Registers <see cref="MetaTelemetryOptions"/> and the <see cref="SharedMetaTelemetry"/>
        /// singleton that the transport handler, session grain and entity grain read.
        /// <para>
        /// Call it in every container that hosts SharedMeta parts — the web host (transport
        /// handler, hub / endpoint filters) and the silo (grains) when they are separate. Idempotent:
        /// repeated calls only add further <paramref name="configure"/> actions.
        /// </para>
        /// <para>
        /// Without this call everything still works on default options (<see cref="SharedMetaTelemetry.Default"/>).
        /// </para>
        /// </summary>
        public static IServiceCollection AddSharedMetaTelemetry(
            this IServiceCollection services,
            Action<MetaTelemetryOptions>? configure = null)
        {
            services.AddOptions<MetaTelemetryOptions>();
            if (configure != null) services.Configure(configure);

            services.TryAddSingleton(sp => new SharedMetaTelemetry(
                sp.GetService<IOptionsMonitor<MetaTelemetryOptions>>(),
                sp.GetServices<IMetaServerObserver>(),
                sp.GetService<ILoggerFactory>()));

            // The analytics-log observer is always registered and decides per event from the
            // current options, so turning AnalyticsLog on in configuration needs no restart.
            if (!services.Any(d => d.ServiceType == typeof(IMetaServerObserver)
                                   && d.ImplementationType == typeof(LoggingServerObserver)))
                services.AddSingleton<IMetaServerObserver, LoggingServerObserver>();
            return services;
        }
    }
}
