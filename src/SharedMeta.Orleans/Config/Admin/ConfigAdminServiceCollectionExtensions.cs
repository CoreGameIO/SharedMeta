using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SharedMeta.Core;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Config.Admin;

namespace SharedMeta.Orleans.Config.Admin
{
    /// <summary>
    /// 0.27.0+ Options bag for <see cref="ConfigsServiceCollectionExtensions.ConfigureConfigs"/>.
    /// Picks the bootstrapper (default-instance / directory / project-typed) and the cold-start
    /// strategy.
    ///
    /// <para>
    /// 0.27.1: <c>OnBeforeSeed</c> and <c>UseLoader</c> were removed — projects that need
    /// pre-bootstrap work register their own <c>IHostedService</c> ahead of
    /// <c>ConfigureConfigs</c>; projects with inline custom seeds implement
    /// <see cref="IConfigBootstrapper"/> directly (it's two short methods).
    /// </para>
    /// </summary>
    public sealed class ConfigsOptions
    {
        /// <summary>
        /// When the framework's hosted service should publish. Default:
        /// <see cref="ConfigSeedStrategy.LoadIfNew"/>.
        /// </summary>
        public ConfigSeedStrategy Strategy { get; set; } = ConfigSeedStrategy.LoadIfNew;

        internal Type? BootstrapperType { get; private set; }
        internal IConfigBootstrapper? BootstrapperInstance { get; private set; }
        internal ServiceLifetime BootstrapperLifetime { get; private set; } = ServiceLifetime.Singleton;
        internal Func<IServiceProvider, IConfigBootstrapper>? BootstrapperFactory { get; private set; }

        /// <summary>Register a typed <see cref="IConfigBootstrapper"/> implementation.</summary>
        public ConfigsOptions UseBootstrapper<TBootstrapper>(ServiceLifetime lifetime = ServiceLifetime.Singleton)
            where TBootstrapper : class, IConfigBootstrapper
        {
            ResetBootstrapper();
            BootstrapperType = typeof(TBootstrapper);
            BootstrapperLifetime = lifetime;
            return this;
        }

        /// <summary>Register a specific <see cref="IConfigBootstrapper"/> instance (singleton).</summary>
        public ConfigsOptions UseBootstrapper(IConfigBootstrapper instance)
        {
            ResetBootstrapper();
            BootstrapperInstance = instance ?? throw new ArgumentNullException(nameof(instance));
            BootstrapperLifetime = ServiceLifetime.Singleton;
            return this;
        }

        /// <summary>
        /// Use the built-in <see cref="DirectoryConfigBootstrapper"/>: scans (read-only)
        /// <c>{root}/{Type.Name}/{Major.Minor.Patch}.bin</c> and seeds the highest matching
        /// version per config. Suitable for Docker images that bake .bin files into the
        /// image at build time. Projects whose dev loop writes the bin files (YAML →
        /// compile → bin) register that step as a separate <c>IHostedService</c> ahead of
        /// <c>ConfigureConfigs</c>.
        /// </summary>
        public ConfigsOptions UseDirectorySeed(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Seed directory root is required.", nameof(root));
            ResetBootstrapper();
            BootstrapperFactory = sp => new DirectoryConfigBootstrapper(
                root, sp.GetService<ILogger<DirectoryConfigBootstrapper>>());
            BootstrapperLifetime = ServiceLifetime.Singleton;
            return this;
        }

        /// <summary>
        /// 0.27.1+ Pure in-memory bootstrap: <see cref="Activator.CreateInstance"/> per
        /// <c>[MetaConfig]</c> type, serialized via <see cref="IMetaSerializer"/>, published
        /// under <paramref name="version"/>. No filesystem reads/writes — works in read-only
        /// Docker images. The default for fresh projects: edit the C# field initializers,
        /// restart, registry updates (under <see cref="ConfigSeedStrategy.LoadIfNew"/> /
        /// <see cref="ConfigSeedStrategy.LoadAlways"/>).
        /// </summary>
        public ConfigsOptions UseDefaultInstances(MetaConfigVersion version)
        {
            ResetBootstrapper();
            BootstrapperFactory = sp => new DefaultInstanceConfigBootstrapper(
                version,
                sp.GetRequiredService<IMetaSerializer>(),
                sp.GetService<ILogger<DefaultInstanceConfigBootstrapper>>());
            BootstrapperLifetime = ServiceLifetime.Singleton;
            return this;
        }

        /// <summary>Convenience overload that parses a <c>"M.m.p"</c> string.</summary>
        public ConfigsOptions UseDefaultInstances(string version = "0.1.0")
            => UseDefaultInstances(MetaConfigVersion.Parse(version));

        private void ResetBootstrapper()
        {
            BootstrapperType = null;
            BootstrapperInstance = null;
            BootstrapperFactory = null;
        }
    }

