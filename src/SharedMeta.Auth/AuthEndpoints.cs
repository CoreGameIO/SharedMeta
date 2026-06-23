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

            // Anonymous: the refresh token itself is the credential.
            endpoints.MapPost($"{prefix}/refresh", HandleRefresh)
                .AllowAnonymous();

            // Anonymous: presenting a valid refresh token proves ownership of that session.
            endpoints.MapPost($"{prefix}/logout", HandleLogout)
                .AllowAnonymous();

            endpoints.MapPost($"{prefix}/link", HandleLink)
                .RequireAuthorization();

            endpoints.MapPost($"{prefix}/unlink", HandleUnlink)
                .RequireAuthorization();

            endpoints.MapPost($"{prefix}/reset-device", HandleResetDevice)
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
            var refreshGrain = grainFactory.GetGrain<IRefreshTokenGrain>(result.PlayerId);
            var refreshToken = await refreshGrain.IssueAsync(request.DeviceId, jwtService.Options.RefreshTokenLifetime);

            return Results.Ok(new LoginResponse
            {
                Token = token,
                PlayerId = result.PlayerId,
                IsNewPlayer = result.IsNewPlayer,
                ExpiresAt = DateTime.UtcNow + jwtService.Options.AccessTokenLifetime,
                RefreshToken = refreshToken,
                RefreshExpiresAt = DateTime.UtcNow + jwtService.Options.RefreshTokenLifetime
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
            var refreshGrain = grainFactory.GetGrain<IRefreshTokenGrain>(result.PlayerId);
            var refreshToken = await refreshGrain.IssueAsync(authKey, jwtService.Options.RefreshTokenLifetime);

            return Results.Ok(new LoginResponse
            {
                Token = token,
                PlayerId = result.PlayerId,
                IsNewPlayer = result.IsNewPlayer,
                ExpiresAt = DateTime.UtcNow + jwtService.Options.AccessTokenLifetime,
                RefreshToken = refreshToken,
                RefreshExpiresAt = DateTime.UtcNow + jwtService.Options.RefreshTokenLifetime
            });
        }

        /// <summary>
        /// Exchange a refresh token for a new access token: POST /meta/auth/refresh { "refreshToken": "..." }
        /// Rotates the refresh token (the presented one becomes invalid). Replay of a used token revokes
        /// the whole session family and returns 401.
        /// </summary>
        private static async Task<IResult> HandleRefresh(
            HttpContext ctx,
            IGrainFactory grainFactory,
            JwtTokenService jwtService)
        {
            var request = await ctx.Request.ReadFromJsonAsync<RefreshRequest>();
            if (request == null || string.IsNullOrEmpty(request.RefreshToken))
                return Results.BadRequest(new { error = "RefreshToken is required" });

            if (!RefreshTokens.TryParse(request.RefreshToken, out var playerId, out _, out _))
                return Results.Unauthorized();

            var refreshGrain = grainFactory.GetGrain<IRefreshTokenGrain>(playerId);
            var rotation = await refreshGrain.RotateAsync(request.RefreshToken, jwtService.Options.RefreshTokenLifetime);

            // Both Invalid and Reused surface as 401 — never reveal which to the caller.
            if (rotation.Outcome != RefreshOutcome.Valid)
                return Results.Unauthorized();

            var accessToken = jwtService.GenerateToken(playerId, AuthTypeOf(rotation.AuthKey));

            return Results.Ok(new LoginResponse
            {
                Token = accessToken,
                PlayerId = playerId,
                IsNewPlayer = false,
                ExpiresAt = DateTime.UtcNow + jwtService.Options.AccessTokenLifetime,
                RefreshToken = rotation.NewRefreshToken!,
                RefreshExpiresAt = rotation.RefreshExpiresAt
            });
        }

        /// <summary>
        /// Revoke the session behind a refresh token: POST /meta/auth/logout { "refreshToken": "..." }
        /// Idempotent — an unknown/expired token still returns success.
        /// </summary>
        private static async Task<IResult> HandleLogout(
            HttpContext ctx,
            IGrainFactory grainFactory)
        {
            var request = await ctx.Request.ReadFromJsonAsync<RefreshRequest>();
            if (request == null || string.IsNullOrEmpty(request.RefreshToken))
                return Results.BadRequest(new { error = "RefreshToken is required" });

            if (RefreshTokens.TryParse(request.RefreshToken, out var playerId, out var familyId, out _))
            {
                var refreshGrain = grainFactory.GetGrain<IRefreshTokenGrain>(playerId);
                await refreshGrain.RevokeFamilyAsync(familyId);
            }

            return Results.Ok(new AuthOperationResponse { Success = true });
        }

        // auth_type claim for a re-issued access token, derived from the credential that owns the
        // session: "google:123" → "google", a bare deviceId → "device".
        private static string AuthTypeOf(string? authKey)
        {
            if (string.IsNullOrEmpty(authKey)) return "device";
            int colon = authKey!.IndexOf(':');
            return colon > 0 ? authKey.Substring(0, colon) : "device";
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

            if (!success)
                return Results.Json(new AuthOperationResponse { Success = false, Error = "Unlink failed" },
                    statusCode: 400);

            // Revoke refresh sessions opened with the unlinked credential.
            await grainFactory.GetGrain<IRefreshTokenGrain>(playerId).RevokeByAuthKeyAsync(request.AuthKey);

            return Results.Ok(new AuthOperationResponse { Success = true });
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

        /// <summary>
        /// Reset device binding: POST /meta/auth/reset-device [Authorize]
        /// { "deviceId": "..." }
        /// Force-unlinks the device from the current player. Next login with this
        /// deviceId will create a new player profile.
        /// </summary>
        private static async Task<IResult> HandleResetDevice(
            HttpContext ctx,
            IGrainFactory grainFactory)
        {
            var playerId = GetPlayerId(ctx);
            if (playerId == null)
                return Results.Unauthorized();

            var request = await ctx.Request.ReadFromJsonAsync<ResetDeviceRequest>();
            if (request == null || string.IsNullOrEmpty(request.DeviceId))
                return Results.BadRequest(new { error = "DeviceId is required" });

            var grain = grainFactory.GetGrain<IAuthGrain>(request.DeviceId);
            var linkedPlayerId = await grain.GetPlayerIdAsync();

            if (linkedPlayerId == null)
                return Results.Json(
                    new AuthOperationResponse { Success = false, Error = "Device is not linked to any player" },
                    statusCode: 404);

            if (linkedPlayerId != playerId)
                return Results.Json(
                    new AuthOperationResponse { Success = false, Error = "Device is linked to a different player" },
                    statusCode: 403);

            var unlinkedId = await grain.ForceUnlinkAsync();
            if (unlinkedId == null)
                return Results.Json(new AuthOperationResponse { Success = false, Error = "Reset failed" },
                    statusCode: 400);

            // Kill any refresh sessions opened with this device so a reset truly logs it out.
            await grainFactory.GetGrain<IRefreshTokenGrain>(playerId).RevokeByAuthKeyAsync(request.DeviceId);

            return Results.Ok(new AuthOperationResponse { Success = true });
        }

        private static string? GetPlayerId(HttpContext ctx)
        {
            return ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? ctx.User.FindFirstValue("sub");
        }
    }
}
