using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Orleans.Hosting;
using Orleans.Runtime.Hosting;
using SharedMeta.Core;

namespace SharedMeta.Server.Core.Storage
{
    /// <summary>
    /// Extension methods for registering FileGrainStorage.
    /// </summary>
    public static class FileGrainStorageExtensions
    {
        /// <summary>
        /// Configure silo to use file-based grain storage.
        /// </summary>
        /// <param name="builder">The silo builder.</param>
        /// <param name="name">The storage provider name (must match [PersistentState] storageName).</param>
        /// <param name="configureOptions">Configuration delegate.</param>
        public static ISiloBuilder AddFileGrainStorage(
            this ISiloBuilder builder,
            string name,
            Action<FileGrainStorageOptions>? configureOptions = null)
        {
            return builder.ConfigureServices(services =>
                services.AddFileGrainStorage(name, configureOptions));
        }

        /// <summary>
        /// Add file-based grain storage to the service collection.
        /// </summary>
        public static IServiceCollection AddFileGrainStorage(
            this IServiceCollection services,
            string name,
            Action<FileGrainStorageOptions>? configureOptions = null)
        {
            services.AddGrainStorage<FileGrainStorage>(name, (sp, _) =>
            {
                var options = new FileGrainStorageOptions();
                configureOptions?.Invoke(options);

                var serializer = sp.GetRequiredService<IMetaSerializer>();
                var logger = sp.GetRequiredService<ILogger<FileGrainStorage>>();
                return new FileGrainStorage(options, serializer, logger);
            });

            return services;
        }
    }
}
