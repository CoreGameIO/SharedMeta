using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orleans;

namespace SharedMeta.Auth
{
    /// <summary>
    /// HTTP endpoints for authentication.
    /// </summary>
    public static class AuthEndpoints
    {
        /// <summary>
        /// Map authentication endpoints.
        /// Usage: app.MapMetaAuth("/meta/auth");
        /// </summary>
        public static IEndpointRouteBuilder MapMetaAuth(
            this IEndpointRouteBuilder endpoints,
            string prefix = "/meta/auth")
        {
            endpoints.MapPost($"{prefix}/login", HandleLogin)
                .AllowAnonymous();

            return endpoints;
        }

        private static async Task<IResult> HandleLogin(
            HttpContext ctx,
            IGrainFactory grainFactory,
            JwtTokenService jwtService)
        {
            var request = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
            if (request == null || string.IsNullOrEmpty(request.DeviceId))
                return Results.BadRequest(new { error = "DeviceId is required" });

            var grain = grainFactory.GetGrain<IAuthGrain>(request.DeviceId);
            var result = await grain.LoginAsync();

            var token = jwtService.GenerateToken(result.PlayerId);

            return Results.Ok(new LoginResponse
            {
                Token = token,
                PlayerId = result.PlayerId,
                IsNewPlayer = result.IsNewPlayer,
                ExpiresAt = DateTime.UtcNow + jwtService.Options.TokenLifetime
            });
        }
    }
}
