using System;
using Microsoft.Extensions.DependencyInjection;

namespace SharedMeta.Auth.Apple
{
    public static class AppleAuthExtensions
    {
        /// <summary>
        /// Register Sign in with Apple authentication validator.
        /// <code>
        /// services.AddMetaAuthApple(o => {
        ///     o.BundleId = "com.yourcompany.yourgame";
        /// });
        /// </code>
        /// </summary>
        public static IServiceCollection AddMetaAuthApple(
            this IServiceCollection services, Action<AppleAuthOptions> configure)
        {
            var options = new AppleAuthOptions();
            configure(options);
            services.AddSingleton(options);
            services.AddSingleton<IExternalAuthValidator, AppleAuthValidator>();
            return services;
        }
    }
}
