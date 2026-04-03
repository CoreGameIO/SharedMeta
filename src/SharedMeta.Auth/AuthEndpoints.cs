using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
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

            endpoints.MapPost($"{prefix}/login-platform", HandlePlatformLogin)
                .AllowAnonymous();

            endpoints.MapPost($"{prefix}/link", HandleLink)
                .RequireAuthorization();

            endpoints.MapPost($"{prefix}/unlink", HandleUnlink)
                .RequireAuthorization();

            endpoints.MapGet($"{prefix}/keys", HandleGetKeys)
                .RequireAuthorization();

            return endpoints;
        }

        /// <summary>
        /// Device login: POST /meta/auth/login { "deviceId": "..." }
        /// </summary>
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

            var token = jwtService.GenerateToken(result.PlayerId, "device");

            return Results.Ok(new LoginResponse
            {
                Token = token,
                PlayerId = result.PlayerId,
                IsNewPlayer = result.IsNewPlayer,
                ExpiresAt = DateTime.UtcNow + jwtService.Options.TokenLifetime
            });
        }

        /// <summary>
        /// Platform login: POST /meta/auth/login-platform { "platform": "google", "platformToken": "..." }
        /// </summary>
        private static async Task<IResult> HandlePlatformLogin(
            HttpContext ctx,
            IGrainFactory grainFactory,
            JwtTokenService jwtService,
            IEnumerable<IExternalAuthValidator> validators)
        {
            var request = await ctx.Request.ReadFromJsonAsync<PlatformLoginRequest>();
            if (request == null || string.IsNullOrEmpty(request.Platform) || string.IsNullOrEmpty(request.PlatformToken))
                return Results.BadRequest(new { error = "Platform and PlatformToken are required" });

            var validator = validators.FirstOrDefault(v =>
                v.Platform.Equals(request.Platform, StringComparison.OrdinalIgnoreCase));
            if (validator == null)
                return Results.BadRequest(new { error = $"Unsupported platform: {request.Platform}" });

            ExternalAuthResult authResult;
            try
            {
                authResult = await validator.ValidateAsync(request.PlatformToken);
            }
            catch (ExternalAuthException)
            {
                return Results.Unauthorized();
            }

            var authKey = $"{request.Platform.ToLowerInvariant()}:{authResult.PlatformUserId}";
            var grain = grainFactory.GetGrain<IAuthGrain>(authKey);
            var result = await grain.LoginAsync();

            var token = jwtService.GenerateToken(result.PlayerId, request.Platform.ToLowerInvariant());

            return Results.Ok(new LoginResponse
            {
                Token = token,
                PlayerId = result.PlayerId,
                IsNewPlayer = result.IsNewPlayer,
                ExpiresAt = DateTime.UtcNow + jwtService.Options.TokenLifetime
            });
        }

        /// <summary>
        /// Link platform to current account: POST /meta/auth/link [Authorize]
        /// { "platform": "google", "platformToken": "..." }
        /// </summary>
        private static async Task<IResult> HandleLink(
            HttpContext ctx,
            IGrainFactory grainFactory,
            IEnumerable<IExternalAuthValidator> validators)
        {
            var playerId = GetPlayerId(ctx);
            if (playerId == null)
                return Results.Unauthorized();

            var request = await ctx.Request.ReadFromJsonAsync<LinkAccountRequest>();
            if (request == null || string.IsNullOrEmpty(request.Platform) || string.IsNullOrEmpty(request.PlatformToken))
                return Results.BadRequest(new { error = "Platform and PlatformToken are required" });

            var validator = validators.FirstOrDefault(v =>
                v.Platform.Equals(request.Platform, StringComparison.OrdinalIgnoreCase));
            if (validator == null)
                return Results.BadRequest(new { error = $"Unsupported platform: {request.Platform}" });

            ExternalAuthResult authResult;
            try
            {
                authResult = await validator.ValidateAsync(request.PlatformToken);
            }
            catch (ExternalAuthException)
            {
                return Results.Json(new AuthOperationResponse { Success = false, Error = "Platform token validation failed" },
                    statusCode: 401);
            }

            var authKey = $"{request.Platform.ToLowerInvariant()}:{authResult.PlatformUserId}";
            var grain = grainFactory.GetGrain<IAuthGrain>(authKey);
            var linkResult = await grain.LinkAsync(playerId);

            if (!linkResult.Success)
                return Results.Json(new AuthOperationResponse { Success = false, Error = linkResult.Error },
                    statusCode: 409);

            return Results.Ok(new AuthOperationResponse { Success = true });
        }

        /// <summary>
        /// Unlink auth key from current account: POST /meta/auth/unlink [Authorize]
        /// { "authKey": "google:123456" }
        /// </summary>
        private static async Task<IResult> HandleUnlink(
            HttpContext ctx,
            IGrainFactory grainFactory)
        {
            var playerId = GetPlayerId(ctx);
            if (playerId == null)
                return Results.Unauthorized();

            var request = await ctx.Request.ReadFromJsonAsync<UnlinkRequest>();
            if (request == null || string.IsNullOrEmpty(request.AuthKey))
                return Results.BadRequest(new { error = "AuthKey is required" });

            // Safety: check that player has more than one key (can't unlink the last one)
            var index = grainFactory.GetGrain<IAuthIndexGrain>(playerId);
            var keys = await index.GetKeysAsync();

            if (keys.Count <= 1)
                return Results.Json(
                    new AuthOperationResponse { Success = false, Error = "Cannot unlink the last auth key" },
                    statusCode: 400);

            if (!keys.Contains(request.AuthKey))
                return Results.Json(
                    new AuthOperationResponse { Success = false, Error = "Auth key not linked to this player" },
                    statusCode: 404);

            var grain = grainFactory.GetGrain<IAuthGrain>(request.AuthKey);
            var success = await grain.UnlinkAsync(playerId);

            return success
                ? Results.Ok(new AuthOperationResponse { Success = true })
                : Results.Json(new AuthOperationResponse { Success = false, Error = "Unlink failed" },
                    statusCode: 400);
        }

        /// <summary>
        /// Get all auth keys for current player: GET /meta/auth/keys [Authorize]
        /// </summary>
        private static async Task<IResult> HandleGetKeys(
            HttpContext ctx,
            IGrainFactory grainFactory)
        {
            var playerId = GetPlayerId(ctx);
            if (playerId == null)
                return Results.Unauthorized();

            var index = grainFactory.GetGrain<IAuthIndexGrain>(playerId);
            var keys = await index.GetKeysAsync();
            return Results.Ok(keys);
        }

        private static string? GetPlayerId(HttpContext ctx)
        {
            return ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? ctx.User.FindFirstValue("sub");
        }
    }
}
