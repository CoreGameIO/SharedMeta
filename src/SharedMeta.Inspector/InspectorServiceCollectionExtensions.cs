using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Orleans;
using Orleans.Runtime;
using SharedMeta.Core.Diagnostics;
using SharedMeta.Server.Core.Diagnostics;

namespace SharedMeta.Inspector;

public static class InspectorServiceCollectionExtensions
{
    /// <summary>
    /// Registers the inspector: the call tap read by entity grains, the outgoing grain-call filter,
    /// the transport observer, and <see cref="InspectorRuntime"/> / <see cref="InspectorService"/>.
    /// Idempotent. Register in the container the silo and the transport resolve from — in a
    /// <c>WebApplication</c> with <c>UseOrleans</c> that is one call on <c>builder.Services</c>.
    /// </summary>
    public static IServiceCollection AddSharedMetaInspector(this IServiceCollection services, Action<InspectorOptions>? configure = null)
    {
        if (services.Any(d => d.ServiceType == typeof(InspectorRuntime))) return services;
        var options = new InspectorOptions();
        configure?.Invoke(options);
        var runtime = new InspectorRuntime(options);
        // Created here, so the container owns it and disposes it with the host.
        services.AddSingleton<InspectorRuntime>(_ => runtime);
        return AddComponents(services);
    }

    /// <summary>
    /// Same, with a runtime created by the host. Use it when the web host and the silo resolve
    /// from different containers: pass the same instance to both, so they share one store.
    /// </summary>
    public static IServiceCollection AddSharedMetaInspector(this IServiceCollection services, InspectorRuntime runtime)
    {
        if (services.Any(d => d.ServiceType == typeof(InspectorRuntime))) return services;
        // Registered as an instance: the host owns it (it may be shared with another container),
        // so no container disposes it.
        services.AddSingleton(runtime);
        return AddComponents(services);
    }

    private static IServiceCollection AddComponents(IServiceCollection services)
    {
        services.TryAddSingleton<InspectorService>();

        // Silo-side components bind the runtime to the silo's services (serializer, method table,
        // grain factory); the transport-side observer and the tools never do, so a separate web
        // container cannot bind it to the wrong ones. Entity grains take one tap; this is it.
        services.AddSingleton<IMetaCallTap>(sp => new InspectorCallTap(BoundRuntime(sp)));
        services.AddSingleton<IOutgoingGrainCallFilter>(sp => new InspectorGrainCallFilter(BoundRuntime(sp)));
        services.AddSingleton<IMetaServerObserver>(sp => sp.GetRequiredService<InspectorRuntime>().ServerObserver);
        // The tap and the filter bind lazily on first resolve; this binds at startup so the tools
        // see the method table before any grain activates.
        services.AddHostedService<SiloBinding>();
        return services;
    }

    private sealed class SiloBinding(IServiceProvider services) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (IsSilo(services)) BoundRuntime(services);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        private static bool IsSilo(IServiceProvider sp)
        {
            try { return sp.GetService<ILocalSiloDetails>() != null; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private static InspectorRuntime BoundRuntime(IServiceProvider sp)
    {
        var runtime = sp.GetRequiredService<InspectorRuntime>();
        runtime.Bind(sp);
        return runtime;
    }
}
