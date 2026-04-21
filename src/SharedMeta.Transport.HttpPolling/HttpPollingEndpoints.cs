using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using SharedMeta.Core.Transport;
using SharedMeta.Server.Core;
using SharedMeta.Server.Core.Transport;

namespace SharedMeta.Transport.HttpPolling
{
    /// <summary>
    /// Minimal API endpoint registration for HTTP long-polling transport.
    /// All endpoints use X-Connection-Id header for connection identification.
    /// </summary>
    public static class HttpPollingEndpoints
    {
        private const string ConnectionIdHeader = "X-Connection-Id";
        private static readonly TimeSpan DefaultPollTimeout = TimeSpan.FromSeconds(30);
        private static readonly JsonSerializerOptions JsonOptions = MetaJsonContext.Default.Options;

        /// <summary>
        /// Map all HTTP polling endpoints under the given prefix.
        /// Usage: app.MapMetaHttpPolling("/meta-http");
        /// </summary>
        public static IEndpointRouteBuilder MapMetaHttpPolling(
            this IEndpointRouteBuilder endpoints,
            string prefix = "/meta")
        {
            var group = endpoints.MapGroup(prefix);

            group.MapPost("/session-connect", HandleSessionConnect);
            group.MapPost("/subscribe", HandleSubscribe);
            group.MapPost("/unsubscribe", HandleUnsubscribe);
            group.MapPost("/rpc", HandleRpcCall);
            group.MapPost("/query", HandleQueryCall);
            group.MapPost("/signal", HandleSignalCall);
            group.MapPost("/debug-options", HandleSetDebugOptions);
            group.MapPost("/desync-report", HandleSendDesyncReport);
            group.MapPost("/ack", HandleAcknowledge);
            group.MapPost("/poll", HandlePoll);
            group.MapPost("/config-url", HandleConfigDownloadUrl);
            group.MapPost("/disconnect", HandleDisconnect);
            group.MapPost("/graceful-disconnect", HandleGracefulDisconnect);

            return endpoints;
        }

        private static async Task<IResult> HandleSessionConnect(
            HttpContext ctx,
            HttpPollingConnectionManager mgr,
            MetaTransportOptions? transportOptions = null)
        {
            var connectionId = GetConnectionId(ctx);
            if (connectionId == null)
                return Results.BadRequest("Missing X-Connection-Id header");

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.SessionConnectRequest);
            if (request == null)
                return Results.BadRequest("Invalid request body");

            // If authenticated via JWT, use PlayerId from token claims (trusted)
            if (ctx.User?.Identity?.IsAuthenticated == true)
            {
                var claimPlayerId = ctx.User.FindFirst("sub")?.Value
                                    ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(claimPlayerId))
                    return Results.Unauthorized();
                request.PlayerId = claimPlayerId;
            }
            else if (transportOptions?.RequireAuthentication == true)
            {
                return Results.Unauthorized();
            }

            var state = mgr.GetOrCreateConnection(connectionId);
            mgr.Touch(connectionId);

