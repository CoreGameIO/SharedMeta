using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace SharedMeta.Auth
{
    /// <summary>
    /// DI extensions for SharedMeta authentication.
    /// </summary>
    public static class MetaAuthExtensions
    {
        /// <summary>
        /// Add SharedMeta JWT authentication services.
        /// Configures ASP.NET Authentication + Authorization with JWT Bearer scheme.
        /// </summary>
        public static IServiceCollection AddMetaAuth(
            this IServiceCollection services,
            Action<MetaAuthOptions> configure)
        {
            var options = new MetaAuthOptions();
            configure(options);

            if (string.IsNullOrEmpty(options.SecretKey) || options.SecretKey.Length < 32)
                throw new ArgumentException("MetaAuthOptions.SecretKey must be at least 32 characters");

            services.AddSingleton(options);
            services.AddSingleton<JwtTokenService>();

            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
                .AddJwtBearer(jwt =>
                {
                    jwt.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(
                            Encoding.UTF8.GetBytes(options.SecretKey)),
                        ValidateIssuer = !string.IsNullOrEmpty(options.Issuer),
                        ValidIssuer = options.Issuer,
                        ValidateAudience = !string.IsNullOrEmpty(options.Audience),
                        ValidAudience = options.Audience,
                        ValidateLifetime = true
                    };

                    // For SignalR: read token from query string ?access_token=
                    jwt.Events = new JwtBearerEvents
                    {
                        OnMessageReceived = context =>
                        {
                            var accessToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(accessToken))
                            {
                                context.Token = accessToken;
                            }
                            return Task.CompletedTask;
                        }
                    };
                });

            services.AddAuthorization();

            return services;
        }
    }
}
