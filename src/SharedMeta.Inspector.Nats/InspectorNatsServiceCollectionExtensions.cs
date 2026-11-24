using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace SharedMeta.Inspector.Nats;

public static class InspectorNatsServiceCollectionExtensions
{
    /// <summary>
    /// Connects the inspector to NATS JetStream: this process publishes its captured events to the
    /// stream, its store is fed with the events of every process on the stream, entity history is
    /// read back from the stream, and capture settings are shared through a key-value bucket — each
    /// part switchable in <see cref="NatsInspectorOptions"/>. Call after <c>AddSharedMetaInspector</c>,
    /// once per container that holds the inspector runtime; later calls are ignored (a second
    /// bridge would publish and ingest every event twice).
    /// </summary>
    public static IServiceCollection AddSharedMetaInspectorNats(this IServiceCollection services, Action<NatsInspectorOptions>? configure = null)
    {
        if (services.Any(d => d.ServiceType == typeof(NatsInspectorOptions))) return services;
        var options = new NatsInspectorOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddHostedService(sp => new NatsInspectorBridge(
            sp.GetRequiredService<InspectorRuntime>(), options, sp.GetService<ILoggerFactory>()));
        return services;
    }
}