            var response = await state.Handler.SessionConnectAsync(request);
            return Results.Json(response, MetaJsonContext.Default.SessionConnectResponse);
        }

        private static async Task<IResult> HandleSubscribe(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.SubscribeRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            var response = await state.Handler.SubscribeAsync(request);
            return Results.Json(response, MetaJsonContext.Default.SubscribeResponse);
        }

        private static async Task<IResult> HandleUnsubscribe(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.UnsubscribeRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            var response = await state.Handler.UnsubscribeAsync(request);
            return Results.Json(response, MetaJsonContext.Default.UnsubscribeResponse);
        }

        private static async Task<IResult> HandleRpcCall(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.RpcCallRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            var response = await state.Handler.RpcCallAsync(request);
            return Results.Json(response, MetaJsonContext.Default.SessionResponse);
        }

        private static async Task<IResult> HandleQueryCall(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.QueryCallRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            var response = await state.Handler.QueryCallAsync(request);
            return Results.Json(response, MetaJsonContext.Default.QueryCallResponse);
        }

        /// <summary>
        /// Fire-and-forget signal endpoint. Accepts the request, schedules handler work on
        /// the task scheduler, returns <c>202 Accepted</c> immediately — the client is free
        /// to stop waiting the moment the HTTP response lands (well before server execution
        /// completes). Server-side errors are caught inside <c>Handler.SignalCallAsync</c>
        /// and never surfaced back through the HTTP response.
        /// </summary>
        private static async Task<IResult> HandleSignalCall(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.SignalCallRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            // Schedule execution but do not await — HTTP response returns 202 immediately,
            // server-side work continues on the task scheduler. Handler swallows exceptions.
            _ = state.Handler.SignalCallAsync(request);
            return Results.Accepted();
        }

        private static async Task<IResult> HandleSetDebugOptions(
            HttpContext ctx,
            HttpPollingConnectionManager mgr,
            MetaTransportOptions? transportOptions = null)
        {
            if (transportOptions != null && !transportOptions.AllowDebugApi)
                return Results.Json(
                    new DebugOptionsResponse { Success = false, Error = "Debug API disabled" },
                    MetaJsonContext.Default.DebugOptionsResponse);

            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.DebugOptionsRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            var response = await state.Handler.SetDebugOptionsAsync(request);
            return Results.Json(response, MetaJsonContext.Default.DebugOptionsResponse);
        }

        private static async Task<IResult> HandleSendDesyncReport(
            HttpContext ctx,
            HttpPollingConnectionManager mgr,
            MetaTransportOptions? transportOptions = null)
        {
            if (transportOptions?.DesyncReportingEnabled != true)
                return Results.Json(
                    new DesyncReportResponse { Status = "disabled" },
                    MetaJsonContext.Default.DesyncReportResponse);

            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.DesyncReportRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            var response = await state.Handler.SendDesyncReportAsync(request);
            return Results.Json(response, MetaJsonContext.Default.DesyncReportResponse);
        }

        private static async Task<IResult> HandleAcknowledge(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.AcknowledgeRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            var response = await state.Handler.AcknowledgeSequenceAsync(request);
            return Results.Json(response, MetaJsonContext.Default.AcknowledgeResponse);
        }

        private static async Task<IResult> HandlePoll(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            // Read optional poll request body for piggybacked acknowledgment
            PollRequest? pollRequest = null;
            if (ctx.Request.ContentLength is > 0)
            {
                pollRequest = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.PollRequest);
            }

            // Process piggybacked acknowledgment
            if (pollRequest?.LastAcknowledgedSequence > 0)
            {
                await state.Handler.AcknowledgeSequenceAsync(new AcknowledgeRequest
                {
                    SequenceNumber = pollRequest.LastAcknowledgedSequence
                });
            }

            // Long poll: wait for messages or timeout
            var response = await state.BroadcastSender.WaitForMessagesAsync(
                DefaultPollTimeout,
                ctx.RequestAborted);

            return Results.Json(response, MetaJsonContext.Default.PollResponse);
        }

        private static async Task<IResult> HandleConfigDownloadUrl(
            HttpContext ctx,
            HttpPollingConnectionManager mgr,
            IConfigDownloadUrlResolver? configUrlResolver = null)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            var request = await ctx.Request.ReadFromJsonAsync(MetaJsonContext.Default.ConfigDownloadUrlRequest);
            if (request == null) return Results.BadRequest("Invalid request body");

            try
            {
                var url = configUrlResolver?.GetDownloadUrl(request.StateTypeName, new SharedMeta.Core.MetaConfigVersion(request.ConfigMajorVersion, request.ConfigMinorVersion));
                var response = new ConfigDownloadUrlResponse { Success = true, DownloadUrl = url };
                return Results.Json(response, MetaJsonContext.Default.ConfigDownloadUrlResponse);
            }
            catch (Exception ex)
            {
                var response = new ConfigDownloadUrlResponse { Success = false, Error = ex.Message };
                return Results.Json(response, MetaJsonContext.Default.ConfigDownloadUrlResponse);
            }
        }

        private static async Task<IResult> HandleGracefulDisconnect(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var (state, error) = GetExistingConnection(ctx, mgr);
            if (state == null) return error!;

            await state.Handler.GracefulDisconnectAsync();
            var connectionId = GetConnectionId(ctx)!;
            await mgr.RemoveConnectionAsync(connectionId);
            return Results.Ok();
        }

        private static async Task<IResult> HandleDisconnect(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var connectionId = GetConnectionId(ctx);
            if (connectionId == null)
                return Results.BadRequest("Missing X-Connection-Id header");

            await mgr.RemoveConnectionAsync(connectionId);
            return Results.Ok();
        }

        private static string? GetConnectionId(HttpContext ctx)
        {
            return ctx.Request.Headers[ConnectionIdHeader].FirstOrDefault();
        }

        private static (ConnectionState? state, IResult? error) GetExistingConnection(
            HttpContext ctx,
            HttpPollingConnectionManager mgr)
        {
            var connectionId = GetConnectionId(ctx);
            if (connectionId == null)
                return (null, Results.BadRequest("Missing X-Connection-Id header"));

            var state = mgr.GetConnection(connectionId);
            if (state == null)
                return (null, Results.StatusCode(410)); // 410 Gone — connection expired

            mgr.Touch(connectionId);
            return (state, null);
        }
    }
}
