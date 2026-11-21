using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SharedMeta.Core.Diagnostics;

namespace SharedMeta.Server.Core.Telemetry
{
    /// <summary>
    /// Writes call and session events to the <c>SharedMeta.Analytics</c> log category when
    /// <see cref="MetaTelemetryOptions.AnalyticsLog"/> is on — structured records a host routes to
    /// its analytics store (Serilog sink, OTel logs, …) without writing an observer of its own.
    /// </summary>
    public sealed class LoggingServerObserver : IMetaServerObserver
    {
        public const string Category = "SharedMeta.Analytics";

        private static readonly EventId CallCompletedEvent = new(9001, "call_completed");
        private static readonly EventId SessionStartedEvent = new(9002, "session_started");
        private static readonly EventId SessionEndedEvent = new(9003, "session_ended");

        private readonly ILogger _logger;
        private readonly IOptionsMonitor<MetaTelemetryOptions> _options;

        public LoggingServerObserver(ILoggerFactory loggerFactory, IOptionsMonitor<MetaTelemetryOptions> options)
        {
            _logger = loggerFactory.CreateLogger(Category);
            _options = options;
        }

        private bool Enabled => _options.CurrentValue.AnalyticsLog && _logger.IsEnabled(LogLevel.Information);

        public void OnCallCompleted(in MetaCallInfo call)
        {
            if (!Enabled) return;
            _logger.LogInformation(CallCompletedEvent,
                "call_completed {CallId} {Op} {Service}.{Method} mode={Mode} player={PlayerId} entity={EntityId} state={StateType} result={Result} error={ErrorKind} {DurationMs}ms client={ClientVersion} session={SessionId}",
                call.CallId, call.Op, call.Service, call.Method, call.Mode, call.PlayerId, call.EntityId,
                call.StateType, call.Result, call.ErrorKind, call.DurationMs, call.ClientVersion, call.SessionId);
        }

        public void OnSessionStarted(string playerId, string sessionId, string? clientVersion, bool resumed)
        {
            if (!Enabled) return;
            _logger.LogInformation(SessionStartedEvent,
                "session_started player={PlayerId} session={SessionId} client={ClientVersion} resumed={Resumed}",
                playerId, sessionId, clientVersion, resumed);
        }

        public void OnSessionEnded(string playerId, string sessionId, MetaSessionEndReason reason, double durationMs)
        {
            if (!Enabled) return;
            _logger.LogInformation(SessionEndedEvent,
                "session_ended player={PlayerId} session={SessionId} reason={Reason} {DurationMs}ms",
                playerId, sessionId, MetricEvents.Session.ReasonTag(reason), durationMs);
        }
    }
}