    /// <summary>
    /// 0.27.0+ DI wiring entry point for the SharedMeta config subsystem. Replaces the
    /// previous mix of <c>AddSharedMetaConfigVersioning()</c> + <c>AddSharedMetaConfigProvider&lt;T&gt;()</c> +
    /// <c>AddMetaConfigPublicUrl()</c> + <c>AddSharedMetaConfigAdmin()</c> with one
    /// umbrella call. Per-type <c>BroadcastingConfigProvider&lt;T&gt;</c> registrations
    /// are auto-emitted by the generated <c>ConfigureMeta()</c> for every <c>[MetaConfig]</c>
    /// type — projects don't list them manually anymore.
    /// </summary>
    public static class ConfigsServiceCollectionExtensions
    {
        /// <summary>
        /// Wire SharedMeta config registry + per-strategy bootstrap + appsettings-bound
        /// <c>ClientVersionOptions</c> + auto-registered <c>DefaultClientVersionService</c>
        /// (<see cref="IConfigVersionResolver"/> + grain-backed runtime Current).
        /// Typical use:
        /// <code>
        /// siloBuilder.ConfigureServices(services =>
        /// {
        ///     services.ConfigureMeta(svc => { /* impls */ });
        ///     services.ConfigureConfigs(o =>
        ///     {
        ///         o.UseDefaultInstances("0.1.0");                  // in-memory defaults from C#
        ///         o.Strategy = ConfigSeedStrategy.LoadIfNew;
        ///     });
        ///     // Public download URL stays a separate one-liner — lives in
        ///     // SharedMeta.Transport.SignalR to avoid a circular project ref:
        ///     services.AddMetaConfigPublicUrl($"http://localhost:{port}");
        /// });
        /// </code>
        /// </summary>
        public static IServiceCollection ConfigureConfigs(
            this IServiceCollection services,
            Action<ConfigsOptions> configure)
        {
            if (services is null) throw new ArgumentNullException(nameof(services));
            if (configure is null) throw new ArgumentNullException(nameof(configure));

            var opts = new ConfigsOptions();
            configure(opts);

            // Registry façade. Idempotent (TryAdd inside AddSharedMetaConfigVersioning).
            services.AddSharedMetaConfigVersioning();

            // Push the options bag so the hosted service can read Strategy.
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(opts));

            // Register the bootstrapper IF the project supplied one. If not, hosted service
            // wouldn't have anything to call — skip the service registration too.
            var hasBootstrapper =
                opts.BootstrapperType != null ||
                opts.BootstrapperInstance != null ||
                opts.BootstrapperFactory != null;

            if (opts.BootstrapperInstance != null)
            {
                services.AddSingleton(opts.BootstrapperInstance);
            }
            else if (opts.BootstrapperFactory != null)
            {
                services.AddSingleton(opts.BootstrapperFactory);
            }
            else if (opts.BootstrapperType != null)
            {
                services.Add(new ServiceDescriptor(
                    serviceType: typeof(IConfigBootstrapper),
                    implementationType: opts.BootstrapperType,
                    lifetime: opts.BootstrapperLifetime));
            }

            if (hasBootstrapper)
                services.AddHostedService<ConfigBootstrapHostedService>();

            // Bind appsettings "ClientVersion" section into ClientVersionOptions if the
            // host has an IConfiguration in DI (true for any WebApplication / HostBuilder).
            // Project pre-binds the same section (services.Configure<ClientVersionOptions>(...))
            // to pre-empt — both registrations compose (last-wins on overlapping keys).
            services.AddOptions<ClientVersionOptions>()
                .Configure<IConfiguration>((cvo, config) =>
                {
                    var section = config.GetSection(ClientVersionOptions.SectionName);
                    if (!section.Exists()) return;
                    var current = section[nameof(ClientVersionOptions.Current)];
                    if (!string.IsNullOrWhiteSpace(current)) cvo.Current = current;
                    cvo.Min = section[nameof(ClientVersionOptions.Min)] ?? cvo.Min;
                    cvo.Max = section[nameof(ClientVersionOptions.Max)] ?? cvo.Max;
                    cvo.Server = section[nameof(ClientVersionOptions.Server)] ?? cvo.Server;
                });

            // Default IConfigVersionResolver: grain-backed Current + appsettings bootstrap.
            // Project pre-registers its own IConfigVersionResolver before this call to pre-empt
            // (TryAddSingleton no-ops on existing impl). The hosted service registration is
            // unconditional — admin-driven Current changes need the poll loop on every silo.
            services.TryAddSingleton<DefaultClientVersionService>();
            services.TryAddSingleton<IConfigVersionResolver>(
                sp => sp.GetRequiredService<DefaultClientVersionService>());
            services.AddHostedService(sp => sp.GetRequiredService<DefaultClientVersionService>());

            return services;
        }
    }
}
