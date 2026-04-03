using System;
using Microsoft.Extensions.DependencyInjection;

namespace SharedMeta.Auth.Google
{
    public static class GoogleAuthExtensions
    {
        /// <summary>
        /// Register Google Play Games authentication validator.
        /// <code>
        /// services.AddMetaAuthGoogle(o => {
        ///     o.ClientId = "your-web-client-id.apps.googleusercontent.com";
        ///     o.ClientSecret = "your-client-secret";
        /// });
        /// </code>
        /// </summary>
        public static IServiceCollection AddMetaAuthGoogle(
            this IServiceCollection services, Action<GoogleAuthOptions> configure)
        {
            var options = new GoogleAuthOptions();
            configure(options);
            services.AddSingleton(options);
            services.AddSingleton<IExternalAuthValidator, GoogleAuthValidator>();
            return services;
        }
    }
}
