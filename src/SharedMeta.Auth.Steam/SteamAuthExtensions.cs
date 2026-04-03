using System;
using Microsoft.Extensions.DependencyInjection;

namespace SharedMeta.Auth.Steam
{
    public static class SteamAuthExtensions
    {
        /// <summary>
        /// Register Steam authentication validator.
        /// <code>
        /// services.AddMetaAuthSteam(o => {
        ///     o.WebApiKey = "your-publisher-web-api-key";
        ///     o.AppId = 480; // your Steam App ID
        /// });
        /// </code>
        /// </summary>
        public static IServiceCollection AddMetaAuthSteam(
            this IServiceCollection services, Action<SteamAuthOptions> configure)
        {
            var options = new SteamAuthOptions();
            configure(options);
            services.AddSingleton(options);
            services.AddSingleton<IExternalAuthValidator, SteamAuthValidator>();
            return services;
        }
    }
}
